using MediaEngine.Web.Components.Shared;

namespace MediaEngine.Web.Services.Navigation;

/// <summary>What the Downloads dock tab does until the Tuvima app can download on its own.</summary>
public enum DownloadsTabMode
{
    /// <summary>The tab opens /downloads, a page that says downloads live in the Tuvima app.</summary>
    ExplainNativeApp,

    /// <summary>The tab is dropped and the dock has four tabs.</summary>
    Hidden,
}

/// <summary>How one dock tab behaves when it is tapped.</summary>
public enum ShellDockKind
{
    Link,
    Libraries,
    Search,
}

/// <summary>One tab in the phone and TV dock.</summary>
public readonly record struct ShellDockEntry(string Key, string Path, string LabelKey, string Icon, ShellDockKind Kind = ShellDockKind.Link);

/// <summary>One row in the Libraries switcher.</summary>
public readonly record struct LibrarySurface(string Key, string Path, string LabelKey, string Icon);

/// <summary>
/// The dock catalog for the phone and TV shell: which tabs exist, which are kept for a Kids profile,
/// and which surfaces count as "a library" for the Libraries tab.
/// Old intents (Read, Watch, Listen, View, Collections) stay in the catalog so settings saved before the
/// five-tab dock still resolve.
/// </summary>
public static class ShellDock
{
    /// <summary>The one switch for the Downloads tab. Flip to <see cref="DownloadsTabMode.Hidden"/> to drop it.</summary>
    public const DownloadsTabMode DownloadsMode = DownloadsTabMode.ExplainNativeApp;

    public const string DownloadsPath = "/downloads";
    public const string SettingsPath = "/settings";
    public const string DefaultLibraryPath = "/watch";

    /// <summary>Browser storage key prefix for the last library surface a profile used.</summary>
    public const string LastLibraryStorageKeyPrefix = "tuvima.last-library.v1.";

    public static readonly LibrarySurface[] LibrarySurfaces =
    [
        new("Watch", "/watch", "Nav_Watch", AppMaterialIcons.Outlined.LiveTv),
        new("Read", "/read", "Nav_Read", AppMaterialIcons.Outlined.MenuBook),
        new("Listen", "/listen", "Nav_Listen", AppMaterialIcons.Outlined.Headphones),
        new("View", "/view", "Nav_View", AppMaterialIcons.Outlined.PhotoLibrary),
        new("Collections", "/collections", "Nav_Collections", AppMaterialIcons.Outlined.CollectionsBookmark),
    ];

    public static readonly LibrarySurface RecentlyAdded =
        new("RecentlyAdded", "/recently-added", "Nav_RecentlyAdded", AppMaterialIcons.Outlined.Schedule);

    // The Kids shell keeps only the lanes a child needs.
    private static readonly string[] KidsLibraryPaths = ["/watch", "/listen", "/read"];
    // Settings saved before the five-tab dock can still list the three lanes a child had, so those stay allowed.
    private static readonly string[] KidsDockKeys = ["Home", "Libraries", "Search", "Downloads", "Watch", "Listen", "Read"];

    private static readonly IReadOnlyDictionary<string, ShellDockEntry> Catalog =
        new Dictionary<string, ShellDockEntry>(StringComparer.OrdinalIgnoreCase)
        {
            ["Home"] = new("Home", "/", "Nav_Home", AppMaterialIcons.Outlined.Home),
            ["Libraries"] = new("Libraries", DefaultLibraryPath, "Nav_Libraries", AppMaterialIcons.Outlined.LibraryBooks, ShellDockKind.Libraries),
            ["Search"] = new("Search", "/search", "Nav_Search", AppMaterialIcons.Outlined.Search, ShellDockKind.Search),
            ["Downloads"] = new("Downloads", DownloadsPath, "Nav_Downloads", AppMaterialIcons.Outlined.Download),
            ["Settings"] = new("Settings", SettingsPath, "Nav_Settings", AppMaterialIcons.Outlined.Settings),
            ["Read"] = new("Read", "/read", "Nav_Read", AppMaterialIcons.Outlined.MenuBook),
            ["Watch"] = new("Watch", "/watch", "Nav_Watch", AppMaterialIcons.Outlined.LiveTv),
            ["Listen"] = new("Listen", "/listen", "Nav_Listen", AppMaterialIcons.Outlined.Headphones),
            ["View"] = new("View", "/view", "Nav_View", AppMaterialIcons.Outlined.PhotoLibrary),
            ["Collections"] = new("Collections", "/collections", "Nav_Collections", AppMaterialIcons.Outlined.CollectionsBookmark),
        };

    /// <summary>The tabs for the configured item names, in order, after the Kids and Downloads rules.</summary>
    public static IReadOnlyList<ShellDockEntry> Resolve(IEnumerable<string> items, bool isKids, DownloadsTabMode downloadsMode = DownloadsMode)
    {
        var entries = new List<ShellDockEntry>();
        foreach (var item in items)
        {
            if (!Catalog.TryGetValue(item, out var entry)) continue;
            if (isKids && !KidsDockKeys.Contains(entry.Key, StringComparer.OrdinalIgnoreCase)) continue;
            if (downloadsMode == DownloadsTabMode.Hidden && entry.Key == "Downloads") continue;
            if (entries.Any(existing => string.Equals(existing.Path, entry.Path, StringComparison.OrdinalIgnoreCase))) continue;
            entries.Add(entry);
        }
        return entries;
    }

    /// <summary>The rows of the Libraries switcher. Kids only see the lanes a child needs, and no Recently added.</summary>
    public static IReadOnlyList<LibrarySurface> SwitcherRows(bool isKids) => isKids
        ? KidsLibraryPaths.Select(path => LibrarySurfaces.First(surface => surface.Path == path)).ToArray()
        : [.. LibrarySurfaces, RecentlyAdded];

    /// <summary>True on any library surface (a lane, a lane's sub-page, Collections or Recently added).</summary>
    public static bool IsLibrarySurface(string path) =>
        LibrarySurfaces.Any(surface => MatchesPath(path, surface.Path)) || MatchesPath(path, RecentlyAdded.Path);

    /// <summary>The lane to remember for a location, or null when the location is not a lane.</summary>
    public static string? LaneForPath(string path) =>
        LibrarySurfaces.FirstOrDefault(surface => MatchesPath(path, surface.Path)).Path;

    /// <summary>A stored value turned into a lane this profile may open, falling back to Watch.</summary>
    public static string NormalizeLastLibrary(string? stored, bool isKids)
    {
        var lane = stored is null ? null : LibrarySurfaces.FirstOrDefault(surface => string.Equals(surface.Path, stored, StringComparison.OrdinalIgnoreCase)).Path;
        if (lane is null) return DefaultLibraryPath;
        return isKids && !KidsLibraryPaths.Contains(lane, StringComparer.OrdinalIgnoreCase) ? DefaultLibraryPath : lane;
    }

    public static string LastLibraryStorageKey(Guid? profileId) =>
        LastLibraryStorageKeyPrefix + (profileId?.ToString("N") ?? "default");

    private static bool MatchesPath(string path, string prefix) =>
        path.Equals(prefix, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase);
}
