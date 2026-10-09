namespace MediaEngine.Web.Services.Integration;

/// <summary>
/// Keeps authenticated Engine artwork on the Dashboard origin. Browser image
/// requests cannot attach the Engine session header, and an Engine URL using
/// localhost would point at the viewer's device rather than the server.
/// </summary>
public static class EngineImageProxyPath
{
    public const string ProxyPrefix = "/engine-image";

    private static readonly HashSet<string> AssetArtworkKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        "cover",
        "cover-thumb",
        "background",
        "banner",
        "logo",
    };

    public static string ToBrowserUrl(string value, Uri? engineBaseAddress)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        if (Uri.TryCreate(value, UriKind.Absolute, out var absolute))
        {
            if (engineBaseAddress is not null
                && HasSameOrigin(absolute, engineBaseAddress)
                && TryBuildProxyUrl(absolute.PathAndQuery, out var proxiedAbsolute))
            {
                return proxiedAbsolute;
            }

            return absolute.ToString();
        }

        var relative = value.StartsWith('/') ? value : $"/{value}";
        if (TryBuildProxyUrl(relative, out var proxiedRelative))
        {
            return proxiedRelative;
        }

        return engineBaseAddress is null
            ? value
            : new Uri(engineBaseAddress, relative).ToString();
    }

    public static bool IsAllowedEnginePath(string path)
    {
        var pathOnly = path.Split('?', 2)[0];
        var segments = pathOnly.Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (segments.Length == 5
            && segments[0].Equals("metadata", StringComparison.OrdinalIgnoreCase)
            && Guid.TryParse(segments[1], out _)
            && segments[2].Equals("tvdb-match", StringComparison.OrdinalIgnoreCase)
            && segments[3].Equals("previews", StringComparison.OrdinalIgnoreCase)
            && segments[4].Length == 32 && segments[4].All(Uri.IsHexDigit))
        {
            return true;
        }

        if (segments.Length == 3
            && segments[0].Equals("stream", StringComparison.OrdinalIgnoreCase)
            && segments[1].Equals("artwork", StringComparison.OrdinalIgnoreCase))
        {
            return Guid.TryParse(segments[2], out _);
        }

        if (segments.Length == 3
            && segments[0].Equals("stream", StringComparison.OrdinalIgnoreCase)
            && Guid.TryParse(segments[1], out _))
        {
            return AssetArtworkKinds.Contains(segments[2]);
        }

        if (segments.Length == 5
            && segments[0].Equals("stream", StringComparison.OrdinalIgnoreCase)
            && segments[1].Equals("entity", StringComparison.OrdinalIgnoreCase)
            && Guid.TryParse(segments[3], out _)
            && segments[4].Equals("cover", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (segments.Length == 7
            && segments[0].Equals("api", StringComparison.OrdinalIgnoreCase)
            && segments[1].Equals("v1", StringComparison.OrdinalIgnoreCase)
            && segments[2].Equals("display", StringComparison.OrdinalIgnoreCase)
            && segments[3].Equals("artwork", StringComparison.OrdinalIgnoreCase)
            && segments[4].Equals("assets", StringComparison.OrdinalIgnoreCase)
            && Guid.TryParse(segments[5], out _)
            && segments[6].Equals("content", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return segments.Length == 3
            && segments[0].Equals("persons", StringComparison.OrdinalIgnoreCase)
            && Guid.TryParse(segments[1], out _)
            && segments[2].Equals("headshot", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryBuildProxyUrl(string pathAndQuery, out string proxyUrl)
    {
        if (!IsAllowedEnginePath(pathAndQuery))
        {
            proxyUrl = string.Empty;
            return false;
        }

        proxyUrl = $"{ProxyPrefix}{(pathAndQuery.StartsWith('/') ? pathAndQuery : $"/{pathAndQuery}")}";
        return true;
    }

    private static bool HasSameOrigin(Uri left, Uri right) =>
        left.Scheme.Equals(right.Scheme, StringComparison.OrdinalIgnoreCase)
        && left.Host.Equals(right.Host, StringComparison.OrdinalIgnoreCase)
        && left.Port == right.Port;
}
