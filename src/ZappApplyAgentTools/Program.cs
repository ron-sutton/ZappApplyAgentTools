using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ZappApplyAgentTools;

var app = SearchHost.Build(args);
if (args.Contains("--initialize-store")) { await ((SqlSearchStore)app.Services.GetRequiredService<ISearchStore>()).Initialize(default); return; }
await app.RunAsync();
public static class SearchHost
{
    public static WebApplication Build(string[] args, Action<WebApplicationBuilder>? configure = null, Action<IServiceCollection>? overrides = null)
    {
        var builder = WebApplication.CreateBuilder(args.Where(a => a != "--initialize-store").ToArray());
        configure?.Invoke(builder);
        builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = 1_000_000);
        var options = builder.Configuration.GetSection("Search").Get<SearchOptions>() ?? new(); options.Validate();
        builder.Services.AddSingleton(options); builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<ISearchStore>(_ => new SqlSearchStore(builder.Configuration.GetConnectionString("SearchStore") ?? throw new InvalidOperationException("Configure ConnectionStrings:SearchStore.")));
        builder.Services.AddSingleton<IPostingReader, PublicPostingReader>();
        builder.Services.AddHttpClient<IApplicationPolicy, PolicyClient>(http =>
        {
            var uri = new Uri(builder.Configuration["App:BaseUrl"] ?? throw new InvalidOperationException("Configure App:BaseUrl."));
            if (uri.Scheme != "https" && !builder.Configuration.GetValue<bool>("App:AllowInsecureHttp")) throw new InvalidOperationException("Policy service must use HTTPS unless trusted HTTP is explicitly enabled.");
            if ((builder.Configuration["App:PolicyKey"] ?? "").Length < 32) throw new InvalidOperationException("Configure App:PolicyKey.");
            http.BaseAddress = new Uri(uri.AbsoluteUri.TrimEnd('/') + "/"); http.Timeout = TimeSpan.FromSeconds(15);
        }).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        builder.Services.AddScoped<SearchService>();
        builder.Services.AddMcpServer().WithHttpTransport().WithTools<SearchTools>();
        overrides?.Invoke(builder.Services);
        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            if (context.Request.Path == "/healthz") { await next(context); return; }
            var expected = context.Request.Path.StartsWithSegments("/mcp") ? options.McpKey : options.ControlKey;
            var header = context.Request.Headers.Authorization.ToString();
            var provided = header.StartsWith("Bearer ", StringComparison.Ordinal) ? header[7..] : "";
            if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(provided)), SHA256.HashData(Encoding.UTF8.GetBytes(expected)))) { context.Response.StatusCode = 401; return; }
            if (context.Request.Headers.ContainsKey("Origin")) { context.Response.StatusCode = 403; return; }
            try { await next(context); }
            catch (ToolFailure error) { if (!context.Response.HasStarted) { context.Response.StatusCode = 409; await context.Response.WriteAsJsonAsync(new { error = error.Message }); } }
        });
        app.MapGet("/healthz", async (ISearchStore store, CancellationToken ct) =>
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(3));
            try { await store.Read(Guid.Empty, timeout.Token); return Results.Ok(new { status = "ready" }); } catch { return Results.StatusCode(503); }
        });
        app.MapPost("/internal/searches", (JsonElement request, SearchService searches, CancellationToken ct) => searches.Begin(request, ct));
        app.MapPost("/internal/searches/{id:guid}/complete", (Guid id, CompletionRequest request, SearchService searches, CancellationToken ct) => searches.Complete(id, request.OwnerId, request.Reason, ct));
        app.MapMcp("/mcp");
        return app;

    }
}
public sealed record CompletionRequest(string OwnerId, string Reason);
