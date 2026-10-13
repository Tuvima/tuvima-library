namespace MediaEngine.Ingestion.Contracts;

/// <summary>
/// Files what is waiting in the Import folder. A file that could not be filed when it arrived, because its type had no
/// managed folder yet, the folder was offline, or a review was still open, is picked up here on a later pass.
/// </summary>
public interface IImportFolderFiler
{
    /// <summary>
    /// Tries again to file every registered file still in the Import folder whose identity work has settled. Returns how
    /// many files left the Import folder. Files that still can't be filed are left exactly where they are.
    /// </summary>
    Task<int> FilePendingAsync(CancellationToken ct = default);

    /// <summary>
    /// Finishes filings that started and never completed (the program stopped between moving a file and recording where it
    /// went): the asset is pointed at the file's new location instead of being treated as missing. Run before the startup
    /// check for missing files. Returns how many were finished.
    /// </summary>
    Task<int> RecoverInterruptedFilingsAsync(CancellationToken ct = default);

    /// <summary>
    /// Under Keep both, files an import copy of a file that is already held, with a unique name, into the same managed
    /// folder as the copy that is held. Returns the path it was filed to, or <see langword="null"/> and leaves the copy
    /// untouched when the held copy is itself still in the Import folder or its destination isn't ready. The copy is not a
    /// second library item: a file is indexed once, whatever the duplicate rule.
    /// </summary>
    Task<string?> FileDuplicateCopyAsync(string copyPath, Guid heldAssetId, string contentHash, CancellationToken ct = default);
}
