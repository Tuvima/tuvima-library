namespace MediaEngine.Domain.Contracts;

/// <summary>How far a Move to Vault job has got. A job in <see cref="Queued"/> or <see cref="Running"/> is carried on by the worker, also after a restart.</summary>
public static class VaultMoveStates
{
    public const string Queued = "queued";
    public const string Running = "running";
    public const string Done = "done";

    /// <summary>Everything that could be moved was, but something needs the administrator (a leftover original, or an item still being added).</summary>
    public const string Attention = "attention";
    public const string Failed = "failed";

    public static bool IsOpen(string state) => state is Queued or Running;
}

/// <summary>The steps one item goes through. Each step is saved before the next begins, so a restart resumes at the last saved one.</summary>
public static class VaultMoveItemStates
{
    /// <summary>Listed, nothing touched.</summary>
    public const string Planned = "planned";

    /// <summary>The copy beside its final place is complete and its hash matches the original. The original is untouched.</summary>
    public const string Copied = "copied";

    /// <summary>The original's records are gone from the library it left, and its file was removed or kept (see <see cref="VaultMoveItem.OriginalLeft"/>).</summary>
    public const string Retired = "retired";

    /// <summary>The copy has its final name, so the destination folder's scan will add it.</summary>
    public const string Published = "published";

    /// <summary>The new item exists in the destination and what the person had done with the old one was carried across.</summary>
    public const string Done = "done";
    public const string Skipped = "skipped";
    public const string Failed = "failed";

    public static bool IsFinished(string state) => state is Done or Skipped or Failed;
}

/// <summary>A Move to Vault or Move out job. The folders are paths on the server and are shown only to an unlocked administrator.</summary>
public sealed record VaultMoveJob(
    Guid Id,
    string Direction,
    string KindKey,
    string SourceLibraryId,
    string DestinationLibraryId,
    string SourceFolder,
    string DestinationFolder,
    bool RemoveReadOnlyOriginals,
    string State,
    string? Message,
    Guid? CreatedByProfileId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>One file (a title's media file) in a job, with its saved progress.</summary>
/// <param name="FilesJson">The sidecars (subtitles, notes) that travel with the media file, as a JSON list of source, staging and final names.</param>
/// <param name="SnapshotJson">What each profile had done with the old item (progress, history, reactions, bookmarks), saved before the old item is removed.</param>
/// <param name="OriginalLeft">True when the original could not be removed ("Moved, original still in place").</param>
public sealed record VaultMoveItem(
    Guid Id,
    Guid MoveId,
    int Position,
    Guid? WorkId,
    Guid? AssetId,
    Guid? NewAssetId,
    string SourcePath,
    string FinalName,
    long SizeBytes,
    string? ContentHash,
    string? FilesJson,
    string? SnapshotJson,
    bool OriginalLeft,
    string State,
    string? Reason,
    DateTimeOffset UpdatedAt);

/// <summary>A file in a folder that could be moved: its title (work) and how many media files that title has in all.</summary>
public sealed record VaultMoveCandidate(Guid? WorkId, Guid AssetId, string FilePath, string? ContentHash, int FilesInTitle);

/// <summary>The Vault's own list of moves. Everything here is for an unlocked administrator; nothing is shared with the ordinary library.</summary>
public interface IVaultMoveRepository
{
    /// <summary>The media files of a library that sit under a folder, each with its title. Reads only.</summary>
    Task<IReadOnlyList<VaultMoveCandidate>> FindCandidatesAsync(string libraryId, string folderPath, CancellationToken ct = default);

    /// <summary>The media asset a library already holds with this content hash (the file the move put there), or null.</summary>
    Task<(Guid AssetId, Guid? WorkId)?> FindByHashAsync(string libraryId, string contentHash, CancellationToken ct = default);

    /// <summary>True when a job that has not finished already moves files out of or into this folder.</summary>
    Task<bool> HasOpenJobForFolderAsync(string folderPath, CancellationToken ct = default);

    Task CreateAsync(VaultMoveJob job, IReadOnlyList<VaultMoveItem> items, CancellationToken ct = default);

    Task<VaultMoveJob?> GetAsync(Guid id, CancellationToken ct = default);

    Task<IReadOnlyList<VaultMoveJob>> ListRecentAsync(int limit, CancellationToken ct = default);

    /// <summary>Jobs still queued or running, oldest first.</summary>
    Task<IReadOnlyList<VaultMoveJob>> ListOpenAsync(CancellationToken ct = default);

    Task<IReadOnlyList<VaultMoveItem>> GetItemsAsync(Guid moveId, CancellationToken ct = default);

    Task UpdateItemAsync(VaultMoveItem item, CancellationToken ct = default);

    Task UpdateJobAsync(Guid id, string state, string? message, DateTimeOffset now, CancellationToken ct = default);
}

/// <summary>
/// Carries what each profile had done with a title across a move: where they got to, what they reacted to, their notes and bookmarks.
/// A move re-adds the title as a new item (Vault sections D36), so this is the only link between the two. History a profile kept while
/// the old item was in the Vault stays hidden from the ordinary library (it is not copied into it).
/// </summary>
public interface IMovedItemStateCarry
{
    /// <summary>Saves everything profiles have recorded against the old title and file, as JSON, before they are removed.</summary>
    Task<string> CaptureAsync(Guid? workId, Guid assetId, CancellationToken ct = default);

    /// <summary>
    /// Writes the saved state against the new title and file. A profile that no longer exists is skipped, and running it again adds nothing twice.
    /// <paramref name="includeHistory"/> is false when the title leaves the Vault, so history kept in the Vault never appears in the ordinary library.
    /// </summary>
    Task RestoreAsync(string snapshotJson, Guid? newWorkId, Guid newAssetId, bool includeHistory = true, CancellationToken ct = default);
}
