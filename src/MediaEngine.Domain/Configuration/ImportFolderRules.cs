namespace MediaEngine.Domain.Configuration;

/// <summary>Whether the optional import folder is set and whether every media type has somewhere to put imported media.</summary>
public sealed record ImportFolderStatus(string? Path, IReadOnlyList<string> MissingTypes)
{
    public bool Configured => !string.IsNullOrWhiteSpace(Path);

    /// <summary>Import runs only when a folder is set and every media type has a destination.</summary>
    public bool Ready => Configured && MissingTypes.Count == 0;
}

/// <summary>
/// The import folder is optional (other apps such as Sonarr and Radarr can drop finished downloads in it).
/// It is the storage location with id <see cref="LocationId"/>. When it is set, each of the six media types needs a
/// destination: a folder Tuvima manages, or an as-is folder the person marked to accept new media.
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

    public static bool HasDestination(LibraryFolderConfig library) => library.Sources.Any(source =>
        (source.ManagementMode == LibrarySourceManagementModes.ManagedByTuvima
            && source.Role == LibrarySourceRoles.PrimaryDestination)
        || (source.ManagementMode == LibrarySourceManagementModes.ExistingLibrary && source.AcceptsNewMedia));

    public static ImportFolderStatus Evaluate(LibrariesConfiguration configuration)
    {
        var location = Find(configuration);
        if (location is null)
        {
            return new ImportFolderStatus(null, []);
        }

        var missing = Types
            .Where(type => !configuration.Libraries.Any(library =>
                string.Equals(library.Kind, LibraryKinds.Catalogued, StringComparison.OrdinalIgnoreCase)
                && library.MediaTypes.Any(token => type.MediaTypes.Contains(token, StringComparer.OrdinalIgnoreCase))
                && HasDestination(library)))
            .Select(type => type.Label)
            .ToList();
        return new ImportFolderStatus(location.Path, missing);
    }
}
