namespace MediaEngine.Domain.Contracts;

/// <summary>Where the files of one library stand: how many are in each of the stages an empty page needs to tell apart.</summary>
public sealed record LibraryFileStageCount(
    string LibraryId,
    int Found,
    int InProgress,
    int WaitingForSource,
    int InReview,
    int Ready);

/// <summary>
/// Counts, per library, the files the Engine has registered and where each stands (honest-waiting-messages WP1). Counted from the
/// identity jobs, the review queue and the open intake operations the Engine already keeps; nothing new is stored. A Vault library's
/// files and orphaned files are never counted. Short-lived connection per call.
/// </summary>
public interface IAreaReadinessReadRepository
{
    /// <summary>One row per library that has at least one file.</summary>
    Task<IReadOnlyList<LibraryFileStageCount>> GetFileStageCountsAsync(CancellationToken ct = default);

    /// <summary>How many files are waiting in the intake queue (found by a scan, not yet registered or finished).</summary>
    Task<int> GetOpenIntakeCountAsync(CancellationToken ct = default);
}
