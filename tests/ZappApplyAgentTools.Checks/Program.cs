using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ZappApplyAgentTools;

var count = 0;
void Check(string name, bool valid) { if (!valid) throw new Exception(name); Console.WriteLine("PASS " + name); count++; }
async Task Reject(string name, Func<Task> action) { try { await action(); throw new Exception("Expected rejection: " + name); } catch (ToolFailure) { Check(name, true); } }
var options = new SearchOptions { ControlKey = new string('c', 40), McpKey = new string('m', 40), TargetCount = 2, MaxToolCalls = 20, MaxMinutes = 30 };
var memory = new MemoryStore(); var reader = new FakeReader(); var policy = new FakePolicy(); var clock = new FakeClock();
var searches = new SearchService(memory, reader, policy, options, clock);
JsonElement Request(Guid id, string owner = "owner-a") => JsonSerializer.SerializeToElement(new { schemaVersion = 1, jobId = id, jobType = "AI_Search", ownerId = owner, requestedAtUtc = "2026-11-08T12:00:00Z", payload = new { criteria = new { titles = new[] { "Engineer" } }, systemMessage = "Fictional app instructions" } });
await Reject("Malformed admission fails with a controlled error", () => searches.Begin(JsonSerializer.SerializeToElement(new { schemaVersion = "wrong" }), default));
var request = Request(Guid.NewGuid()); var admission = await searches.Begin(request, default);
Check("Admission creates opaque handle without app job ID", admission.Handle.Length == 64 && !admission.Handle.Contains(request.GetProperty("jobId").GetGuid().ToString("N")));
Check("Redelivery resumes existing search", (await searches.Begin(request, default)).Handle == admission.Handle && memory.Count == 1);
await Reject("Same job cannot change owner", () => searches.Begin(Request(request.GetProperty("jobId").GetGuid(), "other-owner"), default));
await Reject("Unknown handle cannot access another search", () => searches.GetProgress(new string('a', 64), default));
var source = await searches.ReadPosting(admission.Handle, "https://employer.example/jobs/1", default);
SourcePosting Posting(string url) => new() { Title = "Engineer", Company = "Fixture Employer", Description = "Engineer develops systems. Salary 175000. Remote US.", Source = "Fixture source", Url = url, WorkArrangement = "remote", EligibleCountries = ["US"], SalaryMin = 175000, Currency = "USD", PayPeriod = "annual", CompensationBasis = "base" };
var first = new CandidateSubmission(source.Id, Posting(source.Url)); var progress = await searches.Submit(admission.Handle, [first], default);
Check("Sourced candidate is saved before finalization", progress.AcceptedCount == 1 && progress.Remaining == 1);
Check("Existing app policy is consulted", policy.Calls == 1);
progress = await searches.Finish(admission.Handle, "target_reached", default);
Check("Premature finish returns remaining count and keeps running", progress.Status == "running" && progress.Remaining == 1);
progress = await searches.Submit(admission.Handle, [first], default);
Check("Duplicates cannot fill remaining count", progress.AcceptedCount == 1 && progress.Rejections.Last().Reasons.Any(x => x.Contains("already accepted")));
var other = await searches.Begin(Request(Guid.NewGuid(), "owner-b"), default);
progress = await searches.Submit(other.Handle, [first], default);
Check("Foreign source cannot cross searches", progress.AcceptedCount == 0 && progress.Rejections.Last().Reasons.Any(x => x.Contains("belong")));
var second = await searches.ReadPosting(admission.Handle, "https://employer.example/jobs/2", default);
var unsupported = Posting(second.Url); unsupported.Description = "An invented achievement.";
progress = await searches.Submit(admission.Handle, [new(second.Id, unsupported)], default);
Check("Invented source facts are rejected", progress.AcceptedCount == 1 && progress.Rejections.Last().Reasons.Any(x => x.Contains("description")));
var inventedPay = Posting(second.Url); inventedPay.SalaryMin = 999999;
progress = await searches.Submit(admission.Handle, [new(second.Id, inventedPay)], default);
Check("Unsupported salary does not count", progress.AcceptedCount == 1 && progress.Rejections.Last().Reasons.Any(x => x.Contains("salaryMin")));
var badShape = Posting(second.Url); badShape.WorkArrangement = "Hybrid/Remote";
progress = await searches.Submit(admission.Handle, [new(second.Id, badShape)], default);
Check("Invalid field values cannot reach final JSON", progress.AcceptedCount == 1 && progress.Rejections.Last().Reasons.Any(x => x.Contains("WorkArrangement")));
policy.Reject = true; progress = await searches.Submit(admission.Handle, [new(second.Id, Posting(second.Url))], default);
Check("Criteria rejection returns reasons and remaining count", progress.AcceptedCount == 1 && progress.Remaining == 1 && progress.Rejections.Last().Reasons.Contains("User criterion did not match.")); policy.Reject = false;
await Task.WhenAll(searches.Submit(admission.Handle, [new(second.Id, Posting(second.Url))], default), searches.Submit(admission.Handle, [new(second.Id, Posting(second.Url))], default));
progress = await searches.Finish(admission.Handle, "target_reached", default); Check("Concurrent duplicates cannot exceed target", progress.AcceptedCount == 2 && progress.Status == "completed");
var final = await searches.Complete(request.GetProperty("jobId").GetGuid(), "owner-a", "agent_stopped", default);
using (var result = JsonDocument.Parse(final.Content)) Check("C# creates exact jobs/warnings envelope", result.RootElement.EnumerateObject().Select(x => x.Name).SequenceEqual(new[] { "jobs", "warnings" }) && result.RootElement.GetProperty("jobs").GetArrayLength() == 2 && result.RootElement.GetProperty("warnings").GetArrayLength() == 0);
Check("Finalization is idempotent", (await searches.Complete(request.GetProperty("jobId").GetGuid(), "owner-a", "provider_failed", default)).Content == final.Content);
await Reject("Final results are owner scoped", () => searches.Complete(request.GetProperty("jobId").GetGuid(), "owner-b", "agent_stopped", default));
await Reject("Finalized sessions refuse more work", () => searches.ReadPosting(admission.Handle, "https://employer.example/jobs/3", default));
var partialRequest = Request(Guid.NewGuid()); var partial = await searches.Begin(partialRequest, default); var partialSource = await searches.ReadPosting(partial.Handle, "https://employer.example/jobs/p", default);
await searches.Submit(partial.Handle, [new(partialSource.Id, Posting(partialSource.Url))], default);
var interrupted = await searches.Complete(partialRequest.GetProperty("jobId").GetGuid(), "owner-a", "provider_failed", default);
using (var result = JsonDocument.Parse(interrupted.Content)) Check("Provider failure preserves partial accepted jobs", result.RootElement.GetProperty("jobs").GetArrayLength() == 1 && result.RootElement.GetProperty("warnings")[0].GetString()!.Contains("provider_failed"));
var timed = await searches.Begin(Request(Guid.NewGuid()), default); clock.Advance(TimeSpan.FromHours(1)); Check("Deadline allows explicit partial completion", (await searches.Finish(timed.Handle, "target_reached", default)).FinishReason == "budget_exhausted");
foreach (var ip in new[] { "127.0.0.1", "10.0.0.1", "172.16.0.1", "192.168.1.1", "169.254.169.254", "100.64.0.1", "::1", "fc00::1", "::ffff:127.0.0.1", "2001:db8::1", "2002:7f00:1::1", "2001::1" }) Check("Blocks private address " + ip, !PublicPostingReader.PublicAddress(IPAddress.Parse(ip)));
foreach (var url in new[] { "http://127.0.0.1/", "http://localhost/", "https://example.com:1234/", "file:///etc/passwd", "http://host.internal/" }) await Reject("Rejects unsafe URL before network " + url, () => new PublicPostingReader().Read(url, default));

