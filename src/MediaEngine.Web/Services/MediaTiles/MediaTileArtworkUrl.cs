namespace MediaEngine.Web.Services.MediaTiles;

public static class MediaTileArtworkUrl
{
    public static string? Sized(string? url, string size)
    {
        var normalizedSize = size.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(url)
            || !ArtworkSize.IsKnown(normalizedSize)
            || !SupportsRenditions(url))
        {
            return null;
        }

        var hashIndex = url.IndexOf('#', StringComparison.Ordinal);
        var hash = hashIndex >= 0 ? url[hashIndex..] : string.Empty;
        var withoutHash = hashIndex >= 0 ? url[..hashIndex] : url;
        var queryIndex = withoutHash.IndexOf('?', StringComparison.Ordinal);
        var baseUrl = queryIndex >= 0 ? withoutHash[..queryIndex] : withoutHash;
        var query = queryIndex >= 0 ? withoutHash[(queryIndex + 1)..] : string.Empty;
        var parameters = query
            .Split('&', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(parameter => !parameter.StartsWith("size=", StringComparison.OrdinalIgnoreCase))
            .Append($"size={normalizedSize}");
        return $"{baseUrl}?{string.Join('&', parameters)}{hash}";
    }

    /// <summary>
    /// The address for a picture drawn at a fixed size: the smallest copy at least
    /// <paramref name="drawnPx"/> CSS pixels on its long side at 1x. Addresses without copies
    /// (provider links, static files) come back unchanged.
    /// </summary>
    public static string? ForDrawnSize(string? url, int drawnPx) =>
        Sized(url, ArtworkSize.ForPixels(drawnPx)) ?? url;

    /// <summary>
    /// A "1x, 2x, 3x" source list for a picture drawn at a fixed size, so a sharp screen gets the
    /// next copy up and a 1x screen keeps the small one. Null when every density uses the same copy
    /// or the address has no copies (the plain <c>src</c> is then enough).
    /// </summary>
    public static string? SrcSetForDrawnSize(string? url, int drawnPx)
    {
        if (string.IsNullOrWhiteSpace(url) || !SupportsRenditions(url) || drawnPx <= 0)
        {
            return null;
        }

        var sizes = new[] { 1, 2, 3 }.Select(density => ArtworkSize.ForPixels(drawnPx * density)).ToArray();
        if (sizes.All(size => size == sizes[0]))
        {
            return null;
        }

        return string.Join(", ", sizes.Select((size, index) => $"{Sized(url, size)} {index + 1}x"));
    }

    public static string? SrcSet(string? smallUrl, string? mediumUrl) => SrcSet(smallUrl, mediumUrl, null, null, null);
    public static string? SrcSet(string? smallUrl, string? mediumUrl, string? largeUrl) => SrcSet(smallUrl, mediumUrl, largeUrl, null, null);

    public static string? SrcSet(string? smallUrl, string? mediumUrl, string? largeUrl, int? nativeWidth, int? nativeHeight)
    {
        if (nativeWidth is not > 0 || nativeHeight is not > 0)
        {
            return null;
        }
        var parts = new List<string>();
        var widths = new HashSet<int>();
        var urls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (url, bound) in new[] { (smallUrl, 320), (mediumUrl, 960), (largeUrl, 2160) })
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                continue;
            }
            var width = (int)Math.Round(nativeWidth.Value * Math.Min(1d, bound / (double)Math.Max(nativeWidth.Value, nativeHeight.Value)));
            if (width > 0 && widths.Add(width) && urls.Add(url))
            {
                parts.Add($"{url} {width}w");
            }
        }
        return parts.Count == 0 ? null : string.Join(", ", parts);
    }

    /// <summary>True when the address is an Engine picture route that takes <c>?size=</c>.</summary>
    public static bool SupportsRenditions(string url) =>
        url.Contains("/stream/artwork/", StringComparison.OrdinalIgnoreCase)
        || IsAssetPictureRoute(url)
        || url.Contains("/library/portraits/", StringComparison.OrdinalIgnoreCase)
        || url.Contains("/api/v1/display/artwork/assets/", StringComparison.OrdinalIgnoreCase)
        || (url.Contains("/stream/entity/", StringComparison.OrdinalIgnoreCase)
            && url.Contains("/cover", StringComparison.OrdinalIgnoreCase))
        || (url.Contains("/persons/", StringComparison.OrdinalIgnoreCase)
            && url.Contains("/headshot", StringComparison.OrdinalIgnoreCase));

    // /stream/{assetId}/cover, /cover-thumb and /background take a size; /logo and media streams do not.
    private static bool IsAssetPictureRoute(string url)
    {
        var path = url.Split('?', '#')[0];
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var streamIndex = Array.FindIndex(segments, segment => segment.Equals("stream", StringComparison.OrdinalIgnoreCase));
        return streamIndex >= 0
            && segments.Length == streamIndex + 3
            && Guid.TryParse(segments[streamIndex + 1], out _)
            && segments[streamIndex + 2].ToLowerInvariant() is "cover" or "cover-thumb" or "background";
    }

    private static void AddCandidate(List<string> parts, string? url, int width)
    {
        if (!string.IsNullOrWhiteSpace(url)
            && !parts.Any(part => part.StartsWith($"{url} ", StringComparison.OrdinalIgnoreCase)))
        {
            parts.Add($"{url} {width}w");
        }
    }
}
