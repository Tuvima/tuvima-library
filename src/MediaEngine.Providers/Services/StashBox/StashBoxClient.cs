using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using MediaEngine.Domain.Contracts;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Providers.Services.StashBox;

/// <summary>One scene record as a stash-box server described it, after every field was cleaned and length-limited.</summary>
public sealed record StashBoxScene(
    string Id,
    string? Title,
    string? Details,
    DateOnly? Date,
    string? Code,
    string? Studio,
    IReadOnlyList<string> Performers,
    IReadOnlyList<string> Tags,
    string? ImageUrl);

/// <summary>What one question to a stash-box server came back with. Status is "ok", "not_connected" or "unavailable".</summary>
public sealed record StashBoxReply(string Status, IReadOnlyList<StashBoxScene> Scenes)
{
    public static StashBoxReply NotConnected { get; } = new("not_connected", []);

    public static StashBoxReply Unavailable { get; } = new("unavailable", []);
}

/// <summary>
/// Speaks the open stash-box GraphQL standard over plain HTTPS. The account key goes in an <c>ApiKey</c> header to the
/// server's own address only (never to an image address, never in a URL, never in a log). Redirects are not followed, replies
/// are size-limited, and everything a server says is treated as untrusted text.
/// </summary>
public sealed class StashBoxClient
{
    public const string HttpClientName = "stashbox";
    public const long MaxReplyBytes = 2L * 1024 * 1024;
    public const long MaxCoverBytes = 8L * 1024 * 1024;
    public const int MaxSceneResults = 10;

    private const string SceneFields = "id title details date code studio { id name } tags { id name } performers { performer { id name } } images { url width height }";
    private const int MaxNames = 50;

    private readonly IHttpClientFactory _http;
    private readonly ILogger<StashBoxClient> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTime _lastCallUtc = DateTime.MinValue;

    public StashBoxClient(IHttpClientFactory http, ILogger<StashBoxClient> logger)
    {
        _http = http;
        _logger = logger;
    }

    /// <summary>Asks the server who the key belongs to. Nothing is saved and no file information is sent.</summary>
    public async Task<StashBoxTestResult> TestAsync(Uri address, string key, CancellationToken ct = default)
    {
        var reply = await PostAsync(address, key, "query { me { name } }", null, ct).ConfigureAwait(false);
        using var body = reply.Body;
        if (reply.Status == "unavailable")
        {
            return new StashBoxTestResult(false, "unavailable", "The server could not be reached. Check the address and try again.");
        }

        var root = body?.RootElement;
        var known = reply.Status == "ok" && root is { ValueKind: JsonValueKind.Object } r
            && r.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object
            && data.TryGetProperty("me", out var me) && me.ValueKind == JsonValueKind.Object;
        return known
            ? new StashBoxTestResult(true, StashBoxConnectionStates.Connected, "Connected.")
            : new StashBoxTestResult(false, StashBoxConnectionStates.NotConnected, "The server did not accept this key.");
    }

    /// <summary>Looks scenes up by title text. Only the title leaves the machine.</summary>
    public async Task<StashBoxReply> SearchByTitleAsync(Uri address, string key, string title, CancellationToken ct = default)
    {
        var variables = new Dictionary<string, object?> { ["term"] = title, ["limit"] = MaxSceneResults };
        var query = $"query Search($term: String!, $limit: Int) {{ searchScene(term: $term, limit: $limit) {{ {SceneFields} }} }}";
        return await QueryScenesAsync(address, key, query, variables, "searchScene", ct).ConfigureAwait(false);
    }

