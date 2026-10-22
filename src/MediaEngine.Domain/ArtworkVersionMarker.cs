namespace MediaEngine.Domain;

/// <summary>
/// The short change marker carried in picture addresses (<c>?v=</c>). It is derived from the
/// original picture file's length and last-write time, so an artwork edit or an in-place refresh
/// gives the picture a new address while an unchanged picture keeps its address and stays in the
/// browser's keep. The Engine recomputes it when serving and only lets the browser keep the
/// picture for a long time when the marker in the address still matches the file.
/// </summary>
public static class ArtworkVersionMarker
{
    /// <summary>Query-string key of the change marker.</summary>
    public const string QueryKey = "v";

    /// <summary>Marker for a picture file, or null when the file is missing.</summary>
    public static string? ForFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var info = new FileInfo(path);
        if (!info.Exists)
        {
            return null;
        }

        // FNV-1a over the two numbers keeps the marker short and stable across processes.
        unchecked
        {
            var hash = 14695981039346656037UL;
            foreach (var value in new[] { (ulong)info.Length, (ulong)info.LastWriteTimeUtc.Ticks })
            {
                for (var shift = 0; shift < 64; shift += 8)
                {
                    hash ^= (value >> shift) & 0xFF;
                    hash *= 1099511628211UL;
                }
            }

            return (hash & 0xFFFFFFFFFFUL).ToString("x10", System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    /// <summary>
    /// Adds (or replaces) the change marker on an address. A null marker returns the address
    /// without a marker, so the picture is still served but checked by the browser on each use.
    /// </summary>
    public static string WithMarker(string url, string? marker)
    {
        ArgumentNullException.ThrowIfNull(url);

        var hashIndex = url.IndexOf('#', StringComparison.Ordinal);
        var fragment = hashIndex >= 0 ? url[hashIndex..] : string.Empty;
        var withoutFragment = hashIndex >= 0 ? url[..hashIndex] : url;
        var queryIndex = withoutFragment.IndexOf('?', StringComparison.Ordinal);
        var path = queryIndex >= 0 ? withoutFragment[..queryIndex] : withoutFragment;
        var parameters = (queryIndex >= 0 ? withoutFragment[(queryIndex + 1)..] : string.Empty)
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Where(parameter => !parameter.StartsWith(QueryKey + "=", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (!string.IsNullOrWhiteSpace(marker))
        {
            parameters.Add($"{QueryKey}={Uri.EscapeDataString(marker)}");
        }

        return parameters.Count == 0
            ? path + fragment
            : $"{path}?{string.Join('&', parameters)}{fragment}";
    }
}
