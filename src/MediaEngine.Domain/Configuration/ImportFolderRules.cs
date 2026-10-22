namespace MediaEngine.Domain.Configuration;

/// <summary>Where one media type's imported files go: the library's destination folder, or nothing yet.</summary>
public sealed record ImportFolderDestination(string MediaType, string? LibraryId, string? SourceId, string? Path)
{
    public bool HasDestination => !string.IsNullOrWhiteSpace(SourceId);
}

/// <summary>Whether the optional import folder is set and whether every media type has somewhere to put imported media.</summary>
public sealed record ImportFolderStatus(string? Path, IReadOnlyList<string> MissingTypes, IReadOnlyList<ImportFolderDestination> Destinations)
{
    public ImportFolderStatus(string? path, IReadOnlyList<string> missingTypes)
        : this(path, missingTypes, [])
    {
    }

    public bool Configured => !string.IsNullOrWhiteSpace(Path);

    /// <summary>Import runs only when a folder is set and every media type has a destination.</summary>
    public bool Ready => Configured && MissingTypes.Count == 0;
}

/// <summary>
/// The import folder is optional (other apps such as Sonarr and Radarr can drop finished downloads in it).
/// It is the storage location with id <see cref="LocationId"/>. When it is set, each media type needs a destination: the folder
/// Tuvima manages and files that type into. An as-is folder is never a destination. The media types are the available kinds
/// in <c>config/library_kinds.json</c> that have a format, one per format (see <see cref="TypesFrom"/>). Callers that can read the
/// configuration pass the loaded kinds; <see cref="Types"/> and <see cref="Subfolders"/> are the same lists for the kinds that
/// ship, which the loader also falls back to when the file is missing.
/// </summary>
public static class ImportFolderRules
{
    public const string LocationId = "incoming";

    // The order the Import folder has always listed its types and created its folders in. The kinds list is ordered by area, which
    // would show the user a different order here, so these keep what people already see; a format not named here follows, in
    // the order of the kinds list.
    private static readonly string[] TypeOrder = ["Books", "Audiobooks", "Movies", "TV", "Music", "Comics", "Videos"];
    private static readonly string[] SubfolderOrder = ["Movies", "TV", "Music", "Books", "Audiobooks", "Comics", "Videos"];

    /// <summary>The media types an import folder needs a destination for, for the kinds that ship with the Library (see <see cref="TypesFrom"/>).</summary>
    public static readonly IReadOnlyList<(string Label, string[] MediaTypes)> Types = TypesFrom(LibraryKindsConfiguration.BuiltIn());

    /// <summary>The folders created inside the import folder when it is set, one per media type, for the kinds that ship. Programs such as Sonarr and Radarr are pointed at them.</summary>
    public static readonly IReadOnlyList<string> Subfolders = SubfoldersFrom(LibraryKindsConfiguration.BuiltIn());

    /// <summary>
    /// The import folder's media types for a list of kinds: each available kind that has a format, the first one of each format.
    /// The label is the kind's label; the tokens are the format (TV also answers to its older spelling TVShows). The order is the
    /// one the Import folder has always shown (see <see cref="TypeOrder"/>).
    /// </summary>
    public static IReadOnlyList<(string Label, string[] MediaTypes)> TypesFrom(IEnumerable<LibraryKindDefinition> kinds)
    {
        ArgumentNullException.ThrowIfNull(kinds);
        return
        [
            .. kinds
                .Where(kind => kind.Available && kind.Format is not null)
                .DistinctBy(kind => kind.Format, StringComparer.OrdinalIgnoreCase)
                .OrderBy(kind => RankOf(TypeOrder, kind.Format!))
                .Select(kind => (kind.Label, string.Equals(kind.Format, nameof(Enums.MediaType.TV), StringComparison.OrdinalIgnoreCase)
                    ? new[] { kind.Format!, "TVShows" }
                    : new[] { kind.Format! })),
        ];
    }

    /// <summary>The per-type folder names for a list of kinds, one per media type of <see cref="TypesFrom"/>, in the order they have always been created.</summary>
    public static IReadOnlyList<string> SubfoldersFrom(IEnumerable<LibraryKindDefinition> kinds)
    {
        ArgumentNullException.ThrowIfNull(kinds);
        return
        [
            .. TypesFrom(kinds)
                .OrderBy(type => RankOf(SubfolderOrder, type.MediaTypes[0]))
                .Select(type => SubfolderFor(type.Label)),
        ];
    }