    /// <summary>Looks scenes up by file fingerprints. Callers must have checked the fingerprint permission first.</summary>
    public async Task<StashBoxReply> FindByFingerprintsAsync(Uri address, string key, IReadOnlyList<AdultFingerprint> fingerprints, CancellationToken ct = default)
    {
        var list = fingerprints
            .Where(f => AdultFingerprintAlgorithms.IsValid(f.Algorithm) && IsHash(f.Hash))
            .Take(6)
            .Select(f => new Dictionary<string, object?> { ["hash"] = f.Hash, ["algorithm"] = f.Algorithm })
            .ToList();
        if (list.Count == 0)
        {
            return new StashBoxReply("ok", []);
        }

        var variables = new Dictionary<string, object?> { ["fps"] = list };
        var query = $"query Fp($fps: [FingerprintQueryInput!]!) {{ findScenesByFullFingerprints(fingerprints: $fps) {{ {SceneFields} }} }}";
        return await QueryScenesAsync(address, key, query, variables, "findScenesByFullFingerprints", ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Downloads a cover image. The address must be https on the server's own site, nothing is followed to another host,
    /// the key is not sent, and only a real JPEG, PNG or WebP of a sensible size is returned.
    /// </summary>
    public async Task<(byte[]? Bytes, string? Mime)> DownloadCoverAsync(Uri server, string? imageUrl, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(imageUrl, UriKind.Absolute, out var uri) || !StashBoxAddress.IsOwnImageHost(server, uri))
        {
            return (null, null);
        }

        try
        {
            await WaitTurnAsync(ct).ConfigureAwait(false);
            var client = _http.CreateClient(HttpClientName);
            using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaxCoverBytes)
            {
                return (null, null);
            }

            var bytes = await ReadLimitedAsync(response.Content, MaxCoverBytes, ct).ConfigureAwait(false);
            var mime = bytes is null ? null : SniffImage(bytes);
            return mime is null ? (null, null) : (bytes, mime);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            if (ct.IsCancellationRequested)
            {
                throw;
            }

            _logger.LogInformation("A stash-box cover could not be downloaded from {Host}", uri.Host);
            return (null, null);
        }
    }

