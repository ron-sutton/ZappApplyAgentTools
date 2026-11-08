using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace ZappApplyAgentTools;

public sealed class PolicyClient(HttpClient http, IConfiguration config) : IApplicationPolicy
{
    public async Task<PolicyResult> Evaluate(Guid jobId, string ownerId, JsonElement posting, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/internal/search-tools/evaluate");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config["App:PolicyKey"]);
        request.Content = JsonContent.Create(new { jobId, ownerId, posting });
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) throw new ToolFailure("The application criteria service is unavailable; no candidate was accepted. Retry this tool later.");
        return await response.Content.ReadFromJsonAsync<PolicyResult>(Wire.Json, ct) ?? throw new ToolFailure("Invalid criteria response.");
    }
}
