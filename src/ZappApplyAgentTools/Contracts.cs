using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ZappApplyAgentTools;

public static class Wire
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    public static string Normalize(string value) => System.Text.RegularExpressions.Regex.Replace(value, @"\s+", " ").Trim();
}
public sealed class SearchOptions
{
    public string ControlKey { get; set; } = "";
    public string McpKey { get; set; } = "";
    public int TargetCount { get; set; } = 10;
    public int MaxToolCalls { get; set; } = 100;
    public int MaxMinutes { get; set; } = 30;
    public void Validate()
    {
        if (ControlKey.Length < 32 || McpKey.Length < 32 || ControlKey == McpKey)
            throw new InvalidOperationException("Configure distinct Search:ControlKey and Search:McpKey of at least 32 characters.");
        if (TargetCount is < 1 or > 10 || MaxToolCalls is < 10 or > 500 || MaxMinutes is < 1 or > 60)
            throw new InvalidOperationException("Invalid search limits.");
    }
}
public sealed class SearchState
{
    public Guid JobId { get; set; }
    public string OwnerId { get; set; } = "";
    public string RequestHash { get; set; } = "";
    public string HandleHash { get; set; } = "";
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset Deadline { get; set; }
    public int Target { get; set; }
    public int MaxToolCalls { get; set; }
    public int ToolCalls { get; set; }
    public string Status { get; set; } = "running";
    public string? FinishReason { get; set; }
    public List<AcceptedCandidate> Accepted { get; set; } = [];
    public List<RejectedCandidate> Rejected { get; set; } = [];
}
public sealed record AcceptedCandidate(string Identity, JsonElement Posting, Guid SourceId);
public sealed record RejectedCandidate(string Url, string[] Reasons);
public sealed record SourceRecord(Guid Id, Guid JobId, string Url, string Text, string Hash, DateTimeOffset FetchedAt);
public sealed record ReadPostingResult(Guid SourceId, string Url, string Text, string Hash, DateTimeOffset FetchedAt);
public sealed record CandidateSubmission(Guid SourceId, SourcePosting Posting);
public sealed record PolicyResult(bool Accepted, string Identity, string[] Reasons, bool Cancelled = false);
public sealed record Progress(string Status, int AcceptedCount, int TargetCount, int Remaining, int ToolCalls, int MaxToolCalls,
    DateTimeOffset Deadline, string? FinishReason, IReadOnlyList<RejectedCandidate> Rejections);
public sealed record BeginResponse(string Handle, Progress Progress);
public sealed record FinalResponse(string Content, Progress Progress);
public sealed class ToolFailure(string message) : Exception(message);

public interface ISearchStore
{
    Task<SearchState?> Read(Guid id, CancellationToken ct);
    Task<Guid?> FindHandle(string hash, CancellationToken ct);
    Task<T> Edit<T>(Guid id, Func<SearchState?, (SearchState State, T Result)> edit, CancellationToken ct);
    Task SaveSource(SourceRecord source, CancellationToken ct);
    Task<SourceRecord?> Source(Guid id, Guid jobId, CancellationToken ct);
}
public interface IPostingReader { Task<(string Url, string Text)> Read(string url, CancellationToken ct); }
public interface IApplicationPolicy { Task<PolicyResult> Evaluate(Guid jobId, string ownerId, JsonElement posting, CancellationToken ct); }
