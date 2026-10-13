namespace MediaEngine.Ingestion.Models;

/// <summary>
/// Files that are sitting in the Import folder only because their type can't take them yet: it has no managed folder, or
/// its New media folder is offline, full or not writable. They are left exactly where they are. This is a plain description
/// of that situation, not a state of the Import folder.
/// </summary>
/// <param name="MediaType">Plain name of the media type, for example <c>TV Shows</c>.</param>
/// <param name="Folder">The per-type folder name inside the Import folder, for example <c>TV</c>.</param>
/// <param name="FileCount">How many files are waiting.</param>
/// <param name="Reason"><see cref="ImportDestinationState.NoManagedFolder"/> or <see cref="ImportDestinationState.Unavailable"/>.</param>
/// <param name="Message">One plain sentence, ready to show.</param>
public sealed record ImportFolderWaiting(
    string MediaType,
    string Folder,
    int FileCount,
    ImportDestinationState Reason,
    string Message);
