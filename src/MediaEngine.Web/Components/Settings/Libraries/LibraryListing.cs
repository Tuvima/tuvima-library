using MediaEngine.Contracts.Library;
using MediaEngine.Contracts.Settings;
using MediaEngine.Web.Models.ViewDTOs;

namespace MediaEngine.Web.Components.Settings;

/// <summary>The status words of a library (Library pages D17).</summary>
public enum LibraryStatusKind
{
    Healthy,
    NotIdentified,
    NeedsReview,
    Empty,
    FolderOffline,
    Scanning,
}

/// <summary>
/// One row of the Libraries overview: a single library (never a content type), or one View kind. <paramref name="Status"/> is null while
/// it is not known (the folder checks have not finished, or View's folders are never checked); a row never claims a health it does not know.
/// </summary>
/// <param name="Key">The library's Id from the libraries file, or the View kind's key.</param>
/// <param name="Access"><c>standard</c>, <c>selected</c> or <c>vault</c>; null for a View kind, which keeps its own per-profile rules.</param>
/// <param name="Folders">Folder count; null only for a View kind while its folders are not known yet.</param>
/// <param name="Titles">Titles the Engine counts for the kind; null when not known or when several libraries share the kind (the Engine counts per media type).</param>
/// <param name="Unidentified">Files in the library the Engine could not identify (its unmatched-files list; opens the Unidentified tab).</param>
/// <param name="NeedsReview">Identified items waiting in Needs Review (a different list from <paramref name="Unidentified"/>).</param>
/// <param name="StatusDetail">The words of the folder problem, for the tooltip of a Folder offline status.</param>
public sealed record LibraryListRow(
    string Key,
    LibraryFolderDto? Library,
    string Name,
    LibraryTypeOption Type,
    string Area,
    string? Access,
    int? Folders,
    int? Titles,
    LibraryStatusKind? Status,
    int Unidentified,
    int NeedsReview,
    string? StatusDetail,
    string Href)
{
    public bool IsView => Library is null;
}

/// <summary>
/// Works out the rows of the Libraries overview (one per library, so several libraries of one kind are each their own row) and the
/// words of a library's status. The library page uses the same functions for its header, so a library reads the same on both.
/// </summary>
public static class LibraryListing
{
    public const string Standard = "standard";
    public const string Selected = "selected";
    public const string Vault = "vault";

    public static string AddressOf(string libraryId) => $"/settings/libraries/{libraryId}";

    public static string ViewAddressOf(string viewKind) => $"/settings/libraries/{LibraryTypeCatalog.ViewArea}/{viewKind}";

    /// <summary>The overview address with an area tab selected (the tab lives in the address so Back and a reload keep it).</summary>
    public static string OverviewAddress(string? area) => string.IsNullOrWhiteSpace(area) ? "/settings/libraries" : $"/settings/libraries?area={area}";

    /// <summary>The library kind a library holds, or a stand-in for a kind this Library does not offer, so its folders can still be seen.</summary>
    public static LibraryTypeOption TypeOf(LibraryKindCatalog kinds, LibraryFolderDto library) =>
        kinds.Find(library.KindKey) ?? new LibraryTypeOption(
            string.IsNullOrWhiteSpace(library.KindKey) ? "other" : library.KindKey,
            string.IsNullOrWhiteSpace(library.Name) ? "Other library" : library.Name,
            string.IsNullOrWhiteSpace(library.Name) ? "other" : library.Name,
            library.Area,
            [.. library.MediaTypes],
            LibraryRollup.OtherIcon(library.Area),
            library.Id);

    /// <summary>The area (watch, listen, read) a library sits under: its own, else the one of its kind; null when neither is known.</summary>
    public static string? AreaOf(LibraryKindCatalog kinds, LibraryFolderDto library)
    {
        if (kinds.ForArea(library.Area) is { } own)
        {
            return own.Key;
        }

        return kinds.Find(library.KindKey) is { } kind && kinds.ForArea(kind.Area) is { } byKind ? byKind.Key : null;
    }

    public static string AccessOf(LibraryFolderDto library) => library.IsVault ? Vault : Standard;

    public static string AccessText(string? access) => access switch
    {
        Vault => "Vault-protected",
        Standard => "Standard",
        _ => "Per profile",
    };

