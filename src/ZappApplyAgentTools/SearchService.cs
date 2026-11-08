using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ZappApplyAgentTools;

public sealed class SearchService(ISearchStore store, IPostingReader reader, IApplicationPolicy policy, SearchOptions options, TimeProvider clock)
{
    public async Task<BeginResponse> Begin(JsonElement request, CancellationToken ct)
    {
        if (request.ValueKind != JsonValueKind.Object || request.GetRawText().Length > 900_000
            || !request.TryGetProperty("schemaVersion", out var version) || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var versionNumber) || versionNumber != 1
            || !request.TryGetProperty("jobType", out var type) || type.ValueKind != JsonValueKind.String || type.GetString() != "AI_Search"
            || !request.TryGetProperty("jobId", out var identity) || identity.ValueKind != JsonValueKind.String || !identity.TryGetGuid(out var id)
            || !request.TryGetProperty("ownerId", out var ownerValue) || ownerValue.ValueKind != JsonValueKind.String
            || !request.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object)
            throw new ToolFailure("Unsupported search request.");
        var owner = ownerValue.GetString();
        if (id == Guid.Empty || string.IsNullOrWhiteSpace(owner) || owner.Length > 450) throw new ToolFailure("Invalid search identity.");
        var hash = Wire.Hash(request.GetRawText());
        var signature = Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(options.ControlKey), Encoding.UTF8.GetBytes($"search-tool-v1:{id:N}:{owner}:{hash}")));
        var handle = signature;
        var now = clock.GetUtcNow();
        var state = await store.Edit(id, existing =>
        {
            if (existing is not null && (existing.OwnerId != owner || existing.RequestHash != hash)) throw new ToolFailure("Search identity conflict.");
            var saved = existing ?? new SearchState
            {
                JobId = id,
                OwnerId = owner,
                RequestHash = hash,
                HandleHash = Wire.Hash(handle),
                StartedAt = now,
                Deadline = now.AddMinutes(options.MaxMinutes),
                Target = options.TargetCount,
                MaxToolCalls = options.MaxToolCalls
            };
            return (saved, saved);
        }, ct);
        return new(handle, ToProgress(state));
    }
    private async Task<SearchState> Authorized(string handle, bool spend, CancellationToken ct)
    {
        if (handle is null || handle.Length != 64 || handle.Any(c => !char.IsAsciiHexDigit(c))) throw new ToolFailure("Invalid search handle.");
        var id = await store.FindHandle(Wire.Hash(handle), ct) ?? throw new ToolFailure("Search handle is unavailable.");
        if (!spend)
        {
            var saved = await store.Read(id, ct);
            if (saved is null || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(saved.HandleHash), Encoding.UTF8.GetBytes(Wire.Hash(handle))))
                throw new ToolFailure("Search handle is unavailable.");
            return saved;
        }
        return await store.Edit(id, state =>
        {
            if (state is null || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(state.HandleHash), Encoding.UTF8.GetBytes(Wire.Hash(handle))))
                throw new ToolFailure("Search handle is unavailable.");
            if (spend)
            {
                if (state.Status != "running") throw new ToolFailure("This search is already finalized.");
                if (clock.GetUtcNow() >= state.Deadline || state.ToolCalls >= state.MaxToolCalls) throw new ToolFailure("Search budget exhausted. Call finish_search to retain partial results.");
                state.ToolCalls++;
            }
            return (state, state);
        }, ct);
    }
    public async Task<Progress> GetProgress(string handle, CancellationToken ct) => ToProgress(await Authorized(handle, false, ct));
    public async Task<SourceRecord> ReadPosting(string handle, string url, CancellationToken ct)
    {
        var state = await Authorized(handle, true, ct);
        (string Url, string Text) result;
        try { result = await reader.Read(url, ct); }
        catch (HttpRequestException) { throw new ToolFailure("Posting could not be reached. Try another public source for the same opening."); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new ToolFailure("Posting read timed out. Try another public source for the same opening."); }
        if (result.Text.Length is < 40 or > 150_000) throw new ToolFailure("The public page did not provide a usable posting body. Try another source for this opening.");
        var source = new SourceRecord(Guid.NewGuid(), state.JobId, result.Url, result.Text, Wire.Hash(result.Text), clock.GetUtcNow());
        await store.SaveSource(source, ct);
        return source;
    }
    public async Task<Progress> Submit(string handle, CandidateSubmission[] candidates, CancellationToken ct)
    {
        if (candidates is null || candidates.Length is < 1 or > 10) throw new ToolFailure("Submit between 1 and 10 candidates.");
        var state = await Authorized(handle, true, ct);
        foreach (var candidate in candidates)
        {
            if (candidate?.Posting is null) throw new ToolFailure("Each candidate needs sourceId and a posting object.");
            var reasons = new List<string>(); var posting = JsonSerializer.SerializeToElement(candidate.Posting, Wire.Json);
            string Text(string key) => posting.ValueKind == JsonValueKind.Object && posting.TryGetProperty(key, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString()! : "";
            var validation = new List<System.ComponentModel.DataAnnotations.ValidationResult>();
            if (!System.ComponentModel.DataAnnotations.Validator.TryValidateObject(candidate.Posting, new(candidate.Posting), validation, true)) reasons.AddRange(validation.Select(v => v.ErrorMessage ?? "Invalid candidate field."));
            if (candidate.Posting.SalaryMin.HasValue && candidate.Posting.SalaryMax < candidate.Posting.SalaryMin) reasons.Add("Salary range is invalid.");
            if ((candidate.Posting.EligibleCountries is null || candidate.Posting.EligibleCountries.Any(c => c is null || c.Length != 2 || c.Any(ch => ch is < 'A' or > 'Z')))) reasons.Add("Eligible countries must be uppercase ISO two-letter codes.");
            var url = Text("url");
            var source = await store.Source(candidate.SourceId, state.JobId, ct);
            if (source is null) reasons.Add("Read this posting with read_posting first; its source must belong to this search.");
            if (posting.GetRawText().Length > 150_000) reasons.Add("Candidate is too large.");
            foreach (var field in new[] { "title", "company", "description", "source" })
                if (string.IsNullOrWhiteSpace(Text(field))) reasons.Add("Missing " + field + ".");
            if (source is not null)
            {
                if (!SameUrl(url, source.Url)) reasons.Add("Use the exact retrieved posting URL.");
                var evidence = Wire.Normalize(source.Text);
                foreach (var field in new[] { "title", "company", "description" })
                    if (!string.IsNullOrWhiteSpace(Text(field)) && !evidence.Contains(Wire.Normalize(Text(field)), StringComparison.OrdinalIgnoreCase))
                        reasons.Add(field + " must be supported by an exact passage from the retrieved source; do not invent or summarize it.");
                foreach (var field in new[] { "salaryMin", "salaryMax" })
                    if (posting.TryGetProperty(field, out var value) && value.ValueKind != JsonValueKind.Null)
                    {
                        if (!value.TryGetDecimal(out var amount) || amount < 0 || !evidence.Replace(",", "").Contains(amount.ToString("0.################", CultureInfo.InvariantCulture), StringComparison.Ordinal))
                            reasons.Add(field + " is not supported by the source; use null for unknown salary.");
                    }
            }
            PolicyResult? verdict = null;
            if (reasons.Count == 0)
            {
                verdict = await policy.Evaluate(state.JobId, state.OwnerId, posting, ct);
                if (verdict.Cancelled) throw new ToolFailure("This search was cancelled by its owner.");
                if (!verdict.Accepted) reasons.AddRange(verdict.Reasons);
            }
            await store.Edit(state.JobId, current =>
            {
                if (current is null || current.OwnerId != state.OwnerId) throw new ToolFailure("Search is unavailable.");
                if (current.Status != "running" || clock.GetUtcNow() >= current.Deadline) throw new ToolFailure("Search is no longer accepting candidates.");
                if (verdict is not null && current.Accepted.Any(x => x.Identity == verdict.Identity)) reasons.Add("This opening was already accepted in this search. Find a different opening.");
                if (reasons.Count == 0 && current.Accepted.Count < current.Target)
                    current.Accepted.Add(new(verdict!.Identity, posting.Clone(), candidate.SourceId));
                else if (reasons.Count > 0)
                {
                    current.Rejected.Add(new(url.Length <= 2048 ? url : "", reasons.Distinct().Take(20).ToArray()));
                    if (current.Rejected.Count > 100) current.Rejected.RemoveAt(0);
                }
                return (current, 0);
            }, ct);
        }
        return await GetProgress(handle, ct);
    }
    public async Task<Progress> Finish(string handle, string reason, CancellationToken ct)
    {
        var state = await Authorized(handle, false, ct);
        return await store.Edit(state.JobId, current =>
        {
            if (current is null) throw new ToolFailure("Search is unavailable.");
            if (current.Status != "running") return (current, ToProgress(current));
            if (current.Accepted.Count >= current.Target) { current.Status = "completed"; current.FinishReason = "target_reached"; }
            else if (clock.GetUtcNow() >= current.Deadline || current.ToolCalls >= current.MaxToolCalls) { current.Status = "partial"; current.FinishReason = "budget_exhausted"; }
            else if (reason == "sources_exhausted" && current.ToolCalls >= 5) { current.Status = "partial"; current.FinishReason = "agent_reported_sources_exhausted"; }
            // Premature finish stays running; the returned remaining count directs the agent back to investigation.
            return (current, ToProgress(current));
        }, ct);
    }
    public async Task<FinalResponse> Complete(Guid jobId, string ownerId, string reason, CancellationToken ct)
    {
        var state = await store.Edit(jobId, current =>
        {
            if (current is null || current.OwnerId != ownerId) throw new ToolFailure("Search is unavailable.");
            if (current.Status == "running")
            {
                current.Status = current.Accepted.Count >= current.Target ? "completed" : "partial";
                current.FinishReason = current.Status == "completed" ? "target_reached" : reason is "provider_failed" or "cancelled" ? reason : "agent_stopped_before_target";
            }
            return (current, current);
        }, ct);
        var warnings = state.Status == "completed" ? Array.Empty<string>() : new[] { $"Partial search: {state.Accepted.Count} of {state.Target} jobs accepted; {state.FinishReason}. Accepted jobs were retained." };
        return new(JsonSerializer.Serialize(new { jobs = state.Accepted.Select(x => x.Posting), warnings }, Wire.Json), ToProgress(state));
    }
    public static Progress ToProgress(SearchState state) => new(state.Status, state.Accepted.Count, state.Target, Math.Max(0, state.Target - state.Accepted.Count), state.ToolCalls, state.MaxToolCalls, state.Deadline, state.FinishReason, state.Rejected.TakeLast(10).ToArray());
    private static bool SameUrl(string a, string b) => Uri.TryCreate(a, UriKind.Absolute, out var left) && Uri.TryCreate(b, UriKind.Absolute, out var right) && left.Scheme == right.Scheme && left.Host == right.Host && left.Port == right.Port && left.PathAndQuery.TrimEnd('/') == right.PathAndQuery.TrimEnd('/');
}