var hosted = SearchHost.Build([], b => { b.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { { "Search:ControlKey", options.ControlKey }, { "Search:McpKey", options.McpKey } }); b.WebHost.UseUrls("http://127.0.0.1:0"); }, services => { services.AddSingleton<ISearchStore>(memory); services.AddSingleton<IPostingReader>(reader); services.AddSingleton<IApplicationPolicy>(policy); });
try
{
    await hosted.StartAsync(); var endpoint = hosted.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single(); using var http = new HttpClient { BaseAddress = new Uri(endpoint) };
    Check("Anonymous control access denied", (await http.PostAsJsonAsync("/internal/searches", Request(Guid.NewGuid()))).StatusCode == HttpStatusCode.Unauthorized);
    http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.McpKey); Check("MCP key cannot create sessions", (await http.PostAsJsonAsync("/internal/searches", Request(Guid.NewGuid()))).StatusCode == HttpStatusCode.Unauthorized);
    http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.ControlKey); Check("Control admission works", (await http.PostAsJsonAsync("/internal/searches", Request(Guid.NewGuid()))).IsSuccessStatusCode);
    http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.McpKey); http.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/event-stream");
    async Task<JsonDocument> Rpc(object payload) { using var response = await http.PostAsJsonAsync("/mcp", payload); var text = await response.Content.ReadAsStringAsync(); if (!response.IsSuccessStatusCode) throw new Exception("MCP HTTP " + response.StatusCode + ": " + text); if (text.StartsWith("event:") || text.StartsWith("data:")) text = text.Split('\n').Last(line => line.StartsWith("data: "))[6..]; return JsonDocument.Parse(text); }
    using (var initialized = await Rpc(new { jsonrpc = "2.0", id = 1, method = "initialize", @params = new { protocolVersion = "2025-06-18", capabilities = new { }, clientInfo = new { name = "fictional-checks", version = "1" } } })) Check("Real MCP HTTP handshake works", initialized.RootElement.TryGetProperty("result", out _));
    using (var tools = await Rpc(new { jsonrpc = "2.0", id = 2, method = "tools/list" })) { var names = tools.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray().Select(x => x.GetProperty("name").GetString()).ToArray(); Check("Only four approved tools exposed", names.Order().SequenceEqual(new[] { "finish_search", "get_search_progress", "read_posting", "submit_candidates" })); }
    var httpSession = await new SearchService(memory, reader, policy, options, TimeProvider.System).Begin(Request(Guid.NewGuid()), default);

    using (var toolResult = await Rpc(new { jsonrpc = "2.0", id = 3, method = "tools/call", @params = new { name = "read_posting", arguments = new { handle = httpSession.Handle, url = "https://employer.example/jobs/http" } } }))
    {
        var tool = toolResult.RootElement.GetProperty("result");
        using var sourceJson = JsonDocument.Parse(tool.GetProperty("content")[0].GetProperty("text").GetString()!);
        var body = sourceJson.RootElement;
        Check("MCP tool exposes source evidence without owner or job ID", body.TryGetProperty("sourceId", out _) && !body.TryGetProperty("jobId", out _) && !body.TryGetProperty("ownerId", out _));
        using var submitted = await Rpc(new { jsonrpc = "2.0", id = 4, method = "tools/call", @params = new { name = "submit_candidates", arguments = new { handle = httpSession.Handle, candidates = new[] { new { sourceId = body.GetProperty("sourceId").GetGuid(), posting = Posting(body.GetProperty("url").GetString()!) } } } } });
        using var saved = JsonDocument.Parse(submitted.RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!);
        Check("Real MCP tool submission durably accepts a sourced job", saved.RootElement.GetProperty("acceptedCount").GetInt32() == 1);
    }
    using (var denied = await Rpc(new { jsonrpc = "2.0", id = 5, method = "tools/call", @params = new { name = "get_search_progress", arguments = new { handle = new string('f', 64) } } }))
        Check("MCP invalid session returns a tool error", denied.RootElement.GetProperty("result").GetProperty("isError").GetBoolean());
    http.DefaultRequestHeaders.Add("Origin", "https://untrusted.example"); Check("Browser-origin requests denied", (await http.PostAsJsonAsync("/internal/searches", Request(Guid.NewGuid()))).StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized);
}
finally { await hosted.StopAsync(); await hosted.DisposeAsync(); }
if (args.Contains("--localdb")) await SqlChecks.Run(Check);
Console.WriteLine($"{count} search tool checks passed. No live AI or production writes.");
