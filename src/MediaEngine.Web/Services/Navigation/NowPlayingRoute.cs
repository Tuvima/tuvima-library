using Microsoft.AspNetCore.Components;

namespace MediaEngine.Web.Services.Navigation;

/// <summary>
/// The Now playing page's address and the way back from it (unified player v2 D24). The page is an ordinary routed page, so the person always
/// comes from somewhere: the address carries that place as a relative "return" path, and Back to library and Close go to it. Only a path on this
/// site is ever followed, so the query can never send anyone elsewhere.
/// </summary>
public static class NowPlayingRoute
{
    /// <summary>The page's path.</summary>
    public const string Path = "/listen/now-playing";

    /// <summary>The query name that holds the page the person came from.</summary>
    public const string ReturnQuery = "return";

    /// <summary>Where Back to library and Close go when the page was opened with nothing to return to (a pasted address, a reload).</summary>
    public const string DefaultReturn = "/listen";

    /// <summary>True when an address (absolute, or a path) is the Now playing page itself.</summary>
    public static bool IsNowPlaying(string? address)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return false;
        }

        // A path ("/listen/now-playing?return=...") is read as it is; anything else must be a full address.
        var path = address;
        if (address[0] != '/' && Uri.TryCreate(address, UriKind.Absolute, out var absolute))
        {
            path = absolute.AbsolutePath;
        }
        else
        {
            var end = path.IndexOfAny(['?', '#']);
            if (end >= 0)
            {
                path = path[..end];
            }
        }

        return string.Equals(path.TrimEnd('/'), Path, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The page's address, remembering the page to come back to.</summary>
    public static string Build(string? returnTo)
    {
        var target = Sanitize(returnTo);
        return string.Equals(target, DefaultReturn, StringComparison.Ordinal)
            ? Path
            : $"{Path}?{ReturnQuery}={Uri.EscapeDataString(target)}";
    }

    /// <summary>
    /// A return path that is safe to follow: a path on this site that is not the Now playing page. Anything else (an absolute address, a
    /// protocol-relative one, a backslash trick, empty) becomes <see cref="DefaultReturn"/>.
    /// </summary>
    public static string Sanitize(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate)
            || candidate[0] != '/'
            || candidate.StartsWith("//", StringComparison.Ordinal)
            || candidate.Contains('\\', StringComparison.Ordinal)
            || candidate.Any(char.IsControl)
            || IsNowPlaying(candidate))
        {
            return DefaultReturn;
        }

        return candidate;
    }

    /// <summary>
    /// The page to go back to, read from the address of the routed page: under an open details window that is the page beneath it, not the window's own address.
    /// </summary>
    public static string ReturnTarget(NavigationManager navigation, DetailsOverlayState? overlay)
    {
        var uri = new Uri(RoutedPageAddress.AbsoluteFor(navigation, overlay));
        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var split = pair.IndexOf('=', StringComparison.Ordinal);
            if (split > 0 && string.Equals(pair[..split], ReturnQuery, StringComparison.Ordinal))
            {
                return Sanitize(Uri.UnescapeDataString(pair[(split + 1)..].Replace('+', ' ')));
            }
        }

        return DefaultReturn;
    }
}
