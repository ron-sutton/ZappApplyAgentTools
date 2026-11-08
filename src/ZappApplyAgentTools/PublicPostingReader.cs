using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using AngleSharp.Html.Parser;

namespace ZappApplyAgentTools;

public sealed class PublicPostingReader : IPostingReader
{
    public static bool PublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return false;
        var b = address.GetAddressBytes();
        if (b.Length == 16 && (b[0] == 0x20 && b[1] == 0x02 || b[0] == 0x20 && b[1] == 0x01 && b[2] == 0 && b[3] == 0)) return false;
        if (b.Length == 16) return (b[0] & 0xe0) == 0x20 && !(b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x0d && b[3] == 0xb8);
        return b[0] is not (0 or 10 or 127) && b[0] < 224 && !(b[0] == 169 && b[1] == 254) && !(b[0] == 172 && b[1] is >= 16 and <= 31)
            && !(b[0] == 192 && (b[1] == 168 || b[1] == 0 || b[1] == 2)) && !(b[0] == 100 && b[1] is >= 64 and <= 127)
            && !(b[0] == 198 && b[1] is 18 or 19 or 51) && !(b[0] == 203 && b[1] == 0 && b[2] == 113);
    }
    private static Uri PublicUri(string value)
    {
        if (value.Length > 2048 || !Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo)
            || !uri.IsDefaultPort || !uri.Host.Contains('.') || uri.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase)
            || IPAddress.TryParse(uri.Host, out var ip) && !PublicAddress(ip)) throw new ToolFailure("Use a public HTTP(S) posting URL on its standard port.");
        return uri;
    }
    public async Task<(string Url, string Text)> Read(string url, CancellationToken ct)
    {
        var result = await ReadEvidence(url, ct); return (result.Url, result.Text);
    }
    public async Task<PostingEvidence> ReadEvidence(string url, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(20));
        using var handler = new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false, UseCookies = false, AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate };
        handler.ConnectCallback = async (context, token) =>
        {
            var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, token);
            if (addresses.Length == 0 || addresses.Any(a => !PublicAddress(a))) throw new ToolFailure("Posting host does not resolve exclusively to public addresses.");
            var socket = new Socket(addresses[0].AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            try { await socket.ConnectAsync(new IPEndPoint(addresses[0], context.DnsEndPoint.Port), token); return new NetworkStream(socket, ownsSocket: true); }
            catch { socket.Dispose(); throw; }
        };
        using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var uri = PublicUri(url);
        for (var redirects = 0; redirects <= 5; redirects++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri); request.Headers.UserAgent.ParseAdd("ZappApplyAgentTools/1.0"); request.Headers.Accept.ParseAdd("text/html,application/json;q=0.9");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location) { uri = PublicUri(new Uri(uri, location).AbsoluteUri); continue; }
            if (!response.IsSuccessStatusCode) throw new ToolFailure("Posting could not be read: HTTP " + (int)response.StatusCode + ". Use another public source for the same opening.");
            var media = response.Content.Headers.ContentType?.MediaType;
            if (media is not ("text/html" or "application/xhtml+xml" or "application/json" or "text/plain")) throw new ToolFailure("Posting content type is not supported.");
            await using var input = await response.Content.ReadAsStreamAsync(deadline.Token); using var bytes = new MemoryStream(); var buffer = new byte[16384]; int read;
            while ((read = await input.ReadAsync(buffer, deadline.Token)) > 0) { if (bytes.Length + read > 2_000_000) throw new ToolFailure("Posting page is too large."); bytes.Write(buffer, 0, read); }
            var body = Encoding.UTF8.GetString(bytes.ToArray());
            if (media is "application/json" or "text/plain") return new(uri.AbsoluteUri, body.Length <= 150_000 ? body : throw new ToolFailure("Posting text is too large."), []);
            var document = await new HtmlParser().ParseDocumentAsync(body, deadline.Token);
            var applicationUrls = ApplicationLinks(body, uri);
            var structured = new List<string>();
            foreach (var script in document.QuerySelectorAll("script[type='application/ld+json']"))
            {
                try { using var json = JsonDocument.Parse(script.TextContent); Collect(json.RootElement, structured); } catch (JsonException) { }
            }
            foreach (var element in document.QuerySelectorAll("script,style,noscript,nav,footer,header")) element.Remove();
            var content = document.QuerySelector("main,article,[role='main']") ?? document.Body;
            var text = structured.Count > 0 ? string.Join("\n\n", structured) : content?.TextContent ?? "";
            text = WebUtility.HtmlDecode(text).Trim();
            if (text.Length > 150_000) throw new ToolFailure("Posting text is too large.");
            return new(uri.AbsoluteUri, text, applicationUrls);
        }
        throw new ToolFailure("Too many posting redirects.");
    }
    public static string[] ApplicationLinks(string html, Uri page)
    {
        var document = new HtmlParser().ParseDocument(html);
        return document.QuerySelectorAll("a[href],iframe[src]").Where(element =>
            element.LocalName == "iframe" || element.TextContent.Contains("apply", StringComparison.OrdinalIgnoreCase)
            || (element.GetAttribute("aria-label") ?? "").Contains("apply", StringComparison.OrdinalIgnoreCase))
            .Select(element => element.GetAttribute(element.LocalName == "iframe" ? "src" : "href"))
            .Select(value => Uri.TryCreate(page, value, out var uri) && uri.Scheme is "https" or "http" && string.IsNullOrEmpty(uri.UserInfo) && uri.IsDefaultPort ? uri.AbsoluteUri : null)
            .OfType<string>().Where(url => url.Length <= 2048).Distinct().Take(30).ToArray();
    }
    private static void Collect(JsonElement node, List<string> output)
    {
        if (node.ValueKind == JsonValueKind.Array) { foreach (var item in node.EnumerateArray()) Collect(item, output); return; }
        if (node.ValueKind != JsonValueKind.Object) return;
        if (node.TryGetProperty("@type", out var type) && (type.ValueKind == JsonValueKind.String && type.GetString() == "JobPosting" || type.ValueKind == JsonValueKind.Array && type.EnumerateArray().Any(x => x.ValueKind == JsonValueKind.String && x.GetString() == "JobPosting")))
        {
            foreach (var key in new[] { "title", "description", "hiringOrganization", "jobLocation", "baseSalary", "employmentType", "applicantLocationRequirements" })
                if (node.TryGetProperty(key, out var value)) output.Add(value.ValueKind == JsonValueKind.String ? new HtmlParser().ParseDocument(value.GetString()!).Body?.TextContent ?? "" : value.GetRawText());
        }
        if (node.TryGetProperty("@graph", out var graph)) Collect(graph, output);
    }
}
