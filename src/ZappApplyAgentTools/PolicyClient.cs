using System.Net.Http.Headers;
using System.Text.Json;

namespace ZappApplyAgentTools;

public sealed class PolicyClient(HttpClient http, IConfiguration config) : IApplicationPolicy
{
    public Task<PolicyResult> Evaluate(Guid jobId, string ownerId, JsonElement posting, CancellationToken ct) => EvaluateEvidence(jobId, ownerId, posting, [], ct);
    public async Task<bool> IsCancelled(Guid jobId, string ownerId, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/internal/search-tools/status");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config["App:PolicyKey"]);
        request.Content = JsonContent.Create(new { jobId, ownerId });
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) throw new ToolFailure("Search ownership/status could not be checked.");
        using var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return result.RootElement.GetProperty("cancelled").GetBoolean();
    }
    public async Task<PolicyResult> EvaluateEvidence(Guid jobId, string ownerId, JsonElement posting, string[] urls, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/internal/search-tools/evaluate");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config["App:PolicyKey"]);
        request.Content = JsonContent.Create(new { jobId, ownerId, posting, verifiedApplicationUrls = urls });
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) throw new ToolFailure("The application criteria service is unavailable; no candidate was accepted. Retry this tool later.");
        return await response.Content.ReadFromJsonAsync<PolicyResult>(Wire.Json, ct) ?? throw new ToolFailure("Invalid criteria response.");
    }
}
