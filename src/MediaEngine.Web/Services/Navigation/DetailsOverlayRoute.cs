using System.Globalization;

namespace MediaEngine.Web.Services.Navigation;

/// <summary>
/// Which page sits under the details overlay and which detail address sits over it (spec D1).
/// A detail address is shown as an overlay over the page the person came from; this record is the answer for
/// one address, and <see cref="Resolve"/> is the history-correct rule that produces it.
/// Addresses are app paths with their query ("/listen?x=1"; "/" is Home), never absolute URIs.
/// </summary>
/// <param name="UnderlayUri">The page kept on screen under the overlay; null when no overlay is open.</param>
/// <param name="OverlayUri">The detail address shown over it; null when no overlay is open.</param>
/// <param name="Depth">
/// How many history entries lie between the underlay's entry and this one (1 for the first title opened over a page,
/// 2 after one title-to-title step). 0 means unknown (after a reload), where dismissing navigates to the underlay instead of stepping back.
/// </param>
public sealed record DetailsOverlayRoute(string? UnderlayUri, string? OverlayUri, int Depth)
{
    public const string Home = "/";
    private const string DetailPrefix = "/details/";
    private const string StatePrefix = "tl-details-overlay:v1";

    /// <summary>No overlay: the current address is an ordinary page.</summary>
    public static DetailsOverlayRoute None { get; } = new(null, null, 0);

    public bool IsOpen => OverlayUri is not null;

    /// <summary>True when <paramref name="appUri"/> is a detail address (<c>/details/{type}/{id}</c>).</summary>
    public static bool IsDetailUri(string? appUri) =>
        !string.IsNullOrEmpty(appUri) && PathOf(appUri).StartsWith(DetailPrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>The path part of an app address (no query or fragment).</summary>
    public static string PathOf(string appUri)
    {
        var end = appUri.IndexOfAny(['?', '#']);
        return end < 0 ? appUri : appUri[..end];
    }

    /// <summary>
    /// Decides the overlay for <paramref name="newUri"/>.
    /// </summary>
    /// <param name="current">The answer for the address the person is leaving.</param>
    /// <param name="previousUri">The address the person is leaving; null on the first render of a circuit.</param>
    /// <param name="newUri">The address being shown.</param>
    /// <param name="historyEntryState">The state stored with the new history entry; used for Back and Forward onto a detail entry.</param>
    /// <param name="firstRender">True on the first render of a circuit (first load or reload): the overlay opens over Home and any stored state is ignored.</param>
    /// <param name="canRestoreUnderlay">Whether a page can be shown under the overlay (it was seen in this circuit, or it is Home). Defaults to "anything but a detail address".</param>
    public static DetailsOverlayRoute Resolve(
        DetailsOverlayRoute current,
        string? previousUri,
        string newUri,
        string? historyEntryState,
        bool firstRender,
        Func<string, bool>? canRestoreUnderlay = null)
    {
        if (!IsDetailUri(newUri))
        {
            return None;
        }

        canRestoreUnderlay ??= static _ => true;

        if (firstRender)
        {
            return new DetailsOverlayRoute(Home, newUri, 0);
        }

        // Back or Forward onto an entry this circuit tagged: that entry's own underlay comes back, not the page we are leaving.
        if (TryDecodeState(historyEntryState, out var underlay, out var depth))
        {
            return IsUsableUnderlay(underlay, canRestoreUnderlay)
                ? new DetailsOverlayRoute(underlay, newUri, depth)
                : new DetailsOverlayRoute(Home, newUri, 0);
        }

        // Title to title from inside the overlay: the underlay stays and the overlay content swaps.
        if (current.IsOpen && current.UnderlayUri is { } keptUnderlay)
        {
            return new DetailsOverlayRoute(keptUnderlay, newUri, current.Depth > 0 ? current.Depth + 1 : 0);
        }

        // From an ordinary page: that page is the underlay, as long as it can be shown under an overlay.
        return previousUri is not null && IsUsableUnderlay(previousUri, canRestoreUnderlay)
            ? new DetailsOverlayRoute(previousUri, newUri, 1)
            : new DetailsOverlayRoute(Home, newUri, 0);
    }

    /// <summary>
    /// The history state to store with a new detail entry opened from <paramref name="current"/>'s address, so Back and Forward
    /// later restore the same underlay. Returns null when <paramref name="targetUri"/> is not a detail address.
    /// </summary>
    public static string? StateForNewEntry(
        DetailsOverlayRoute current,
        string? previousUri,
        string targetUri,
        Func<string, bool>? canRestoreUnderlay = null)
    {
        if (!IsDetailUri(targetUri))
        {
            return null;
        }

        var next = Resolve(current, previousUri, targetUri, historyEntryState: null, firstRender: false, canRestoreUnderlay);
        return EncodeState(next.UnderlayUri ?? Home, next.Depth);
    }

    /// <summary>The state stored with an entry whose overlay is <paramref name="route"/> (used to replace an entry without losing its underlay).</summary>
    public static string? StateOf(DetailsOverlayRoute route) =>
        route.IsOpen ? EncodeState(route.UnderlayUri ?? Home, route.Depth) : null;

    public static string EncodeState(string underlayUri, int depth) =>
        string.Create(CultureInfo.InvariantCulture, $"{StatePrefix};{Math.Max(depth, 0)};{underlayUri}");

    public static bool TryDecodeState(string? state, out string underlayUri, out int depth)
    {
        underlayUri = Home;
        depth = 0;
        if (string.IsNullOrEmpty(state))
        {
            return false;
        }

        var parts = state.Split(';', 3);
        if (parts.Length != 3
            || !string.Equals(parts[0], StatePrefix, StringComparison.Ordinal)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out depth)
            || !IsAppPath(parts[2]))
        {
            depth = 0;
            return false;
        }

        underlayUri = parts[2];
        return true;
    }

    // A stored underlay must be an app path ("/..."), never another site or a detail address.
    private static bool IsAppPath(string value) =>
        value.Length > 0 && value[0] == '/' && !value.StartsWith("//", StringComparison.Ordinal) && !IsDetailUri(value);

    private static bool IsUsableUnderlay(string uri, Func<string, bool> canRestoreUnderlay) =>
        IsAppPath(uri) && canRestoreUnderlay(uri);
}