    /// <summary>
    /// The rows of one area. A Vault-protected library is listed only in the Vault area, and that area has rows only when <paramref name="showVault"/>
    /// says the Vault may be shown (an unlocked administrator); otherwise there is no trace of it. The Engine's own answer decides whether such a library exists.
    /// </summary>
    public static IReadOnlyList<LibraryListRow> RowsFor(
        LibraryKindCatalog kinds,
        string area,
        LibrariesConfigurationDto configuration,
        LibraryOverviewDto? overview,
        IReadOnlyDictionary<string, int>? viewFolders,
        IReadOnlyDictionary<string, string>? issues,
        bool showVault)
    {
        if (string.Equals(area, LibraryTypeCatalog.ViewArea, StringComparison.OrdinalIgnoreCase))
        {
            return [.. kinds.ViewKinds.Select(kind => ViewRowFor(kind, viewFolders?.GetValueOrDefault(kind.Key)))];
        }

        var inVault = string.Equals(area, LibraryGroupCatalog.VaultKey, StringComparison.OrdinalIgnoreCase);
        if (inVault && !showVault)
        {
            return [];
        }

        var rows = new List<LibraryListRow>();
        foreach (var library in configuration.Libraries.Where(LibraryRollup.IsCatalogued))
        {
            // The Vault is its own area (library access D6): its libraries are listed only there, and never in Watch, Listen or Read.
            var isVault = AccessOf(library) == Vault;
            if (isVault != inVault)
            {
                continue;
            }

            if (!inVault && !string.Equals(AreaOf(kinds, library), area, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            rows.Add(RowFor(kinds, library, configuration, overview, issues));
        }

        return rows;
    }

    /// <summary>The row of one library, wherever it sits (the library page's header reads the same facts).</summary>
    public static LibraryListRow RowFor(
        LibraryKindCatalog kinds,
        LibraryFolderDto library,
        LibrariesConfigurationDto configuration,
        LibraryOverviewDto? overview,
        IReadOnlyDictionary<string, string>? issues)
    {
        var type = TypeOf(kinds, library);
        // The Engine counts titles per media type, so a kind held by several libraries cannot be split between them: no guess is shown.
        var sharesKind = configuration.Libraries.Count(other => LibraryRollup.IsCatalogued(other)
            && string.Equals(other.KindKey, library.KindKey, StringComparison.OrdinalIgnoreCase)) > 1;
        int? titles = sharesKind ? null : LibraryRollup.TitlesFor(type, overview);
        var health = issues is null ? null : LibraryFolderHealth.Worst(library.Sources, issues);
        var name = string.IsNullOrWhiteSpace(library.Name) ? type.Label : library.Name;
        var (status, detail) = StatusOf(library, titles, health);
        return new LibraryListRow(
            library.Id,
            library,
            name,
            type,
            AreaOf(kinds, library) ?? library.Area,
            AccessOf(library),
            library.Sources.Count,
            titles,
            status,
            library.UnmatchedCount,
            library.ReviewCount,
            detail,
            AddressOf(library.Id));
    }

    /// <summary>The row of one View kind (the overview and the kind's own page read the same facts); <paramref name="folders"/> is null while it is not known.</summary>
    public static LibraryListRow ViewRowFor(LibraryTypeOption kind, int? folders)
    {
        // View's folders are never checked, so only "Empty" can be said for certain.
        LibraryStatusKind? status = folders == 0 ? LibraryStatusKind.Empty : null;
        return new LibraryListRow(kind.Key, null, kind.Label, kind, LibraryTypeCatalog.ViewArea, null, folders, null, status, 0, 0, null, ViewAddressOf(kind.Key));
    }

    /// <summary>
    /// The worst state first: a folder that is offline, then files that need review, a scan in progress, a library with nothing in it, else healthy.
    /// Null while the folder checks have not finished for a library that has folders.
    /// </summary>
    public static (LibraryStatusKind? Status, string? Detail) StatusOf(LibraryFolderDto library, int? titles, LibraryHealth? health)
    {
        if (library.Sources.Count == 0)
        {
            return (LibraryStatusKind.Empty, null);
        }

        if (health is null)
        {
            return (null, null);
        }

        if (!health.Healthy)
        {
            return (LibraryStatusKind.FolderOffline, health.Text);
        }

        // Two kinds of attention, never mixed (Libraries redesign D3): files nobody could identify, then identified items to confirm.
        if (library.UnmatchedCount > 0)
        {
            return (LibraryStatusKind.NotIdentified, null);
        }

        if (library.ReviewCount > 0)
        {
            return (LibraryStatusKind.NeedsReview, null);
        }

        if (library.Sources.Any(source => source.ScanEnabled && source.FirstCheck is { State: "checking" }))
        {
            return (LibraryStatusKind.Scanning, null);
        }

        return titles == 0 ? (LibraryStatusKind.Empty, null) : (LibraryStatusKind.Healthy, null);
    }

    public static string StatusText(LibraryStatusKind status) => status switch
    {
        LibraryStatusKind.NotIdentified => "Files not identified",
        LibraryStatusKind.NeedsReview => "Needs review",
        LibraryStatusKind.Empty => "Empty",
        LibraryStatusKind.FolderOffline => "Folder offline",
        LibraryStatusKind.Scanning => "Scanning",
        _ => "Healthy",
    };

    /// <summary>The values of the overview's status filter (the status words, and "all").</summary>
    public static string FilterValue(LibraryStatusKind status) => status.ToString();

    /// <summary>Needs Review, with the library as the filter (Library pages D17).</summary>
    /// <summary>The library's Unidentified files tab.</summary>
    public static string UnidentifiedAddress(string libraryId) => $"{AddressOf(libraryId)}?tab=unidentified";

    /// <summary>"12 files not identified" / "1 file not identified".</summary>
    public static string UnidentifiedText(int count) => count == 1 ? "1 file not identified" : $"{count:N0} files not identified";

    /// <summary>"3 need review" / "1 needs review".</summary>
    public static string NeedsReviewText(int count) => count == 1 ? "1 needs review" : $"{count:N0} need review";

    public static string NeedsReviewAddress(string libraryId) => $"{SettingsNav.RouteFor(SettingsSection.Review)}?library={Uri.EscapeDataString(libraryId)}";
}
