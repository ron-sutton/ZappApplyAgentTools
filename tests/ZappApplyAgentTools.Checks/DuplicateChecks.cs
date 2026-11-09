using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using ZappApplyAgentTools;

static class DuplicateChecks
{
    public static async Task Run(Action<string, bool> check)
    {
        var store = new MemoryStore(); var reader = new CountingReader(); var policy = new EarlyPolicy();
        var service = new SearchService(store, reader, policy,
            new SearchOptions { ControlKey = new string('c', 40), McpKey = new string('m', 40) }, TimeProvider.System);
        var id = Guid.NewGuid();
        var admission = await service.Begin(JsonSerializer.SerializeToElement(new {
            schemaVersion = 1, jobId = id, jobType = "AI_Search", ownerId = "fixture-owner",
            payload = new { searchToolsVersion = 2 }
        }), default);
        await service.Control(id, new("fixture-owner"), default);
        await service.UpdateWorker(admission.Handle, "round-0-worker-1", "started", default);
        var tools = new SearchTools(service);
        policy.Result = new(true);
        var known = await tools.ReadPosting(admission.Handle, "https://fixture.example/jobs/old", default, "round-0-worker-1");
        check("Known posting returns a normal skip result without downloading", known.AlreadyKnown && known.SourceId == Guid.Empty && reader.Calls == 0);
        check("History and contact data are not exposed in a duplicate result", known.Text == "" && known.Message!.Contains("different opening"));
        check("Early check uses authorized search owner", policy.Owner == "fixture-owner" && policy.JobId == id);
        check("Skipped read releases worker activity", (await store.Read(id, default))!.Workers.Single(w => w.Id == "round-0-worker-1").ActiveCalls == 0);
        policy.Result = new(false);
        var source = await tools.ReadPosting(admission.Handle, "https://fixture.example/jobs/new", default, "round-0-worker-1");
        check("New opening is fetched and retained normally", !source.AlreadyKnown && reader.Calls == 1 && await store.Source(source.SourceId, id, default) is not null);
        policy.Fail = true;
        try { await service.ReadPosting(admission.Handle, "https://fixture.example/jobs/unavailable", default, "round-0-worker-1"); throw new Exception("Expected failure"); }
        catch (ToolFailure) { check("Unavailable preflight does not silently bypass history", reader.Calls == 1); }

        var handler = new PolicyHandler();
        var client = new PolicyClient(new HttpClient(handler) { BaseAddress = new Uri("https://app.example/") },
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["App:PolicyKey"] = "fictional-key" }).Build());
        check("Policy HTTP client returns duplicate verdict", (await client.CheckDuplicate(id, "fixture-owner", "https://fixture.example/jobs/old", default)).Duplicate);
        check("Policy request authenticates and carries only owner/run/URL", handler.Valid);
    }
    sealed class CountingReader : IPostingReader
    {
        public int Calls;
        public Task<(string Url, string Text)> Read(string url, CancellationToken ct)
        { Calls++; return Task.FromResult((url, "Fixture Employer Engineer. Documented professional experience.")); }
    }
    sealed class EarlyPolicy : IApplicationPolicy
    {
        public DuplicateCheckResult Result = new(false); public bool Fail;
        public string? Owner; public Guid JobId;
        public Task<DuplicateCheckResult> CheckDuplicate(Guid id, string owner, string url, CancellationToken ct)
        { Owner = owner; JobId = id; if (Fail) throw new ToolFailure("Unavailable"); return Task.FromResult(Result); }
        public Task<PolicyResult> Evaluate(Guid id, string owner, JsonElement posting, CancellationToken ct) => throw new Exception("Read must not perform criteria evaluation");
    }
    sealed class PolicyHandler : HttpMessageHandler
    {
        public bool Valid;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            using var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            Valid = request.RequestUri!.AbsolutePath == "/api/internal/search-tools/duplicate"
                && request.Headers.Authorization?.Parameter == "fictional-key"
                && payload.RootElement.EnumerateObject().Count() == 3;
            return new(HttpStatusCode.OK) { Content = new StringContent("{\"duplicate\":true}", System.Text.Encoding.UTF8, "application/json") };
        }
    }
}
