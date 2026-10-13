namespace MediaEngine.Domain.Configuration;

/// <summary>
/// Tuvima's own folders: the library folder that holds its managed data (and the View personal space), and the
/// import folder. They are created or chosen by Tuvima itself and are never offered in a folder picker.
/// </summary>
public static class TuvimaOwnFolders
{
    /// <summary>Storage location id of Tuvima's own library folder, created automatically on first run.</summary>
    public const string LibraryLocationId = "media";

    /// <summary>
    /// True when the storage location is one of Tuvima's own folders: its library folder, the import folder, or
    /// whichever location the View personal space lives in.
    /// </summary>
    public static bool IsOwnLocation(LibrariesConfiguration configuration, string? locationId)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (string.IsNullOrWhiteSpace(locationId))
        {
            return false;
        }

        return string.Equals(locationId, LibraryLocationId, StringComparison.OrdinalIgnoreCase)
            || string.Equals(locationId, ImportFolderRules.LocationId, StringComparison.OrdinalIgnoreCase)
            || string.Equals(locationId, configuration.ViewStorage?.StorageLocationId, StringComparison.OrdinalIgnoreCase);
    }
}
