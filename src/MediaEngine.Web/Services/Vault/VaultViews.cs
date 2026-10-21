using MediaEngine.Web.Services.Navigation;

namespace MediaEngine.Web.Services.Vault;

/// <summary>One view of the Vault area (Videos spec D39).</summary>
public readonly record struct VaultView(string Key, string Label);

/// <summary>
/// The views of the Vault area. The main page shows videos and images; Performers, Studios and Tags are their own lists and are never
/// mixed into it (D39). Library settings live inside the area, for administrators only (D45).
/// </summary>
public static class VaultViews
{
    public const string RecentlyAdded = "recently-added";
    public const string LibrarySettings = "library-settings";

    public static readonly IReadOnlyList<VaultView> All =
    [
        new(RecentlyAdded, "Recently added"),
        new("scenes", "Scenes"),
        new("folders", "Folders"),
        new("unmatched", "Unmatched"),
        new("images", "Images"),
        new("performers", "Performers"),
        new("studios", "Studios"),
        new("tags", "Tags"),
    ];

    public static readonly VaultView Settings = new(LibrarySettings, "Library settings");

    /// <summary>The view for a route segment: the default (Recently added) when it is missing, null when it is not a view.</summary>
    public static VaultView? Resolve(string? key, bool isAdministrator)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return All[0];
        }

        if (isAdministrator && string.Equals(key, LibrarySettings, StringComparison.OrdinalIgnoreCase))
        {
            return Settings;
        }

        foreach (var view in All)
        {
            if (string.Equals(view.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                return view;
            }
        }

        return null;
    }

    /// <summary>The views listed in the area's menu.</summary>
    public static IReadOnlyList<VaultView> For(bool isAdministrator) => isAdministrator ? [.. All, Settings] : All;

    public static string Route(string key) => key == RecentlyAdded ? ShellNavigation.VaultPath : $"{ShellNavigation.VaultPath}/{key}";

    /// <summary>The views whose tiles open to a list of the scenes the person owns (a performer, studio, tag or folder).</summary>
    public static bool OpensToScenes(string key) => key is "performers" or "studios" or "tags" or "folders";

    /// <summary>A tile's page: the scenes of one performer, studio, tag or folder (<c>/vault/performers/{id}</c>).</summary>
    public static string TileRoute(string key, string id) => $"{ShellNavigation.VaultPath}/{key}/{Uri.EscapeDataString(id)}";

    /// <summary>One scene's own page, where Identify and playing live.</summary>
    public static string ScenePath(Guid assetId) => $"{ShellNavigation.VaultPath}/scene/{assetId:D}";
}
