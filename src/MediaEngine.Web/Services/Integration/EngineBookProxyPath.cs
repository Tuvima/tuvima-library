namespace MediaEngine.Web.Services.Integration;

/// <summary>
/// The one browser-facing address for reading a book file: <c>/engine-book/{assetId}/file</c>.
/// A browser reader cannot attach the Engine's credentials, so the Dashboard fetches the file
/// for it. This is deliberately separate from the artwork proxy and far narrower: one asset id
/// in canonical form and one fixed suffix, nothing the caller can steer toward another Engine route.
/// </summary>
public static class EngineBookProxyPath
{
    public const string ProxyPrefix = "/engine-book";
    public const string FileSuffix = "file";
    public const string FileRoute = ProxyPrefix + "/{assetId:guid}/" + FileSuffix;

    public static string ToBrowserUrl(Guid assetId) => $"{ProxyPrefix}/{assetId:D}/{FileSuffix}";

    /// <summary>The Engine route the proxy calls; built from a parsed id, never from request text.</summary>
    public static string ToEnginePath(Guid assetId) => $"/read/{assetId:D}/{FileSuffix}";

    /// <summary>
    /// True only for exactly <c>/engine-book/{guid in D form}/file</c>: no query string, no extra
    /// or empty segments, no encoded or backslash characters, no other suffix.
    /// </summary>
    public static bool TryParseBrowserPath(string? path, out Guid assetId)
    {
        assetId = Guid.Empty;
        if (string.IsNullOrEmpty(path) || path.AsSpan().IndexOfAny('?', '#', '%', '\\') >= 0)
        {
            return false;
        }

        var segments = path.Split('/');
        // "/engine-book/{id}/file" splits into ["", "engine-book", "{id}", "file"].
        if (segments.Length != 4
            || segments[0].Length != 0
            || !segments[1].Equals(ProxyPrefix[1..], StringComparison.OrdinalIgnoreCase)
            || !segments[3].Equals(FileSuffix, StringComparison.OrdinalIgnoreCase)
            || !Guid.TryParseExact(segments[2], "D", out var parsed)
            || parsed == Guid.Empty)
        {
            return false;
        }

        assetId = parsed;
        return true;
    }
}
