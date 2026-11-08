using Microsoft.Data.SqlClient;
using System.Text.Json;
using ZappApplyAgentTools;

static class SqlChecks
{
    public static async Task Run(Action<string, bool> check)
    {
        var name = "ZappApplySearchToolsCheck_" + Guid.NewGuid().ToString("N");
        var master = new SqlConnectionStringBuilder(@"Server=(localdb)\MSSQLLocalDB;Integrated Security=true;TrustServerCertificate=true") { InitialCatalog = "master" };
        await using var admin = new SqlConnection(master.ConnectionString); await admin.OpenAsync();
        await using var command = admin.CreateCommand(); command.CommandText = $"CREATE DATABASE [{name}]"; await command.ExecuteNonQueryAsync();
        try
        {
            var connection = new SqlConnectionStringBuilder(master.ConnectionString) { InitialCatalog = name }; var store = new SqlSearchStore(connection.ConnectionString); await store.Initialize(default); await store.Initialize(default);
            await ControlledChecks.Run(check, store);
            var options = new SearchOptions { ControlKey = new string('s', 40), McpKey = new string('t', 40), TargetCount = 1 };
            var service = new SearchService(store, new FakeReader(), new FakePolicy(), options, TimeProvider.System);
            var id = Guid.NewGuid(); var input = JsonSerializer.SerializeToElement(new { schemaVersion = 1, jobId = id, jobType = "AI_Search", ownerId = "fictional-owner", payload = new { } });
            var session = await service.Begin(input, default); var source = await service.ReadPosting(session.Handle, "https://employer.example/jobs/sql", default);
            var restarted = new SqlSearchStore(connection.ConnectionString);
            check("SQL state survives store recreation", (await restarted.Read(id, default))?.OwnerId == "fictional-owner" && await restarted.FindHandle(Wire.Hash(session.Handle), default) == id);
            check("SQL evidence survives store recreation and stays search-scoped", (await restarted.Source(source.Id, id, default))?.Hash == source.Hash && await restarted.Source(source.Id, Guid.NewGuid(), default) is null);
            var posting = new SourcePosting { Url = source.Url, Title = "Engineer", Company = "Fixture Employer", Description = "Engineer develops systems. Salary 175000. Remote US.", Source = "Fictional" };
            await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => new SearchService(new SqlSearchStore(connection.ConnectionString), new FakeReader(), new FakePolicy(), options, TimeProvider.System).Submit(session.Handle, [new(source.Id, posting)], default)));
            check("SQL concurrent submissions preserve one accepted identity", (await store.Read(id, default))!.Accepted.Count == 1);
            var complete = await service.Complete(id, "fictional-owner", "agent_stopped", default);
            check("SQL completion survives replay", (await new SearchService(restarted, new FakeReader(), new FakePolicy(), options, TimeProvider.System).Complete(id, "fictional-owner", "provider_failed", default)).Content == complete.Content);
        }
        finally { SqlConnection.ClearAllPools(); command.CommandText = $"ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{name}]"; await command.ExecuteNonQueryAsync(); }
    }
}
