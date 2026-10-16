namespace MediaEngine.Contracts.Details;

/// <summary>
/// The one switch for the Universe and Related detail tabs. Both the Engine (when it builds a
/// page's tab list) and the Dashboard (when it filters a tab list for display) read it, so
/// restoring the tabs on every page and every device is a single change here. The Universe
/// explorer and Collections stay reachable from their own places either way.
/// </summary>
public static class DetailTabVisibility
{
    /// <summary>Set to <c>true</c> to bring the Universe and Related tabs back everywhere.</summary>
    public static bool ShowUniverseAndRelatedTabs { get; } = false;

    /// <summary>True for the tab keys the switch controls.</summary>
    public static bool IsSwitchedTab(string? key) =>
        string.Equals(key, "universe", StringComparison.OrdinalIgnoreCase)
        || string.Equals(key, "related", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether a tab with this key may be shown for the given state of the switch.</summary>
    public static bool IsTabAllowed(string? key, bool showUniverseAndRelated) =>
        showUniverseAndRelated || !IsSwitchedTab(key);
}
