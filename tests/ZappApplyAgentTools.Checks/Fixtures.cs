using System.Text.Json;
using ZappApplyAgentTools;

sealed class MemoryStore : ISearchStore
{
    readonly object gate = new(); readonly Dictionary<Guid, string> states = []; readonly Dictionary<Guid, SourceRecord> sources = [];
    public int Count { get { lock (gate) return states.Count; } }
    public Task<SearchState?> Read(Guid id, CancellationToken ct) { lock (gate) return Task.FromResult(states.TryGetValue(id, out var data) ? JsonSerializer.Deserialize<SearchState>(data, Wire.Json) : null); }
    public Task<Guid?> FindHandle(string hash, CancellationToken ct) { lock (gate) { foreach (var (id, data) in states) if (JsonSerializer.Deserialize<SearchState>(data, Wire.Json)!.HandleHash == hash) return Task.FromResult<Guid?>(id); return Task.FromResult<Guid?>(null); } }
    public Task<T> Edit<T>(Guid id, Func<SearchState?, (SearchState State, T Result)> edit, CancellationToken ct) { lock (gate) { var change = edit(states.TryGetValue(id, out var data) ? JsonSerializer.Deserialize<SearchState>(data, Wire.Json) : null); states[id] = JsonSerializer.Serialize(change.State, Wire.Json); return Task.FromResult(change.Result); } }
    public Task SaveSource(SourceRecord source, CancellationToken ct) { lock (gate) sources[source.Id] = source; return Task.CompletedTask; }
    public Task<SourceRecord?> Source(Guid id, Guid jobId, CancellationToken ct) { lock (gate) return Task.FromResult(sources.TryGetValue(id, out var source) && source.JobId == jobId ? source : null); }
}
sealed class FakeReader : IPostingReader
{ public Task<(string Url, string Text)> Read(string url, CancellationToken ct) => Task.FromResult((url, "Fixture Employer\nEngineer\nEngineer develops systems. Salary 175000. Remote US.")); }
sealed class FakePolicy : IApplicationPolicy
{
    public bool Reject; public int Calls;
    public Task<PolicyResult> Evaluate(Guid jobId, string owner, JsonElement posting, CancellationToken ct) { Interlocked.Increment(ref Calls); return Task.FromResult(new PolicyResult(!Reject, posting.GetProperty("url").GetString()!, Reject ? ["User criterion did not match."] : [])); }
}
sealed class FakeClock : TimeProvider
{ DateTimeOffset now = DateTimeOffset.UtcNow; public override DateTimeOffset GetUtcNow() => now; public void Advance(TimeSpan duration) => now += duration; }
