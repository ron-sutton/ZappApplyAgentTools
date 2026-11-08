using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace ZappApplyAgentTools;

public sealed class SqlSearchStore(string connectionString) : ISearchStore
{
    private async Task<SqlConnection> Open(CancellationToken ct) { var db = new SqlConnection(connectionString); await db.OpenAsync(ct); return db; }
    public async Task Initialize(CancellationToken ct)
    {
        await using var db = await Open(ct); await using var command = db.CreateCommand(); command.CommandTimeout = 30;
        command.CommandText = """
          IF OBJECT_ID(N'dbo.SearchToolRuns') IS NULL CREATE TABLE dbo.SearchToolRuns
          (JobId uniqueidentifier NOT NULL PRIMARY KEY, OwnerId nvarchar(450) NOT NULL, HandleHash varchar(64) NOT NULL UNIQUE, StateJson nvarchar(max) NOT NULL, UpdatedAt datetimeoffset NOT NULL);
          IF OBJECT_ID(N'dbo.SearchToolSources') IS NULL CREATE TABLE dbo.SearchToolSources
          (Id uniqueidentifier NOT NULL PRIMARY KEY, JobId uniqueidentifier NOT NULL, SourceJson nvarchar(max) NOT NULL,
           CONSTRAINT FK_SearchToolSources_Run FOREIGN KEY(JobId) REFERENCES dbo.SearchToolRuns(JobId));
          IF NOT EXISTS(SELECT 1 FROM sys.indexes WHERE name=N'IX_SearchToolSources_JobId' AND object_id=OBJECT_ID(N'dbo.SearchToolSources'))
          CREATE INDEX IX_SearchToolSources_JobId ON dbo.SearchToolSources(JobId);
          """;
        await command.ExecuteNonQueryAsync(ct);
    }
    public async Task<Guid?> FindHandle(string hash, CancellationToken ct)
    {
        await using var db = await Open(ct); await using var cmd = db.CreateCommand(); cmd.CommandTimeout = 15;
        cmd.CommandText = "SELECT JobId FROM dbo.SearchToolRuns WHERE HandleHash=@hash"; cmd.Parameters.AddWithValue("@hash", hash);
        return await cmd.ExecuteScalarAsync(ct) is Guid id ? id : null;
    }
    public async Task<SearchState?> Read(Guid id, CancellationToken ct)
    {
        await using var db = await Open(ct); await using var cmd = db.CreateCommand(); cmd.CommandTimeout = 15;
        cmd.CommandText = "SELECT StateJson FROM dbo.SearchToolRuns WHERE JobId=@id"; cmd.Parameters.AddWithValue("@id", id);
        return await cmd.ExecuteScalarAsync(ct) is string value ? JsonSerializer.Deserialize<SearchState>(value, Wire.Json) : null;
    }
    public async Task<T> Edit<T>(Guid id, Func<SearchState?, (SearchState State, T Result)> edit, CancellationToken ct)
    {
        await using var db = await Open(ct); await using var tx = (SqlTransaction)await db.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        await using var cmd = db.CreateCommand(); cmd.Transaction = tx; cmd.CommandTimeout = 15;
        cmd.Parameters.AddWithValue("@id", id); cmd.CommandText = "SELECT StateJson FROM dbo.SearchToolRuns WITH(UPDLOCK,HOLDLOCK) WHERE JobId=@id";
        var before = await cmd.ExecuteScalarAsync(ct) as string;
        var change = edit(before is null ? null : JsonSerializer.Deserialize<SearchState>(before, Wire.Json));
        cmd.Parameters.Add("@state", SqlDbType.NVarChar, -1).Value = JsonSerializer.Serialize(change.State, Wire.Json);
        cmd.Parameters.AddWithValue("@owner", change.State.OwnerId);
        cmd.Parameters.AddWithValue("@handle", change.State.HandleHash);
        cmd.CommandText = before is null ? "INSERT dbo.SearchToolRuns(JobId,OwnerId,HandleHash,StateJson,UpdatedAt) VALUES(@id,@owner,@handle,@state,SYSUTCDATETIME())" : "UPDATE dbo.SearchToolRuns SET StateJson=@state,UpdatedAt=SYSUTCDATETIME() WHERE JobId=@id AND OwnerId=@owner";
        if (await cmd.ExecuteNonQueryAsync(ct) != 1) throw new IOException("Search state could not be saved.");
        await tx.CommitAsync(ct); return change.Result;
    }
    public async Task SaveSource(SourceRecord source, CancellationToken ct)
    {
        await using var db = await Open(ct); await using var cmd = db.CreateCommand(); cmd.CommandTimeout = 15;
        cmd.CommandText = "INSERT dbo.SearchToolSources(Id,JobId,SourceJson) VALUES(@id,@job,@json)";
        cmd.Parameters.AddWithValue("@id", source.Id); cmd.Parameters.AddWithValue("@job", source.JobId);
        cmd.Parameters.Add("@json", SqlDbType.NVarChar, -1).Value = JsonSerializer.Serialize(source, Wire.Json); await cmd.ExecuteNonQueryAsync(ct);
    }
    public async Task<SourceRecord?> Source(Guid id, Guid jobId, CancellationToken ct)
    {
        await using var db = await Open(ct); await using var cmd = db.CreateCommand(); cmd.CommandTimeout = 15;
        cmd.CommandText = "SELECT SourceJson FROM dbo.SearchToolSources WHERE Id=@id AND JobId=@job";
        cmd.Parameters.AddWithValue("@id", id); cmd.Parameters.AddWithValue("@job", jobId);
        return await cmd.ExecuteScalarAsync(ct) is string value ? JsonSerializer.Deserialize<SourceRecord>(value, Wire.Json) : null;
    }
}
