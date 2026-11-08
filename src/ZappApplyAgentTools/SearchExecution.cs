using System.Text.Json;

namespace ZappApplyAgentTools;

public sealed class SearchWorker
{
    public string Id { get; set; } = "";
    public int Round { get; set; }
    public string Status { get; set; } = "pending";
    public List<string> Sources { get; set; } = [];
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public int ActiveCalls { get; set; }
}
public sealed record SourceAttempt(string WorkerId, string Url, string Domain, string Outcome, DateTimeOffset At);
public sealed class SearchRound
{
    public int Sequence { get; set; }
    public Guid ExecutionId { get; set; }
    public JsonElement Input { get; set; }
    public bool ProviderCompleted { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}
public sealed record RoundDecision(string Decision, Guid ExecutionId, int Sequence, JsonElement Input, string? Content, Progress Progress);
public sealed record SearchControlRequest(string OwnerId, int? CompletedRound = null, string Outcome = "completed");

public sealed partial class SearchService
{
    private static Guid ExecutionId(Guid jobId, int round)
    {
        // Separate provider turns, one stable logical app job. No UUID or owner is sent to the model.
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"search-turn-v2:{jobId:N}:{round}"));
        return new Guid(bytes.AsSpan(0, 16));
    }
    public async Task<RoundDecision> Control(Guid jobId, SearchControlRequest request, CancellationToken ct)
    {
        if (request.Outcome is not ("completed" or "provider_failed") || request.CompletedRound is < 0) throw new ToolFailure("Invalid provider outcome.");
        var cancelled = await policy.IsCancelled(jobId, request.OwnerId, ct);
        return await store.Edit(jobId, state =>
        {
            if (state is null || state.OwnerId != request.OwnerId || state.ProtocolVersion != 2) throw new ToolFailure("Search control is unavailable.");
            var now = clock.GetUtcNow();
            if (state.Status == "running" && request.CompletedRound.HasValue && state.CurrentRound is { } current)
            {
                if (request.CompletedRound > current.Sequence) throw new ToolFailure("Invalid provider round.");
                if (request.CompletedRound == current.Sequence)
                {
                    current.ProviderCompleted = true;
                    current.CompletedAt ??= now;
                    if (request.Outcome == "provider_failed") { state.Status = "partial"; state.FinishReason = "provider_failed"; }
                }
                // Replayed notification for an older round never advances the new round twice.
            }
            if (state.Status == "running")
            {
                if (cancelled) { state.Status = "partial"; state.FinishReason = "cancelled"; }
                else if (state.Accepted.Count >= state.Target) { state.Status = "completed"; state.FinishReason = "target_reached"; }
                else if (now >= state.Deadline || state.ToolCalls >= state.MaxToolCalls) { state.Status = "partial"; state.FinishReason = "budget_exhausted"; }
            }
            if (state.Status != "running") return (state, Decision(state, "final"));
            if (state.CurrentRound is { ProviderCompleted: true } previous)
            {
                var workers = state.Workers.Where(w => w.Round == previous.Sequence).ToList();
                // A completed parent turn is not proof that its background children have stopped.
                if (workers.Any(w => w.Status is "running" or "finishing") || workers.All(w => w.Status == "pending") && now < previous.CompletedAt!.Value.AddSeconds(20))
                    return (state, Decision(state, "wait"));
                foreach (var worker in workers.Where(w => w.Status == "pending")) worker.Status = "not_started";
                if (previous.Sequence + 1 >= state.MaxRounds) { state.Status = "partial"; state.FinishReason = "continuation_budget_exhausted"; return (state, Decision(state, "final")); }
                state.CurrentRound = null;
            }
            if (state.CurrentRound is null)
            {
                var sequence = state.RoundCount++;
                var pendingSources = state.StartingSources.Where(domain => !state.SourceAttempts.Any(a => DomainMatches(a.Domain, domain))).ToList();
                var assignments = Enumerable.Range(0, state.WorkerCount).Select(index => new SearchWorker
                {
                    Id = $"round-{sequence}-worker-{index + 1}", Round = sequence,
                    Sources = pendingSources.Where((_, i) => i % state.WorkerCount == index).ToList()
                }).ToList();
                state.Workers.AddRange(assignments);
                var input = JsonSerializer.SerializeToElement(new
                {
                    protocolVersion = 2, round = sequence, workerCount = state.WorkerCount,
                    assignments = assignments.Select(w => new { workerId = w.Id, startingSources = w.Sources }),
                    progress = ToProgress(state), acceptedJobs = state.Accepted.Select(x => new { url = x.Posting.GetProperty("url").GetString() })
                }, Wire.Json);
                state.CurrentRound = new() { Sequence = sequence, ExecutionId = ExecutionId(jobId, sequence), Input = JsonSerializer.SerializeToElement(input, Wire.Json) };
            }
            return (state, Decision(state, "run"));
        }, ct);
    }
    private static RoundDecision Decision(SearchState state, string decision) => new(decision,
        state.CurrentRound?.ExecutionId ?? Guid.Empty, state.CurrentRound?.Sequence ?? -1,
        state.CurrentRound?.Input ?? JsonSerializer.SerializeToElement(new { }),
        decision == "final" ? FinalContent(state) : null, ToProgress(state));
    public async Task<Progress> UpdateWorker(string handle, string workerId, string status, CancellationToken ct)
    {
        if (status is not ("started" or "completed")) throw new ToolFailure("Worker status must be started or completed.");
        var state = await Authorized(handle, false, ct);
        return await store.Edit(state.JobId, current =>
        {
            if (current is null || current.ProtocolVersion != 2 || current.Status != "running") throw new ToolFailure("Search worker is unavailable.");
            var worker = current.Workers.SingleOrDefault(w => w.Id == workerId && w.Round == current.CurrentRound?.Sequence) ?? throw new ToolFailure("Unknown or stale worker assignment.");
            if (worker.Status == "completed") return (current, ToProgress(current));
            if (status == "started")
            {
                if (worker.Status is not ("pending" or "running")) throw new ToolFailure("This worker is already stopping.");
                worker.Status = "running"; worker.StartedAt ??= clock.GetUtcNow();
            }
            else
            {
                if (worker.Status is not ("running" or "finishing")) throw new ToolFailure("Start this worker assignment before completing it.");
                worker.Status = worker.ActiveCalls > 0 ? "finishing" : "completed";
                if (worker.Status == "completed") worker.FinishedAt = clock.GetUtcNow();
            }
            return (current, ToProgress(current));
        }, ct);
    }
    private static void CheckWorker(SearchState state, string? workerId)
    {
        if (state.ProtocolVersion == 2 && !state.Workers.Any(w => w.Id == workerId && w.Round == state.CurrentRound?.Sequence && w.Status == "running"))
            throw new ToolFailure("Start a current worker assignment before reading or submitting. Supply its workerId.");
    }
    private async Task ReleaseCall(SearchState state, string? workerId)
    {
        if (state.ProtocolVersion != 2) return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await store.Edit(state.JobId, current =>
        {
            if (current is null) throw new ToolFailure("Search is unavailable.");
            var worker = current.Workers.Single(w => w.Id == workerId);
            worker.ActiveCalls = Math.Max(0, worker.ActiveCalls - 1);
            if (worker.ActiveCalls == 0 && worker.Status == "finishing") { worker.Status = "completed"; worker.FinishedAt = clock.GetUtcNow(); }
            return (current, 0);
        }, timeout.Token);
    }
    private async Task RecordAttempt(SearchState state, string? workerId, string url, string outcome, CancellationToken ct)
    {
        if (state.ProtocolVersion != 2) return;
        var domain = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : "";
        await store.Edit(state.JobId, current =>
        {
            if (current is null) throw new ToolFailure("Search is unavailable.");
            current.SourceAttempts.Add(new(workerId!, url.Length <= 2048 ? url : "", domain, outcome, clock.GetUtcNow()));
            return (current, 0);
        }, ct);
    }
    internal static bool DomainMatches(string host, string source) => host.Equals(source, StringComparison.OrdinalIgnoreCase) || host.EndsWith("." + source, StringComparison.OrdinalIgnoreCase);
    private static string FinalContent(SearchState state)
    {
        var pending = state.StartingSources.Where(domain => !state.SourceAttempts.Any(a => DomainMatches(a.Domain, domain))).ToArray();
        var warnings = state.Status == "completed" ? Array.Empty<string>() : new[]
        {
            $"Partial search: {state.Accepted.Count} of {state.Target} jobs accepted; {state.FinishReason}. Saved jobs were retained. {pending.Length} starting sources have no recorded page-access attempt. This does not establish that no other matching jobs exist."
        };
        return JsonSerializer.Serialize(new { jobs = state.Accepted.Select(x => x.Posting), warnings }, Wire.Json);
    }
}
