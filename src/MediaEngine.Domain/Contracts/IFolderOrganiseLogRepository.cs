namespace MediaEngine.Domain.Contracts;

/// <summary>What happened to one file (or one sidecar) during the organising job.</summary>
public static class FolderOrganiseOutcomes
{
    /// <summary>The intent, written before the file moves. A restart finishes or retires it.</summary>
    public const string Moving = "moving";

    /// <summary>Renamed or moved to the standard name.</summary>
    public const string Moved = "moved";

    /// <summary>Moved, but the standard name was taken by a different file, so a numbered name was used.</summary>
    public const string MovedWithUniqueName = "moved_unique_name";

    /// <summary>The file already had the standard name.</summary>
    public const string AlreadyCorrect = "already_correct";

    /// <summary>An exact copy was already at the standard name, so this copy was removed under Keep one copy.</summary>
    public const string DuplicateRemoved = "duplicate_removed";

    /// <summary>A sidecar (subtitles, artwork, notes) that moved with its media file.</summary>
    public const string SidecarMoved = "sidecar_moved";

    /// <summary>A file the job left where it was because it is not a media file or sidecar it knows how to move.</summary>
    public const string NotMoved = "not_moved";

    /// <summary>The file is not in the library yet, so it was sent to be identified and is named when that finishes.</summary>
    public const string QueuedForIdentification = "queued_for_identification";

    /// <summary>The file needs a person to confirm its identity first, so it was left where it is.</summary>
    public const string NeedsReview = "needs_review";

    /// <summary>The file could not be moved (locked, permission, disk full) after retrying.</summary>
    public const string Failed = "failed";

    /// <summary>The file was not there any more, or is not ready to be named yet; normal scans tidy it later.</summary>
    public const string Skipped = "skipped";

    public static bool IsFinal(string outcome) => !string.Equals(outcome, Moving, StringComparison.Ordinal);
}

/// <summary>One line of the organising job's log.</summary>
public sealed record FolderOrganiseLogEntry(
    Guid OperationId,
    Guid SourceId,
    string OldPath,
    string NewPath,
    string Outcome,
    DateTimeOffset At,
    string? Detail = null);

/// <summary>
/// The per-file ledger of the organising job (<c>folder_organise_log</c>). It is the job's checkpoint (a restart resumes
/// after the last final entry), its audit trail (every move, old path to new path) and the source of the exported log.
/// </summary>
public interface IFolderOrganiseLogRepository
{
    /// <summary>Writes or replaces the entry for <c>(operation, old path)</c>.</summary>
    Task UpsertAsync(FolderOrganiseLogEntry entry, CancellationToken ct = default);

    Task<FolderOrganiseLogEntry?> GetAsync(Guid operationId, string oldPath, CancellationToken ct = default);

    /// <summary>Removes one entry (used to retire an intent whose move never happened).</summary>
    Task DeleteAsync(Guid operationId, string oldPath, CancellationToken ct = default);

    /// <summary>Entries whose move was started but not finished (outcome <c>moving</c>).</summary>
    Task<IReadOnlyList<FolderOrganiseLogEntry>> ListUnfinishedAsync(Guid operationId, CancellationToken ct = default);

    /// <summary>
    /// Every path (old and new) that already has a final outcome, so a resumed job does not touch a file again whether it is
    /// still at its old path or already at its new one.
    /// </summary>
    Task<HashSet<string>> GetFinishedPathsAsync(Guid operationId, CancellationToken ct = default);

    /// <summary>A page of the log in the order the moves happened.</summary>
    Task<IReadOnlyList<FolderOrganiseLogEntry>> ListAsync(Guid operationId, int offset, int limit, CancellationToken ct = default);

    Task<int> CountAsync(Guid operationId, CancellationToken ct = default);

    /// <summary>How many entries carry each outcome, for the completion summary.</summary>
    Task<IReadOnlyDictionary<string, int>> CountByOutcomeAsync(Guid operationId, CancellationToken ct = default);
}
