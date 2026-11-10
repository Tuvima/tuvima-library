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

    /// <summary>A comic's page list and its page pictures (PDF and comic reader spec D2).</summary>
    public const string PagesSuffix = "pages";
    public const string PagesRoute = ProxyPrefix + "/{assetId:guid}/" + PagesSuffix;
    public const string PageRoute = ProxyPrefix + "/{assetId:guid}/" + PagesSuffix + "/{page:int}";

    /// <summary>The largest page number and picture width the proxy will carry; the Engine applies its own, narrower rules.</summary>
    public const int MaxPageNumber = 100_000;
    public const int MaxWidth = 4096;

    public static string ToPagesBrowserUrl(Guid assetId) => $"{ProxyPrefix}/{assetId:D}/{PagesSuffix}";

    public static string ToPageBrowserUrl(Guid assetId, int page, int width = 0) =>
        $"{ProxyPrefix}/{assetId:D}/{PagesSuffix}/{page}" + (width > 0 ? $"?width={width}" : string.Empty);

    public static string ToEnginePagesPath(Guid assetId) => $"/read/{assetId:D}/{PagesSuffix}";

    public static string ToEnginePagePath(Guid assetId, int page, int width) =>
        $"/read/{assetId:D}/{PagesSuffix}/{page}" + (width > 0 ? $"?width={width}" : string.Empty);

    /// <summary>
    /// True only for exactly <c>/engine-book/{guid}/pages</c> (when <paramref name="page"/> is null) or
    /// <c>/engine-book/{guid}/pages/{n}</c>, with n a plain positive number. Same strictness as the file path.
    /// </summary>
    public static bool TryParsePagesPath(string? path, bool withPage, out Guid assetId, out int page)
    {
        assetId = Guid.Empty;
        page = 0;
        if (string.IsNullOrEmpty(path) || path.IndexOfAny(['?', '#', '%', '\\']) >= 0)
        {
            return false;
        }

        var segments = path.Split('/');
        if (segments.Length != (withPage ? 5 : 4)
            || segments[0].Length != 0
            || !segments[1].Equals(ProxyPrefix[1..], StringComparison.OrdinalIgnoreCase)
            || !segments[3].Equals(PagesSuffix, StringComparison.OrdinalIgnoreCase)
            || !Guid.TryParseExact(segments[2], "D", out var parsed)
            || parsed == Guid.Empty)
        {
            return false;
        }

        if (withPage
            && (segments[4].Length is 0 or > 6
                || !segments[4].All(char.IsAsciiDigit)
                || !int.TryParse(segments[4], out page)
                || page < 1 || page > MaxPageNumber))
        {
            return false;
        }

        assetId = parsed;
        return true;
    }

    /// <summary>The only query a page request may carry is <c>width</c>, a whole number from 0 to <see cref="MaxWidth"/>.</summary>
    public static bool TryReadWidth(IQueryCollection query, out int width)
    {
        width = 0;
        if (query.Count == 0)
        {
            return true;
        }

        return query.Count == 1
            && query.TryGetValue("width", out var values)
            && values.Count == 1
            && values[0] is { Length: > 0 and <= 4 } text
            && text.All(char.IsAsciiDigit)
            && int.TryParse(text, out width)
            && width <= MaxWidth;
    }

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
        if (string.IsNullOrEmpty(path) || path.IndexOfAny(['?', '#', '%', '\\']) >= 0)
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
