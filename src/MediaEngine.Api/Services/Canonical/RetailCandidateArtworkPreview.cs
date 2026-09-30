using MediaEngine.Domain.Services;
using Microsoft.Extensions.Caching.Memory;
using SkiaSharp;

namespace MediaEngine.Api.Services.Canonical;

/// <summary>Creates bounded, allow-listed previews for retail search tiles.</summary>
internal static class RetailCandidateArtworkPreview
{
    internal static Task<string?> LoadAsync(
        string? providerName,
        string? sourceUrl,
        IHttpClientFactory httpFactory,
        IMemoryCache cache,
        CancellationToken ct) =>
        LoadAsync(NormalizePreviewSource(providerName, sourceUrl),
            IsAllowedProviderArtworkHost(providerName), httpFactory, cache, ct);

    internal static async Task<string?> LoadTvdbAsync(
        string? sourceUrl, IHttpClientFactory httpFactory, IMemoryCache cache, CancellationToken ct)
    {
        return await LoadAsync(sourceUrl, IsTvdbHost, httpFactory, cache, ct);
    }

    private static async Task<string?> LoadAsync(
        string? sourceUrl,
        Func<Uri, bool> allowedHost,
        IHttpClientFactory httpFactory,
        IMemoryCache cache,
        CancellationToken ct)
    {
        if (!Uri.TryCreate(sourceUrl, UriKind.Absolute, out var source)
            || source.Scheme != Uri.UriSchemeHttps
            || !allowedHost(source))
            return null;

        var key = $"retail-preview:{source}";
        if (cache.TryGetValue<string>(key, out var cached)) return cached;

        try
        {
            using var client = httpFactory.CreateClient("cover_download");
            using var response = await client.GetAsync(source, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode) return null;
            var bytes = await BoundedHttpContent.ReadImageAsync(response.Content, ct);
            using var bitmap = SKBitmap.Decode(bytes);
            if (bitmap is null || bitmap.Width < 1 || bitmap.Height < 1) return null;
            var width = Math.Min(bitmap.Width, 180);
            var height = Math.Max(1, (int)Math.Round(bitmap.Height * width / (double)bitmap.Width));
            using var resized = bitmap.Resize(new SKImageInfo(width, height), SKSamplingOptions.Default);
            if (resized is null) return null;
            using var image = SKImage.FromBitmap(resized);
            using var encoded = image.Encode(SKEncodedImageFormat.Jpeg, 78);
            var preview = $"data:image/jpeg;base64,{Convert.ToBase64String(encoded.ToArray())}";
            cache.Set(key, preview, TimeSpan.FromMinutes(15));
            return preview;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    private static Func<Uri, bool> IsAllowedProviderArtworkHost(string? providerName) =>
        (providerName ?? string.Empty).Trim().ToLowerInvariant().Replace('-', '_') switch
        {
            "tvdb" => IsTvdbHost,
            "tmdb" => uri => HostMatches(uri, "image.tmdb.org"),
            "musicbrainz" => uri => HostMatches(uri, "coverartarchive.org"),
            "apple_api" or "apple_music" => uri => HostMatches(uri, "mzstatic.com"),
            "comicvine" or "comic_vine" => uri => HostMatches(uri, "comicvine.gamespot.com")
                                                   || HostMatches(uri, "gamespot.com")
                                                   || HostMatches(uri, "comicvine.com"),
            _ => _ => false,
        };

    internal static string? NormalizePreviewSource(string? providerName, string? sourceUrl)
    {
        if (string.IsNullOrWhiteSpace(sourceUrl)) return null;
        var provider = (providerName ?? string.Empty).Trim().ToLowerInvariant().Replace('-', '_');
        return provider switch
        {
            "apple_api" or "apple_music" => System.Text.RegularExpressions.Regex.Replace(
                sourceUrl, @"\d+x\d+bb\.(?=jpe?g|png)", "180x180bb.",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase),
            "tmdb" => System.Text.RegularExpressions.Regex.Replace(
                sourceUrl, @"/t/p/(?:original|w\d+)/", "/t/p/w185/",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase),
            "musicbrainz" => sourceUrl.Replace("/front-500", "/front-250", StringComparison.OrdinalIgnoreCase),
            "comicvine" or "comic_vine" => sourceUrl.Replace("/uploads/original/", "/uploads/scale_small/", StringComparison.OrdinalIgnoreCase),
            _ => sourceUrl,
        };
    }

    private static bool IsTvdbHost(Uri uri) => HostMatches(uri, "thetvdb.com");

    private static bool HostMatches(Uri uri, string host) =>
        uri.Host.Equals(host, StringComparison.OrdinalIgnoreCase)
        || uri.Host.EndsWith($".{host}", StringComparison.OrdinalIgnoreCase);
}
