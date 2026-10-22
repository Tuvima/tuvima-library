using MediaEngine.Web.Components.Shared;

namespace MediaEngine.Web.Components.Settings;

/// <summary>One kind of library the Libraries screen lists. <paramref name="LibraryId"/> is set only for a library that is not a standard type.</summary>
public sealed record LibraryTypeOption(string Key, string Label, string PluralLabel, string Area, string[] MediaTypes, string Icon, string? LibraryId = null);

/// <summary>The standard library types, shared by the folders panel (first-run setup and Settings), the group pages and the front door.</summary>
public static class LibraryTypeCatalog
{
    public const string ImportKey = "import";
    public const string PhotosKey = "photos";

    public static IReadOnlyList<LibraryTypeOption> Types { get; } =
    [
        new("books", "Books", "Books", "read", ["Books"], AppMediaPresentation.IconFor("Books", outlined: true)),
        new("audiobooks", "Audiobooks", "Audiobooks", "listen", ["Audiobooks"], AppMediaPresentation.IconFor("Audiobooks", outlined: true)),
        new("movies", "Movies", "Movies", "watch", ["Movies"], AppMediaPresentation.IconFor("Movies", outlined: true)),
        new("tv", "TV Shows", "TV shows", "watch", ["TV", "TVShows"], AppMediaPresentation.IconFor("TV", outlined: true)),
        new("music", "Music", "Music", "listen", ["Music"], AppMediaPresentation.IconFor("Music", outlined: true)),
        new("comics", "Comics", "Comics", "read", ["Comics"], AppMediaPresentation.IconFor("Comics", outlined: true)),
        new("videos", "Videos", "Videos", "watch", ["Videos"], AppMediaPresentation.IconFor("Videos", outlined: true)),
    ];

    /// <summary>View's own folder store. It is not a library type in the Work/Edition graph, so it is not in <see cref="Types"/>.</summary>
    public static LibraryTypeOption Photos { get; } =
        new(PhotosKey, "Photos & personal videos", "photos and personal videos", "view", [], AppMaterialIcons.Outlined.PhotoLibrary);

    /// <summary>A standard type (or Photos) by key, or null.</summary>
    public static LibraryTypeOption? Find(string? key) =>
        string.Equals(key, PhotosKey, StringComparison.OrdinalIgnoreCase)
            ? Photos
            : Types.FirstOrDefault(type => string.Equals(type.Key, key, StringComparison.OrdinalIgnoreCase));
}

/// <summary>One step of the breadcrumb on the group and type pages; no <paramref name="Href"/> means the current page.</summary>
public sealed record LibraryCrumb(string Label, string? Href = null);

/// <summary>What the Settings frame shows for one Libraries address: its title and the breadcrumb steps from "Libraries" down (none on the front door).</summary>
public sealed record LibraryLevelDescription(string Title, IReadOnlyList<LibraryCrumb> Crumbs);

/// <summary>
/// Reads a Libraries address (/settings/libraries/{subsection}/{detail}) and says what the Settings frame should show above the
/// content: the frame owns the breadcrumb and the title, so the pages draw neither. An address that names nothing (or the Vault
/// while it is hidden) reads as the front door; the page itself shows its "does not exist" state.
/// </summary>
public static class LibraryAddress
{
    public const string FrontDoorTitle = "Libraries";
    private const string OtherLibraryTitle = "Other library";
    private const string LibrariesAddress = "/settings/libraries";

