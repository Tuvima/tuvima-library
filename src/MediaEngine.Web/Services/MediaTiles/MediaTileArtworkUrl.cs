namespace MediaEngine.Web.Services.MediaTiles;

public static class MediaTileArtworkUrl
{
    public static string? Sized(string? url, string size)
    {
        var normalizedSize = size.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(url)
            || normalizedSize is not ("s" or "m" or "l")
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

    private static bool SupportsRenditions(string url) =>
        url.Contains("/stream/artwork/", StringComparison.OrdinalIgnoreCase)
        || url.Contains("/api/v1/display/artwork/assets/", StringComparison.OrdinalIgnoreCase)
        || (url.Contains("/stream/entity/", StringComparison.OrdinalIgnoreCase)
            && url.Contains("/cover", StringComparison.OrdinalIgnoreCase))
        || (url.Contains("/persons/", StringComparison.OrdinalIgnoreCase)
            && url.Contains("/headshot", StringComparison.OrdinalIgnoreCase));

    private static void AddCandidate(List<string> parts, string? url, int width)
    {
        if (!string.IsNullOrWhiteSpace(url)
            && !parts.Any(part => part.StartsWith($"{url} ", StringComparison.OrdinalIgnoreCase)))
        {
            parts.Add($"{url} {width}w");
        }
    }
}
