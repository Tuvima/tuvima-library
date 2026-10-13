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
/// It is the storage location with id <see cref="LocationId"/>. When it is set, each of the six media types needs a
/// destination: the folder Tuvima manages and files that type into. An as-is folder is never a destination.
/// </summary>
public static class ImportFolderRules
{
    public const string LocationId = "incoming";

    public static readonly IReadOnlyList<(string Label, string[] MediaTypes)> Types =
    [
        ("Books", ["Books"]),
        ("Audiobooks", ["Audiobooks"]),
        ("Movies", ["Movies"]),
        ("TV Shows", ["TV", "TVShows"]),
        ("Music", ["Music"]),
        ("Comics", ["Comics"]),
    ];

    /// <summary>The folders created inside the import folder when it is set, one per media type. Programs such as Sonarr and Radarr are pointed at them.</summary>
    public static readonly IReadOnlyList<string> Subfolders = ["Movies", "TV", "Music", "Books", "Audiobooks", "Comics"];

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

    /// <summary>The media type label for a per-type folder name, or <see langword="null"/> when it is not one of the six.</summary>
    public static string? LabelForSubfolder(string? folderName)
    {
        if (string.IsNullOrWhiteSpace(folderName))
        {
            return null;
        }

        foreach (var type in Types)
        {
            if (string.Equals(SubfolderFor(type.Label), folderName.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return type.Label;
            }
        }

        return null;
    }

    /// <summary>The type entry that covers a media type name such as <c>Movies</c>, <c>TV</c> or <c>Comic</c>, or <see langword="null"/>.</summary>
    public static (string Label, string[] MediaTypes)? TypeForMediaType(string? mediaTypeName)
    {
        if (string.IsNullOrWhiteSpace(mediaTypeName))
        {
            return null;
        }

        var parsed = Services.MediaTypeParser.Parse(mediaTypeName);
        foreach (var type in Types)
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

    public static ImportFolderStatus Evaluate(LibrariesConfiguration configuration)
    {
        var location = Find(configuration);
        if (location is null)
        {
            return new ImportFolderStatus(null, []);
        }

        var destinations = Types
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
