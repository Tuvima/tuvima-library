using MediaEngine.Contracts.Library;
using MediaEngine.Contracts.LocalAssets;
using MediaEngine.Contracts.Settings;
using MediaEngine.Domain.PersonalMedia;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Formatting;

namespace MediaEngine.Web.Components.Settings;

/// <summary>One row on a group page, and one line of a group card: what a library type holds and how its folders are doing.</summary>
/// <param name="Folders">Folder count; null only for a View kind while its folders are not known yet.</param>
/// <param name="Titles">Titles the Engine counts for the type; null when the overview is not available or the type has no count.</param>
/// <param name="Unmatched">Files in the row's library (or libraries) the Engine could not match; kept-as-is files are not counted.</param>
/// <param name="SharesKindTitles">True for one of several named libraries of a kind: the Engine counts titles per media type, so the kind's count cannot be split between them.</param>
public sealed record LibraryRowSummary(LibraryTypeOption Type, int? Folders, int? Titles, LibraryHealth? Health, int Unmatched = 0, bool SharesKindTitles = false, bool Paused = false);

/// <summary>A group card: its rows plus their totals and the worst health among them.</summary>
public sealed record LibraryGroupSummary(LibraryGroup Group, IReadOnlyList<LibraryRowSummary> Rows, int? Folders, int? Titles, LibraryHealth? Health);