    public static LibraryLevelDescription Describe(string? subsection, string? detail, bool showVault)
    {
        var front = new LibraryLevelDescription(FrontDoorTitle, []);
        var sub = subsection?.Trim();
        var det = detail?.Trim();
        var libraries = new LibraryCrumb(FrontDoorTitle, LibrariesAddress);
        if (string.IsNullOrEmpty(sub))
        {
            return front;
        }

        if (string.Equals(sub, LibraryTypeCatalog.ImportKey, StringComparison.OrdinalIgnoreCase))
        {
            return string.IsNullOrEmpty(det) ? Level("Import folder", libraries) : front;
        }

        if (string.Equals(sub, "other", StringComparison.OrdinalIgnoreCase))
        {
            return det is not null && det.StartsWith("other-", StringComparison.OrdinalIgnoreCase) ? Level(OtherLibraryTitle, libraries) : front;
        }

        if (LibraryGroupCatalog.TryGetVisibleGroup(sub, showVault) is not { } group)
        {
            return front;
        }

        var groupCrumb = new LibraryCrumb(group.Label, LibraryGroupCatalog.GroupAddress(group));
        if (string.IsNullOrEmpty(det))
        {
            return Level(group.Label, libraries);
        }

        if (group.Key == "view")
        {
            return string.Equals(det, LibraryTypeCatalog.PhotosKey, StringComparison.OrdinalIgnoreCase)
                ? Level(LibraryTypeCatalog.Photos.Label, libraries, groupCrumb)
                : front;
        }

        if (group.TypeKeys.FirstOrDefault(key => string.Equals(key, det, StringComparison.OrdinalIgnoreCase)) is { } typeKey)
        {
            return Level(LibraryTypeCatalog.Find(typeKey)!.Label, libraries, groupCrumb);
        }

        return det.StartsWith("other-", StringComparison.OrdinalIgnoreCase) ? Level(OtherLibraryTitle, libraries, groupCrumb) : front;
    }

    // The last step is the page itself, so it has no link.
    private static LibraryLevelDescription Level(string title, params LibraryCrumb[] ancestors) =>
        new(title, [.. ancestors, new LibraryCrumb(title)]);
}

/// <summary>A way of presenting library types: Watch, Listen, Read, View, Vault. A group has no folders of its own.</summary>
public sealed record LibraryGroup(string Key, string Label, string Icon, IReadOnlyList<string> TypeKeys);

/// <summary>The five groups, in the order the Libraries front door shows them (icons match the main navigation).</summary>
public static class LibraryGroupCatalog
{
    public const string VaultKey = "vault";

    public static IReadOnlyList<LibraryGroup> Groups { get; } =
    [
        new("watch", "Watch", AppMaterialIcons.Outlined.LiveTv, ["movies", "tv", "videos"]),
        new("listen", "Listen", AppMaterialIcons.Outlined.Headphones, ["music", "audiobooks"]),
        new("read", "Read", AppMaterialIcons.Outlined.MenuBook, ["books", "comics"]),
        new("view", "View", AppMaterialIcons.Outlined.PhotoLibrary, [LibraryTypeCatalog.PhotosKey]),
        // Vault has no types yet; they arrive with the Videos Vault work.
        new(VaultKey, "Vault", AppMaterialIcons.Outlined.Lock, []),
    ];

    /// <summary>The group a standard type (or Photos) belongs to, or null for an unknown key.</summary>
    public static LibraryGroup? GroupFor(string? typeKey) =>
        Groups.FirstOrDefault(group => group.TypeKeys.Contains(typeKey ?? string.Empty, StringComparer.OrdinalIgnoreCase));

    public static LibraryGroup? TryGetGroup(string? key) =>
        Groups.FirstOrDefault(group => string.Equals(group.Key, key?.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>A group by key, only when it may be shown: the Vault is not found while Vault mode is off.</summary>
    public static LibraryGroup? TryGetVisibleGroup(string? key, bool showVault) =>
        Visible(showVault).FirstOrDefault(group => string.Equals(group.Key, key?.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>The group a catalogued library with this area sits under, or null when the area is unknown.</summary>
    public static LibraryGroup? ForArea(string? area) =>
        string.Equals(area?.Trim(), VaultKey, StringComparison.OrdinalIgnoreCase) ? null : TryGetGroup(area);

    /// <summary>The groups to draw: Vault is last and only present when it may be shown.</summary>
    public static IReadOnlyList<LibraryGroup> Visible(bool showVault) =>
        showVault ? Groups : [.. Groups.Where(group => group.Key != VaultKey)];

    /// <summary>The address of a group page ("/settings/libraries/watch").</summary>
    public static string GroupAddress(LibraryGroup group) => $"/settings/libraries/{group.Key}";

    /// <summary>The address of a type page ("/settings/libraries/watch/movies"); "other-{libraryId}" for a library that is not a standard type.</summary>
    public static string TypeAddress(LibraryGroup group, string typeKey) => $"/settings/libraries/{group.Key}/{typeKey}";
}
