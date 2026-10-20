using System.Globalization;
using System.Net;
using System.Text;
using MediaEngine.Providers.Models;

namespace MediaEngine.Providers.Tests.ProviderRecording;

/// <summary>One outgoing provider request as the recording handler saw it.</summary>
internal sealed record RecordedRequest(
    string Method, Uri Uri, string Body, string? BearerToken, IReadOnlyDictionary<string, string>? Headers = null)
{
    public string Url => Uri.AbsoluteUri;

    /// <summary>The value of a request header, or <c>null</c> when it was not sent.</summary>
    public string? Header(string name) => Headers?.GetValueOrDefault(name);
}

/// <summary>
/// Stub HTTP handler that answers canned responses and records every request as
/// <c>METHOD url | body [auth:scheme]</c>. API keys passed as <c>api_key</c> query
/// parameters are removed from the recorded URL (the before/after check keys on
/// method + URL without keys + body).
/// </summary>
internal sealed class RecordingHandler(Func<RecordedRequest, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<string> Lines { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null
            ? string.Empty
            : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var uri = request.RequestUri!;
        var line = $"{request.Method.Method} {RedactKeys(uri.AbsoluteUri)}";
        if (body.Length > 0)
        {
            line += $" | {body}";
        }

        if (request.Headers.Authorization is { } auth)
        {
            line += $" [auth:{auth.Scheme}]";
        }

        Lines.Add(line);
        var headers = request.Headers.ToDictionary(
            header => header.Key, header => string.Join(',', header.Value), StringComparer.OrdinalIgnoreCase);
        return respond(new RecordedRequest(request.Method.Method, uri, body, request.Headers.Authorization?.Parameter, headers));
    }

    internal static string RedactKeys(string url)
    {
        var queryStart = url.IndexOf('?', StringComparison.Ordinal);
        if (queryStart < 0)
        {
            return url;
        }

        var kept = url[(queryStart + 1)..]
            .Split('&')
            .Where(pair => !pair.StartsWith("api_key=", StringComparison.OrdinalIgnoreCase));
        var query = string.Join('&', kept);
        return query.Length == 0 ? url[..queryStart] : $"{url[..queryStart]}?{query}";
    }

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) => new(status)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    public static HttpResponseMessage Status(HttpStatusCode status) => new(status);
}

/// <summary><see cref="IHttpClientFactory"/> that hands every named client the same handler.</summary>
internal sealed class RecordingHttpFactory(HttpMessageHandler handler) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
}

/// <summary>Renders provider output as stable text for golden comparison.</summary>
internal static class RecordingText
{
    public static string Claims(IEnumerable<ProviderClaim> claims) =>
        string.Join('\n', claims.Select(c =>
            $"{c.Key}={c.Value}@{c.Confidence.ToString("0.####", CultureInfo.InvariantCulture)}"
            + (c.SourceLanguage is null ? string.Empty : $"~{c.SourceLanguage}")));

    public static string Section(string title, IEnumerable<string> lines) =>
        $"== {title} ==\n{string.Join('\n', lines)}\n";
}

/// <summary>
/// Golden-file comparison. Snapshots live in <c>Fixtures/ProviderRecordings</c>. Set
/// <c>TUVIMA_UPDATE_RECORDINGS=1</c> to (re)write them; otherwise a missing or different
/// snapshot fails the test.
/// </summary>
internal static class RecordingSnapshot
{
    public static void Verify(string name, params string[] sections)
    {
        var actual = Normalize(string.Concat(sections));
        var path = Path.Combine(FindRepoRoot(), "tests", "MediaEngine.Providers.Tests",
            "Fixtures", "ProviderRecordings", $"{name}.txt");
        if (Environment.GetEnvironmentVariable("TUVIMA_UPDATE_RECORDINGS") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, actual);
            return;
        }

        Assert.True(File.Exists(path), $"Recording '{name}' has not been captured yet: {path}");
        Assert.Equal(Normalize(File.ReadAllText(path)), actual);
    }

    private static string Normalize(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd() + "\n";

    internal static string FindRepoRoot()
    {
        var dir = Path.GetDirectoryName(typeof(RecordingSnapshot).Assembly.Location);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir, "MediaEngine.slnx")))
            {
                return dir;
            }

            dir = Path.GetDirectoryName(dir);
        }

        throw new InvalidOperationException("Could not find repository root (MediaEngine.slnx)");
    }
}
