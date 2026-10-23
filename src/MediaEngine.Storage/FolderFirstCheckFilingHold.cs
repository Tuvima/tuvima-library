using MediaEngine.Domain.Contracts;

namespace MediaEngine.Storage;

/// <summary>
/// Says that filing into a newly added Managed folder waits while its first check is running or paused (Library setup spec D61). A folder
/// with no check, or whose check has ended, is never held, so the filing hook of the batched write-back records waiting rows for it.
/// </summary>
public sealed class FolderFirstCheckFilingHold(IFolderFirstCheckRepository checks) : IFilingHold
{
    /// <inheritdoc/>
    public bool IsHeld(string libraryId, string sourceId, string path) => checks.IsHeld(sourceId);
}
