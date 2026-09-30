using MediaEngine.Domain.Services;
using Microsoft.Extensions.Caching.Memory;
using SkiaSharp;

namespace MediaEngine.Api.Services.Canonical;

/// <summary>Creates a bounded preview for a TheTVDB search result without exposing its original to picker tiles.</summary>
internal static class RetailCandidateArtworkPreview
{
    internal static async Task<string?> LoadTvdbAsync(
        string? sourceUrl, IHttpClientFactory httpFactory, IMemoryCache cache, CancellationToken ct)
    {
        if (!Uri.TryCreate(sourceUrl, UriKind.Absolute, out var source)
            || source.Scheme != Uri.UriSchemeHttps
            || !(source.Host.Equals("thetvdb.com", StringComparison.OrdinalIgnoreCase)
                 || source.Host.EndsWith(".thetvdb.com", StringComparison.OrdinalIgnoreCase)))
            return null;

        var key = $"tvdb-retail-preview:{source}";
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
}
