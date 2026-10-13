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

    public static ServerStorageLocationConfig? Find(LibrariesConfiguration configuration) => configuration.StorageLocations
        .FirstOrDefault(location => string.Equals(location.Id, LocationId, StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(location.Path));

    public static bool HasDestination(LibraryFolderConfig library) => library.PrimaryDestination is not null;

    private static LibraryFolderConfig? FindLibrary(LibrariesConfiguration configuration, string[] mediaTypes) =>
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
