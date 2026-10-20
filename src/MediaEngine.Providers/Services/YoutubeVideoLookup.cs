using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using MediaEngine.Domain.Contracts;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Providers.Services;

/// <summary>
/// The YouTube lookup (the Official "youtube" provider). Without a key it asks YouTube's public oEmbed address for the
/// title, channel and thumbnail; with the person's own free key it also gets the description and upload date. Only the
/// 11-character video ID is sent. The key lives in the secrets store and is sent in a header, never in the address.
/// </summary>
public sealed partial class YoutubeVideoLookup : IVideoOnlineLookup
{
    public const string ProviderName = "youtube";
    public const string HttpClientName = "youtube";
    public const string KeySecretName = "api_key";

    private const string DefaultOEmbed = "https://www.youtube.com/oembed";
    private const string DefaultApi = "https://www.googleapis.com/youtube/v3";
    private const long MaxThumbnailBytes = 5L * 1024 * 1024;
    private static readonly string[] ThumbnailHosts = ["i.ytimg.com", "img.youtube.com", "i9.ytimg.com"];

    [GeneratedRegex("^[A-Za-z0-9_-]{11}$")]
    private static partial Regex IdPattern();

    private readonly IConfigurationLoader _configuration;
    private readonly IConfigSecretsStore _secrets;
    private readonly IHttpClientFactory _http;
    private readonly ILogger<YoutubeVideoLookup> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTime _lastCallUtc = DateTime.MinValue;

    public YoutubeVideoLookup(
        IConfigurationLoader configuration,
        IConfigSecretsStore secrets,
        IHttpClientFactory http,
        ILogger<YoutubeVideoLookup> logger)
    {
        _configuration = configuration;
        _secrets = secrets;
        _http = http;
        _logger = logger;
    }

    public async Task<VideoOnlineInfo?> LookupAsync(string youtubeVideoId, CancellationToken ct = default)
    {
        if (!IdPattern().IsMatch(youtubeVideoId ?? string.Empty))
        {
            return null;
        }

        var provider = _configuration.LoadProvider(ProviderName);
        if (provider is { Enabled: false })
        {
            return null;
        }

        var oembed = provider?.Endpoints.GetValueOrDefault("oembed") ?? DefaultOEmbed;
        var api = provider?.Endpoints.GetValueOrDefault("api") ?? DefaultApi;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var wait = TimeSpan.FromMilliseconds(250) - (DateTime.UtcNow - _lastCallUtc);
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

        try
        {
            var client = _http.CreateClient(HttpClientName);
            string? title = null, channel = null, thumbnailUrl = null, description = null;
            DateOnly? published = null;

            var embedUrl = $"{oembed}?url={Uri.EscapeDataString($"https://www.youtube.com/watch?v={youtubeVideoId}")}&format=json";
            using (var response = await client.GetAsync(embedUrl, ct).ConfigureAwait(false))
            {
                if (response.IsSuccessStatusCode)
                {
                    using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
                    title = Text(doc.RootElement, "title");
                    channel = Text(doc.RootElement, "author_name");
                    thumbnailUrl = Text(doc.RootElement, "thumbnail_url");
                }
                else
                {
                    _logger.LogInformation("YouTube lookup found nothing for a video ID (status {Status})", (int)response.StatusCode);
                }
            }

            var key = await _secrets.GetAsync(SecretOwner.Provider(ProviderName), KeySecretName, ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(key))
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, $"{api}/videos?part=snippet&id={youtubeVideoId}");
                request.Headers.Add("x-goog-api-key", key);
                using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
                    if (doc.RootElement.TryGetProperty("items", out var items) && items.GetArrayLength() > 0
                        && items[0].TryGetProperty("snippet", out var snippet))
                    {
                        title ??= Text(snippet, "title");
                        channel ??= Text(snippet, "channelTitle");
                        description = Text(snippet, "description");
                        published = Text(snippet, "publishedAt") is { Length: >= 10 } at
                            && DateOnly.TryParse(at[..10], CultureInfo.InvariantCulture, out var date) ? date : null;
                    }
                }
                else
                {
                    _logger.LogWarning("The YouTube key was not accepted or the allowance is used up (status {Status})", (int)response.StatusCode);
                }
            }

            if (title is null && channel is null && description is null)
            {
                return null;
            }

            var (bytes, mime) = await DownloadThumbnailAsync(client, thumbnailUrl, ct).ConfigureAwait(false);
            return new VideoOnlineInfo(title, channel, description, published, bytes, mime);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            if (ct.IsCancellationRequested)
            {
                throw;
            }

            _logger.LogInformation("YouTube lookup unavailable: {Reason}", ex.Message);
            return null;
        }
    }

    private async Task<(byte[]? Bytes, string? Mime)> DownloadThumbnailAsync(HttpClient client, string? url, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !ThumbnailHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase))
        {
            return (null, null);
        }

        using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaxThumbnailBytes)
        {
            return (null, null);
        }

        var bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        if (bytes.Length is < 1024 or > (int)MaxThumbnailBytes)
        {
            return (null, null);
        }

        if (bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
        {
            return (bytes, "image/jpeg");
        }

        return bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47 ? (bytes, "image/png") : (null, null);
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!.Trim()
            : null;
}
