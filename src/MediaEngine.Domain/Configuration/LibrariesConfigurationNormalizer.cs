namespace MediaEngine.Domain.Configuration;

/// <summary>
/// Applies the rules that are derived rather than chosen, so every writer of <c>libraries.json</c> ends up with the
/// same result: a folder's access follows its management mode (and an As-is folder is never mid-conversion), and each
/// library has the right destination.
/// </summary>
public static class LibrariesConfigurationNormalizer
{
    public static LibrariesConfiguration Normalize(LibrariesConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        foreach (var library in configuration.Libraries)
        {
            foreach (var source in library.Sources)
            {
                source.AccessMode = source.IsManaged
                    ? LibrarySourceAccessModes.Writable
                    : LibrarySourceAccessModes.ReadOnly;

                // A folder Tuvima does not manage is not being converted: switching it back to As-is ends any conversion.
                if (!source.IsManaged)
                {
                    source.ConversionState = LibrarySourceConversionStates.None;
                    source.ConversionOperationId = null;
                }
            }

            library.PrimaryDestinationSourceId = PickDestination(library);
        }

        return configuration;
    }

    /// <summary>
    /// Keeps the current destination while it is still a managed folder of the library. Otherwise (none yet, removed,
    /// or switched to as-is) the earliest-added remaining managed folder takes over, and with none left the library has
    /// no destination. Sources are only ever appended, so list order is the order they were added; it means nothing else.
    /// </summary>
    public static string? PickDestination(LibraryFolderConfig library)
    {
        ArgumentNullException.ThrowIfNull(library);

        var current = library.PrimaryDestination;
        if (current is not null)
        {
            return current.Id;
        }

        return library.Sources.FirstOrDefault(source => source.IsManaged)?.Id;
    }
}