    private async Task<StashBoxReply> QueryScenesAsync(
        Uri address, string key, string query, Dictionary<string, object?> variables, string field, CancellationToken ct)
    {
        var reply = await PostAsync(address, key, query, variables, ct).ConfigureAwait(false);
        if (reply.Status != "ok")
        {
            return reply.Status == "not_connected" ? StashBoxReply.NotConnected : StashBoxReply.Unavailable;
        }

        using var body = reply.Body!;
        var root = body.RootElement;
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array
            && errors.GetArrayLength() > 0 && LooksUnauthorised(errors))
        {
            return StashBoxReply.NotConnected;
        }

        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object
            || !data.TryGetProperty(field, out var scenes) || scenes.ValueKind != JsonValueKind.Array)
        {
            return StashBoxReply.Unavailable;
        }

        var result = new List<StashBoxScene>();
        foreach (var scene in scenes.EnumerateArray().Take(MaxSceneResults * 2))
        {
            if (ParseScene(scene) is { } parsed)
            {
                result.Add(parsed);
            }
        }

        return new StashBoxReply("ok", result);
    }

    private sealed record RawReply(string Status, JsonDocument? Body);

    private async Task<RawReply> PostAsync(Uri address, string key, string query, Dictionary<string, object?>? variables, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > 512 || key.Any(c => char.IsControl(c) || c is '\r' or '\n'))
        {
            return new RawReply("not_connected", null);
        }

        try
        {
            await WaitTurnAsync(ct).ConfigureAwait(false);
            var payload = JsonSerializer.Serialize(new Dictionary<string, object?> { ["query"] = query, ["variables"] = variables });
            using var request = new HttpRequestMessage(HttpMethod.Post, address)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json"),
            };
            request.Headers.Add("ApiKey", key);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            var client = _http.CreateClient(HttpClientName);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return new RawReply("not_connected", null);
            }

            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaxReplyBytes)
            {
                _logger.LogInformation("A stash-box server answered with status {Status}", (int)response.StatusCode);
                return new RawReply("unavailable", null);
            }

            var bytes = await ReadLimitedAsync(response.Content, MaxReplyBytes, ct).ConfigureAwait(false);
            if (bytes is null)
            {
                return new RawReply("unavailable", null);
            }

            var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
            if (document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("errors", out var errors)
                && errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() > 0 && LooksUnauthorised(errors))
            {
                document.Dispose();
                return new RawReply("not_connected", null);
            }

            return new RawReply("ok", document);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or IOException or InvalidOperationException)
        {
            if (ct.IsCancellationRequested)
            {
                throw;
            }

            _logger.LogInformation("A stash-box server is unavailable: {Reason}", ex.GetType().Name);
            return new RawReply("unavailable", null);
        }
    }

    private async Task WaitTurnAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var wait = TimeSpan.FromMilliseconds(300) - (DateTime.UtcNow - _lastCallUtc);
            if (wait > TimeSpan.Zero)
            {
                await Task.Delay(wait, ct).ConfigureAwait(false);
            }

            _lastCallUtc = DateTime.UtcNow;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static async Task<byte[]?> ReadLimitedAsync(HttpContent content, long limit, CancellationToken ct)
    {
        await using var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > limit)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private static bool LooksUnauthorised(JsonElement errors)
    {
        foreach (var error in errors.EnumerateArray().Take(5))
        {
            if (error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String)
            {
                var text = message.GetString() ?? string.Empty;
                if (text.Contains("unauthor", StringComparison.OrdinalIgnoreCase) || text.Contains("forbidden", StringComparison.OrdinalIgnoreCase)
                    || text.Contains("not authenticated", StringComparison.OrdinalIgnoreCase) || text.Contains("api key", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    internal static StashBoxScene? ParseScene(JsonElement scene)
    {
        if (scene.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var id = Clean(Text(scene, "id"), 64);
        if (id is null || !id.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))
        {
            return null;
        }

        DateOnly? date = null;
        if (Text(scene, "date") is { } dateText
            && DateOnly.TryParseExact(dateText, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedDate)
            && parsedDate.Year is >= 1900 and <= 2100)
        {
            date = parsedDate;
        }

        var studio = scene.TryGetProperty("studio", out var studioElement) && studioElement.ValueKind == JsonValueKind.Object
            ? Clean(Text(studioElement, "name"), 200)
            : null;

        var performers = new List<string>();
        if (scene.TryGetProperty("performers", out var performerList) && performerList.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in performerList.EnumerateArray().Take(MaxNames))
            {
                if (entry.ValueKind == JsonValueKind.Object && entry.TryGetProperty("performer", out var performer)
                    && performer.ValueKind == JsonValueKind.Object && Clean(Text(performer, "name"), 200) is { } name)
                {
                    AddDistinct(performers, name);
                }
            }
        }

        var tags = new List<string>();
        if (scene.TryGetProperty("tags", out var tagList) && tagList.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in tagList.EnumerateArray().Take(MaxNames))
            {
                if (entry.ValueKind == JsonValueKind.Object && Clean(Text(entry, "name"), 100) is { } name)
                {
                    AddDistinct(tags, name);
                }
            }
        }

        string? imageUrl = null;
        if (scene.TryGetProperty("images", out var images) && images.ValueKind == JsonValueKind.Array)
        {
            foreach (var image in images.EnumerateArray().Take(20))
            {
                if (image.ValueKind != JsonValueKind.Object || Text(image, "url") is not { Length: <= 500 } url)
                {
                    continue;
                }

                // A scene cover is landscape; the first landscape image wins, otherwise the first image.
                imageUrl ??= url;
                if (image.TryGetProperty("width", out var w) && image.TryGetProperty("height", out var h)
                    && w.ValueKind == JsonValueKind.Number && h.ValueKind == JsonValueKind.Number
                    && w.TryGetInt32(out var width) && h.TryGetInt32(out var height) && width >= height)
                {
                    imageUrl = url;
                    break;
                }
            }
        }

        return new StashBoxScene(
            id,
            Clean(Text(scene, "title"), 300),
            Clean(Text(scene, "details"), 4000, keepNewLines: true),
            date,
            Clean(Text(scene, "code"), 64),
            studio,
            performers,
            tags,
            imageUrl);
    }

    private static void AddDistinct(List<string> list, string value)
    {
        if (!list.Contains(value, StringComparer.OrdinalIgnoreCase))
        {
            list.Add(value);
        }
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>Strips control characters, trims, and cuts to a sensible length. Returns null for nothing left.</summary>
    internal static string? Clean(string? value, int maxLength, bool keepNewLines = false)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var builder = new StringBuilder(Math.Min(value.Length, maxLength));
        foreach (var c in value)
        {
            if (builder.Length >= maxLength)
            {
                break;
            }

            if (char.IsControl(c))
            {
                if (keepNewLines && c is '\n')
                {
                    builder.Append(c);
                }
                else if (c is '\t' or '\r' or '\n')
                {
                    builder.Append(' ');
                }

                continue;
            }

            builder.Append(c);
        }

        var cleaned = builder.ToString().Trim();
        return cleaned.Length == 0 ? null : cleaned;
    }

    private static bool IsHash(string? value) =>
        !string.IsNullOrEmpty(value) && value.Length <= 128 && value.All(char.IsAsciiHexDigit);

    internal static string? SniffImage(byte[] bytes)
    {
        if (bytes.Length < 1024)
        {
            return null;
        }

        if (bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
        {
            return "image/jpeg";
        }

        if (bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47)
        {
            return "image/png";
        }

        return bytes[0] == 'R' && bytes[1] == 'I' && bytes[2] == 'F' && bytes[3] == 'F'
            && bytes[8] == 'W' && bytes[9] == 'E' && bytes[10] == 'B' && bytes[11] == 'P' ? "image/webp" : null;
    }
}
