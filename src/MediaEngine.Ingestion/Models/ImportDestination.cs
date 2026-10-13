namespace MediaEngine.Ingestion.Models;

/// <summary>Whether the folder an imported file would be filed into can take it right now.</summary>
public enum ImportDestinationState
{
    /// <summary>The type's New media folder is reachable and writable.</summary>
    Ready,

    /// <summary>The type has no managed folder, so imported files stay where they are until it has one.</summary>
    NoManagedFolder,

    /// <summary>The type's New media folder is offline, full or not writable. Files stay where they are; nothing goes elsewhere.</summary>
    Unavailable,
}

/// <summary>
/// Where one media type's imported files are filed: the single managed folder marked New media, or nothing.
/// There is never a fallback to another managed folder.
/// </summary>
public sealed record ImportDestination(
    string TypeLabel,
    ImportDestinationState State,
    LibraryFolderEntry? Library = null,
    LibrarySourceEntry? Source = null,
    string? Reason = null)
{
    public bool IsReady => State == ImportDestinationState.Ready && Library is not null && Source is not null;
}