    private static int RankOf(string[] order, string format)
    {
        var index = Array.FindIndex(order, candidate => string.Equals(candidate, format, StringComparison.OrdinalIgnoreCase));
        return index < 0 ? order.Length : index;
    }

    /// <summary>Name of the folder, inside the import folder, that Tuvima uses for its own working files. It is never scanned for media.</summary>
    public const string WorkingFolderName = ".data";

    /// <summary>True when <paramref name="path"/> is the import folder or anywhere inside it.</summary>
    public static bool IsInside(string importPath, string path)
    {
        if (string.IsNullOrWhiteSpace(importPath) || string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(importPath));
        var candidate = Path.GetFullPath(path);
        return string.Equals(candidate, root, comparison)
            || candidate.StartsWith(root + Path.DirectorySeparatorChar, comparison);
    }

    /// <summary>The folder name used for a media type label, for example <c>TV Shows</c> becomes <c>TV</c>.</summary>
    public static string SubfolderFor(string label) =>
        string.Equals(label, "TV Shows", StringComparison.OrdinalIgnoreCase) ? "TV" : label;

    /// <summary>The media type label for a per-type folder name, or <see langword="null"/> when it is not one of the types (<paramref name="types"/>, or the ones that ship).</summary>
    public static string? LabelForSubfolder(string? folderName, IReadOnlyList<(string Label, string[] MediaTypes)>? types = null)
    {
        if (string.IsNullOrWhiteSpace(folderName))
        {
            return null;
        }

        foreach (var type in types ?? Types)
        {
            if (string.Equals(SubfolderFor(type.Label), folderName.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return type.Label;
            }
        }

        return null;
    }

    /// <summary>The type entry that covers a media type name such as <c>Movies</c>, <c>TV</c> or <c>Comic</c>, or <see langword="null"/>.</summary>
    public static (string Label, string[] MediaTypes)? TypeForMediaType(string? mediaTypeName, IReadOnlyList<(string Label, string[] MediaTypes)>? types = null)
    {
        if (string.IsNullOrWhiteSpace(mediaTypeName))
        {
            return null;
        }

        var parsed = Services.MediaTypeParser.Parse(mediaTypeName);
        foreach (var type in types ?? Types)
        {
            if (type.MediaTypes.Any(token =>
                    string.Equals(token, mediaTypeName.Trim(), StringComparison.OrdinalIgnoreCase)
                    || (parsed != Enums.MediaType.Unknown && Services.MediaTypeParser.Parse(token) == parsed)))
            {
                return type;
            }
        }

        return null;
    }

    public static ServerStorageLocationConfig? Find(LibrariesConfiguration configuration) => configuration.StorageLocations
        .FirstOrDefault(location => string.Equals(location.Id, LocationId, StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(location.Path));

    public static bool HasDestination(LibraryFolderConfig library) => library.PrimaryDestination is not null;

    /// <summary>The catalogued library that holds a type and has a managed destination, or <see langword="null"/>.</summary>
    public static LibraryFolderConfig? FindLibrary(LibrariesConfiguration configuration, string[] mediaTypes) =>
        configuration.Libraries.FirstOrDefault(library =>
            string.Equals(library.Kind, LibraryKinds.Catalogued, StringComparison.OrdinalIgnoreCase)
            && library.MediaTypes.Any(token => mediaTypes.Contains(token, StringComparer.OrdinalIgnoreCase))
            && HasDestination(library));

    public static ImportFolderStatus Evaluate(LibrariesConfiguration configuration, IReadOnlyList<(string Label, string[] MediaTypes)>? types = null)
    {
        var location = Find(configuration);
        if (location is null)
        {
            return new ImportFolderStatus(null, []);
        }

        var destinations = (types ?? Types)
            .Select(type =>
            {
                var library = FindLibrary(configuration, type.MediaTypes);
                var destination = library?.PrimaryDestination;
                return new ImportFolderDestination(type.Label, library?.Id, destination?.Id, destination?.Path);
            })
            .ToList();
        var missing = destinations.Where(destination => !destination.HasDestination).Select(destination => destination.MediaType).ToList();
        return new ImportFolderStatus(location.Path, missing, destinations);
    }
}
