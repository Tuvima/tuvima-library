using MediaEngine.Contracts.Library;
using MediaEngine.Contracts.LocalAssets;
using MediaEngine.Contracts.Settings;
using MediaEngine.Domain.PersonalMedia;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Formatting;

namespace MediaEngine.Web.Components.Settings;

/// <summary>
/// Looks up the libraries of a kind in the library configuration, and words the counts the Libraries pages show. The overview, the
/// library pages and the folders panel use the same lookups, so a type means the same thing on every level.
/// </summary>
public static class LibraryRollup
{
    /// <summary>The one library of a kind: the catalogued library that names the kind.</summary>
    public static LibraryFolderDto? PrimaryLibraryFor(LibrariesConfigurationDto? configuration, LibraryTypeOption type) => configuration?.Libraries
        .FirstOrDefault(library => IsCatalogued(library) && string.Equals(library.KindKey, type.Key, StringComparison.OrdinalIgnoreCase));

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

        return [.. configuration.Libraries.Where(library => IsCatalogued(library) && string.Equals(library.KindKey, type.Key, StringComparison.OrdinalIgnoreCase))];
    }

    public static bool IsCatalogued(LibraryFolderDto library) => string.Equals(library.Kind, "catalogued", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Libraries that are not the one library of a kind (one that names no available kind, or a second library of a kind), so
    /// their folders can be seen and removed instead of silently hiding. Their key is "other-{libraryId}".
    /// </summary>
    public static IReadOnlyList<LibraryTypeOption> OtherLibraries(LibraryKindCatalog kinds, LibrariesConfigurationDto? configuration) => configuration is null
        ? []
        : configuration.Libraries
            .Where(library => IsCatalogued(library) && !kinds.Types.Any(type => LibrariesOf(configuration, type).Any(held => ReferenceEquals(held, library))))
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
