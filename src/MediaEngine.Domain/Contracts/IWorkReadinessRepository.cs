namespace MediaEngine.Domain.Contracts;

/// <summary>
/// One Work whose stored Ready mark just changed. <see cref="IsReady"/> is the new state.
/// <see cref="IsFirstReady"/> is true only when the Work became Ready for the first time ever (<c>first_ready_at</c>
/// was empty); a Work that becomes Ready again after leaving Ready is not announced as added again.
/// </summary>
public sealed record WorkReadinessChange(
    Guid WorkId, string MediaType, bool IsReady, DateTimeOffset At, bool IsFirstReady = false);

/// <summary>
/// The newest value seen in each table the readiness rule reads. Each value is the maximum of that table's
/// own timestamp column, so they are never compared with each other or with the wall clock (the tables
/// store different text formats). A null value means "start from nothing". <paramref name="Unmatched"/> is the newest time a file was
/// marked Unmatched or tried again: an Unmatched file is shown without waiting for the full sweep, even when it has no identity job.
/// </summary>
public sealed record WorkReadinessWatermark(string? Jobs, string? Reviews, string? Artwork, string? Unmatched = null)
{
    /// <summary>The starting point: nothing seen yet.</summary>
    public static WorkReadinessWatermark Empty { get; } = new(null, null, null, null);
}

/// <summary>
/// Persistence contract for the stored Ready mark (<c>works.ready_at</c>) on leaf Works.
///
/// A leaf Work is Ready when it is matched to a real title, placed in its show or album, has its artwork
/// present or settled, is not in review and is not rejected. The Engine's readiness service is the only
/// writer; every display surface reads the mark.
/// </summary>
public interface IWorkReadinessRepository
{
    /// <summary>
    /// Re-evaluates the given leaf Works: sets <c>ready_at</c> on those that now meet the full rule and clears it
    /// on Ready works that hit a negative outcome (review, no match, failure, rejection, lost file or placement).
    /// A Ready Work is not asked for its matching or artwork again, and keeps its original <c>ready_at</c>.
    /// Ids that are not leaf Works are ignored. Returns one record per Work whose state changed.
    /// <paramref name="bypassIdentityLibraryIds"/> lists the libraries whose metadata policy skips external
    /// identity (local-only or manual); their works have no identity job, so they count as matched without one.
    /// An empty list means every work needs a matched identity job.
    /// </summary>
    Task<IReadOnlyList<WorkReadinessChange>> RefreshAsync(
        IReadOnlyCollection<Guid> workIds, IReadOnlyCollection<string> bypassIdentityLibraryIds, CancellationToken ct = default);

    /// <summary>Re-evaluates every leaf Work.</summary>
    Task<IReadOnlyList<WorkReadinessChange>> RefreshAllAsync(
        IReadOnlyCollection<string> bypassIdentityLibraryIds, CancellationToken ct = default);

    /// <summary>
    /// Finds the leaf Works whose readiness inputs (identity jobs, review rows, artwork values) changed after
    /// <paramref name="since"/>, and returns the new watermark. A <paramref name="since"/> value that is null
    /// for a table returns nothing for that table and only establishes its watermark. Each look-up also re-reads a
    /// fixed stretch of time below the watermark, because a row is stamped before its write commits and a slow write
    /// can land below the watermark; re-checking a Work is harmless.
    /// </summary>
    Task<(IReadOnlyList<Guid> WorkIds, WorkReadinessWatermark Next)> FindTouchedWorkIdsAsync(
        WorkReadinessWatermark since, CancellationToken ct = default);

    /// <summary>
    /// Returns what the Ready event needs for the given Works: the work's own collection and title, its
    /// primary asset id and media type. Works that no longer exist are skipped.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, WorkReadinessDetails>> GetDetailsAsync(
        IReadOnlyCollection<Guid> workIds, CancellationToken ct = default);

    /// <summary>
    /// Finds the leaf Works that meet the position and review rules and have a matched identity job but are
    /// still waiting for artwork. Used to time how long artwork takes after a match.
    /// </summary>
    Task<IReadOnlyList<Guid>> FindMatchedAwaitingArtworkAsync(
        IReadOnlyCollection<Guid> workIds, IReadOnlyCollection<string> bypassIdentityLibraryIds, CancellationToken ct = default);
}

/// <summary>What the Ready event needs to describe a Work.</summary>
public sealed record WorkReadinessDetails(Guid WorkId, Guid? CollectionId, string MediaType, string Title, Guid? AssetId, string? GroupTitle = null);