/// <summary>
/// Works out what the Libraries front door and group pages show from the library configuration, the Engine's overview counts
/// and the folder checks. The panel uses the same lookups, so a type means the same thing on every level.
/// </summary>
public static class LibraryRollup
{
    /// <summary>The one library of a kind: the catalogued library that names the kind. A Vault-protected library sits beside it and is never the one library of the kind.</summary>
    public static LibraryFolderDto? PrimaryLibraryFor(LibrariesConfigurationDto? configuration, LibraryTypeOption type) => configuration?.Libraries
        .FirstOrDefault(library => IsCatalogued(library) && !LibraryLookupRules.IsVaultLibrary(library) && string.Equals(library.KindKey, type.Key, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Every library a kind holds: for a kind that allows several named libraries (the plain kinds) each one, in the saved order; for any
    /// other kind its one library.
    /// </summary>
    public static IReadOnlyList<LibraryFolderDto> LibrariesOf(LibrariesConfigurationDto? configuration, LibraryTypeOption type)
    {
        if (configuration is null)
        {
            return [];
        }

        if (!type.AllowsMultiple)
        {
            return PrimaryLibraryFor(configuration, type) is { } only ? [only] : [];
        }

        return [.. configuration.Libraries.Where(library => IsCatalogued(library) && !LibraryLookupRules.IsVaultLibrary(library) && string.Equals(library.KindKey, type.Key, StringComparison.OrdinalIgnoreCase))];
    }

    public static bool IsCatalogued(LibraryFolderDto library) => string.Equals(library.Kind, "catalogued", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Libraries that are not the one library of a kind (one that names no available kind, or a second library of a kind), so
    /// their folders can be seen and removed instead of silently hiding. Their key is "other-{libraryId}".
    /// </summary>
    public static IReadOnlyList<LibraryTypeOption> OtherLibraries(LibraryKindCatalog kinds, LibrariesConfigurationDto? configuration) => configuration is null
        ? []
        : configuration.Libraries
            .Where(library => IsCatalogued(library) && !LibraryLookupRules.IsVaultLibrary(library) && !kinds.Types.Any(type => LibrariesOf(configuration, type).Any(held => ReferenceEquals(held, library))))
            .Select(library => new LibraryTypeOption(
                OtherKey(library.Id),
                string.IsNullOrWhiteSpace(library.Name) ? "Other library" : library.Name,
                string.IsNullOrWhiteSpace(library.Name) ? "other" : library.Name,
                library.Area,
                [.. library.MediaTypes],
                kinds.Find(library.KindKey)?.Icon ?? OtherIcon(library.Area),
                library.Id,
                LookupProviders: kinds.Find(library.KindKey)?.LookupProviders))
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
    public static IReadOnlyList<LibraryTypeOption> OthersIn(LibraryKindCatalog kinds, LibrariesConfigurationDto? configuration, LibraryGroup group) =>
        [.. OtherLibraries(kinds, configuration).Where(other => kinds.ForArea(other.Area)?.Key == group.Key)];

    /// <summary>Other libraries with no known area; the front door lists them under the Import folder row.</summary>
    public static IReadOnlyList<LibraryTypeOption> OthersWithoutGroup(LibraryKindCatalog kinds, LibrariesConfigurationDto? configuration) =>
        [.. OtherLibraries(kinds, configuration).Where(other => kinds.ForArea(other.Area) is null)];

    public static LibraryFolderDto? LibraryFor(LibrariesConfigurationDto? configuration, LibraryTypeOption type) => type.LibraryId is not null
        ? configuration?.Libraries.FirstOrDefault(library => library.Id == type.LibraryId)
        : PrimaryLibraryFor(configuration, type);

    /// <summary>
    /// True when a View folder belongs on the page of this View kind: its own kind, or, for the phone-backup and upload folders
    /// that take pictures and videos together, Photos and Personal videos alike.
    /// </summary>
    public static bool ViewFolderBelongsTo(ViewSourceAdminDto source, string kind) =>
        string.Equals(source.ContentKind, kind, StringComparison.OrdinalIgnoreCase)
        || (source.AcceptsPhotosAndVideos && kind is ViewContentKinds.Photos or ViewContentKinds.PersonalVideos);

    /// <summary>
    /// The counts and health of one type. <paramref name="viewFolders"/> is View's own folder count per kind (a View kind has no
    /// library); null while it is not known.
    /// </summary>
    public static LibraryRowSummary RowFor(
        LibraryTypeOption type,
        LibrariesConfigurationDto configuration,
        LibraryOverviewDto? overview,
        IReadOnlyDictionary<string, int>? viewFolders,
        IReadOnlyDictionary<string, string>? issues)
    {
        if (LibraryTypeCatalog.IsViewKind(type))
        {
            // View keeps its folders outside the library list and the Engine counts no titles for it, so only folders show.
            return new LibraryRowSummary(type, viewFolders is null ? null : viewFolders.GetValueOrDefault(type.Key), null, null);
        }

        var library = LibraryFor(configuration, type);
        var sources = library?.Sources ?? [];
        return new LibraryRowSummary(type, sources.Count, TitlesFor(type, overview), issues is null ? null : LibraryFolderHealth.Worst(sources, issues), UnmatchedFor(configuration, type), Paused: PausedFor(configuration, type));
    }

    /// <summary>The Unmatched files of a row: its own library's count, or the sum over the libraries a bare kind holds.</summary>
    public static int UnmatchedFor(LibrariesConfigurationDto configuration, LibraryTypeOption type) => type.LibraryId is not null
        ? LibraryFor(configuration, type)?.UnmatchedCount ?? 0
        : LibrariesOf(configuration, type).Sum(library => library.UnmatchedCount);

    /// <summary>True when any folder of the row's library (or of the libraries a bare kind holds) has its first check paused (Library setup D35).</summary>
    public static bool PausedFor(LibrariesConfigurationDto configuration, LibraryTypeOption type) =>
        (type.LibraryId is not null ? [.. new[] { LibraryFor(configuration, type) }.OfType<LibraryFolderDto>()] : LibrariesOf(configuration, type))
            .Any(library => library.Sources.Any(source => source.FirstCheck is { State: "paused" }));

    /// <summary>
    /// The rows of a group page: its types in order, then that area's other libraries. A kind that allows several named libraries
    /// (Other videos and its kind) has one row per library, each under the name its owner chose; with none yet it keeps its own row.
    /// </summary>
    public static IReadOnlyList<LibraryRowSummary> RowsFor(
        LibraryKindCatalog kinds,
        LibraryGroup group,
        LibrariesConfigurationDto configuration,
        LibraryOverviewDto? overview,
        IReadOnlyDictionary<string, int>? viewFolders,
        IReadOnlyDictionary<string, string>? issues)
    {
        var rows = new List<LibraryRowSummary>();
        foreach (var type in group.TypeKeys.Select(kinds.Find).OfType<LibraryTypeOption>())
        {
            var named = type.AllowsMultiple ? LibrariesOf(configuration, type) : [];
            if (named.Count == 0)
            {
                rows.Add(RowFor(type, configuration, overview, viewFolders, issues));
                continue;
            }

            foreach (var library in named)
            {
                var name = string.IsNullOrWhiteSpace(library.Name) ? type.Label : library.Name;
                var row = RowFor(type with { Label = name, PluralLabel = name, LibraryId = library.Id }, configuration, overview, viewFolders, issues);
                rows.Add(named.Count > 1 ? row with { Titles = null, SharesKindTitles = true } : row);
            }
        }

        rows.AddRange(OthersIn(kinds, configuration, group).Select(type => RowFor(type, configuration, overview, viewFolders, issues)));
        return rows;
    }

    public static LibraryGroupSummary SummaryFor(
        LibraryKindCatalog kinds,
        LibraryGroup group,
        LibrariesConfigurationDto configuration,
        LibraryOverviewDto? overview,
        IReadOnlyDictionary<string, int>? viewFolders,
        IReadOnlyDictionary<string, string>? issues)
    {
        // The Vault's folders are kept out of the library settings the Dashboard reads, so its strip shows no count rather than a wrong zero.
        if (group.Key == LibraryGroupCatalog.VaultKey)
        {
            return new LibraryGroupSummary(group, [], null, null, null);
        }

        var rows = RowsFor(kinds, group, configuration, overview, viewFolders, issues);
        int? folders = rows.Any(row => row.Folders is null) ? null : rows.Sum(row => row.Folders!.Value);
        // The libraries of one kind share its title count (the Engine counts per media type), so it is added once for them all.
        var sharedTitles = rows.Where(row => row.SharesKindTitles).GroupBy(row => row.Type.Key).Sum(kind => TitlesFor(kind.First().Type, overview) ?? 0);
        int? titles = overview is null || rows.All(row => row.Titles is null && !row.SharesKindTitles) ? null : rows.Sum(row => row.Titles ?? 0) + sharedTitles;
        var sources = rows.Where(row => !LibraryTypeCatalog.IsViewKind(row.Type))
            .SelectMany(row => LibraryFor(configuration, row.Type)?.Sources ?? []);
        // View's folders are never checked, so a group of only View has no health to show; others wait for the checks.
        var checkedRows = rows.Where(row => !LibraryTypeCatalog.IsViewKind(row.Type)).ToList();
        var health = issues is null || checkedRows.Count == 0 ? null : LibraryFolderHealth.Worst(sources, issues);
        return new LibraryGroupSummary(group, rows, folders, titles, health);
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
