using MediaEngine.Contracts.Library;
using MediaEngine.Contracts.Settings;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Formatting;

namespace MediaEngine.Web.Components.Settings;

/// <summary>One row on a group page, and one line of a group card: what a library type holds and how its folders are doing.</summary>
/// <param name="Folders">Folder count; null only for View while its folders are not known yet.</param>
/// <param name="Titles">Titles the Engine counts for the type; null when the overview is not available or the type has no count.</param>
public sealed record LibraryRowSummary(LibraryTypeOption Type, int? Folders, int? Titles, LibraryHealth Health);

/// <summary>A group card: its rows plus their totals and the worst health among them.</summary>
public sealed record LibraryGroupSummary(LibraryGroup Group, IReadOnlyList<LibraryRowSummary> Rows, int? Folders, int? Titles, LibraryHealth Health);

/// <summary>
/// Works out what the Libraries front door and group pages show from the library configuration, the Engine's overview counts
/// and the folder checks. The panel uses the same lookups, so a type means the same thing on every level.
/// </summary>
public static class LibraryRollup
{
    /// <summary>The one library of a standard type: the catalogued library carrying one of its media types.</summary>
    public static LibraryFolderDto? PrimaryLibraryFor(LibrariesConfigurationDto? configuration, LibraryTypeOption type) => configuration?.Libraries
        .FirstOrDefault(library => IsCatalogued(library)
            && library.MediaTypes.Any(token => type.MediaTypes.Contains(token, StringComparer.OrdinalIgnoreCase)));

    public static bool IsCatalogued(LibraryFolderDto library) => string.Equals(library.Kind, "catalogued", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Libraries that are not the one library of a standard type (an older custom type, or a second library of a standard
    /// type), so their folders can be seen and removed instead of silently hiding. Their key is "other-{libraryId}".
    /// </summary>
    public static IReadOnlyList<LibraryTypeOption> OtherLibraries(LibrariesConfigurationDto? configuration) => configuration is null
        ? []
        : configuration.Libraries
            .Where(library => IsCatalogued(library) && !LibraryTypeCatalog.Types.Any(type => ReferenceEquals(PrimaryLibraryFor(configuration, type), library)))
            .Select(library => new LibraryTypeOption(
                OtherKey(library.Id),
                string.IsNullOrWhiteSpace(library.Name) ? "Other library" : library.Name,
                string.IsNullOrWhiteSpace(library.Name) ? "other" : library.Name,
                library.Area,
                [.. library.MediaTypes],
                OtherIcon(library.Area),
                library.Id))
            .ToList();

    public static string OtherKey(string libraryId) => $"other-{libraryId}";

    public static string OtherIcon(string? area) => area?.ToLowerInvariant() switch
    {
        "read" => AppMaterialIcons.Outlined.MenuBook,
        "watch" => AppMaterialIcons.Outlined.Movie,
        "listen" => AppMaterialIcons.Outlined.Headphones,
        _ => AppMaterialIcons.Outlined.Folder,
    };

    /// <summary>Other libraries whose area belongs to <paramref name="group"/>.</summary>
    public static IReadOnlyList<LibraryTypeOption> OthersIn(LibrariesConfigurationDto? configuration, LibraryGroup group) =>
        [.. OtherLibraries(configuration).Where(other => LibraryGroupCatalog.ForArea(other.Area)?.Key == group.Key)];

    /// <summary>Other libraries with no known area; the front door lists them under the Import folder row.</summary>
    public static IReadOnlyList<LibraryTypeOption> OthersWithoutGroup(LibrariesConfigurationDto? configuration) =>
        [.. OtherLibraries(configuration).Where(other => LibraryGroupCatalog.ForArea(other.Area) is null)];

    public static LibraryFolderDto? LibraryFor(LibrariesConfigurationDto? configuration, LibraryTypeOption type) => type.LibraryId is not null
        ? configuration?.Libraries.FirstOrDefault(library => library.Id == type.LibraryId)
        : PrimaryLibraryFor(configuration, type);

    /// <summary>The counts and health of one type. <paramref name="viewFolders"/> is View's own folder count (Photos has no library).</summary>
    public static LibraryRowSummary RowFor(
        LibraryTypeOption type,
        LibrariesConfigurationDto configuration,
        LibraryOverviewDto? overview,
        int? viewFolders,
        IReadOnlyDictionary<string, string> issues)
    {
        if (type.Key == LibraryTypeCatalog.PhotosKey)
        {
            // View keeps its folders outside the library list and the Engine counts no titles for it, so only folders show.
            return new LibraryRowSummary(type, viewFolders, null, LibraryHealth.Ok);
        }

        var library = LibraryFor(configuration, type);
        var sources = library?.Sources ?? [];
        return new LibraryRowSummary(type, sources.Count, TitlesFor(type, overview), LibraryFolderHealth.Worst(sources, issues));
    }

    /// <summary>The rows of a group page: its types in order, then that area's other libraries.</summary>
    public static IReadOnlyList<LibraryRowSummary> RowsFor(
        LibraryGroup group,
        LibrariesConfigurationDto configuration,
        LibraryOverviewDto? overview,
        int? viewFolders,
        IReadOnlyDictionary<string, string> issues) =>
        [.. group.TypeKeys
            .Select(LibraryTypeCatalog.Find)
            .OfType<LibraryTypeOption>()
            .Concat(OthersIn(configuration, group))
            .Select(type => RowFor(type, configuration, overview, viewFolders, issues))];

    public static LibraryGroupSummary SummaryFor(
        LibraryGroup group,
        LibrariesConfigurationDto configuration,
        LibraryOverviewDto? overview,
        int? viewFolders,
        IReadOnlyDictionary<string, string> issues)
    {
        var rows = RowsFor(group, configuration, overview, viewFolders, issues);
        int? folders = rows.Any(row => row.Folders is null) ? null : rows.Sum(row => row.Folders!.Value);
        int? titles = overview is null || rows.All(row => row.Titles is null) ? null : rows.Sum(row => row.Titles ?? 0);
        var sources = rows.Where(row => row.Type.Key != LibraryTypeCatalog.PhotosKey)
            .SelectMany(row => LibraryFor(configuration, row.Type)?.Sources ?? []);
        return new LibraryGroupSummary(group, rows, folders, titles, LibraryFolderHealth.Worst(sources, issues));
    }

    /// <summary>The titles the Engine counts for a type, or null when the overview could not be read.</summary>
    public static int? TitlesFor(LibraryTypeOption type, LibraryOverviewDto? overview) => overview is null
        ? null
        : overview.MediaTypeCounts
            .Where(pair => type.MediaTypes.Contains(pair.Key, StringComparer.OrdinalIgnoreCase))
            .Sum(pair => pair.Value);

    public static string FolderText(int count) => count == 1 ? "1 folder" : $"{DisplayFormat.FormatCount(count)} folders";

    public static string TitleText(int count) => count == 1 ? "1 title" : $"{DisplayFormat.FormatCount(count)} titles";

    /// <summary>"3 folders · 1,284 titles", "1 folder", "Nothing added yet", or nothing at all when the folder count is not known.</summary>
    public static string CountsText(int? folders, int? titles) => folders switch
    {
        null => string.Empty,
        0 => "Nothing added yet",
        _ => titles is null ? FolderText(folders.Value) : $"{FolderText(folders.Value)} · {TitleText(titles.Value)}",
    };
}
