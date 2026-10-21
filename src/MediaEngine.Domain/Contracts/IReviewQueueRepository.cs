using MediaEngine.Domain.Entities;

namespace MediaEngine.Domain.Contracts;

/// <summary>
/// Persistence contract for <see cref="ReviewQueueEntry"/> records.
///
/// The <c>review_queue</c> table stores metadata hydration items that require
/// user intervention — disambiguation, low-confidence confirmation, or manual
/// match fixing.
///
/// Implementations live in <c>MediaEngine.Storage</c>.
/// </summary>
public interface IReviewQueueRepository
{
    /// <summary>
    /// Inserts a review queue entry.
    /// Returns the assigned <see cref="ReviewQueueEntry.Id"/>, or the existing
    /// pending row id when the same entity/trigger is already waiting for review.
    /// </summary>
    /// <param name="entry">The entry to insert. <see cref="ReviewQueueEntry.Id"/>
    /// should be pre-assigned by the caller.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<Guid> InsertAsync(ReviewQueueEntry entry, CancellationToken ct = default);

    /// <summary>
    /// Returns all pending review items, ordered by creation date (newest first).
    /// </summary>
    /// <param name="limit">Maximum entries to return (default 50).</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<ReviewQueueEntry>> GetPendingAsync(
        int limit = 50,
        CancellationToken ct = default);

    /// <summary>
    /// Returns a single review item by its ID, or <c>null</c> if not found.
    /// </summary>
    Task<ReviewQueueEntry?> GetByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Returns all review items (any status) for a given entity, ordered newest first.
    /// </summary>
    Task<IReadOnlyList<ReviewQueueEntry>> GetByEntityAsync(
        Guid entityId,
        CancellationToken ct = default);

    /// <summary>
    /// Returns all <c>Pending</c> review items for a given entity, ordered by creation date.
    /// Used by <see cref="MediaEngine.Ingestion.AutoOrganizeService"/> to check for blocking
    /// reviews before promoting a file from staging.
    /// </summary>
    Task<IReadOnlyList<ReviewQueueEntry>> GetPendingByEntityAsync(
        Guid entityId,
        CancellationToken ct = default);

    /// <summary>
    /// Updates the status of a review item (e.g. Pending → Resolved or Dismissed).
    /// Sets <see cref="ReviewQueueEntry.ResolvedAt"/> to <c>DateTimeOffset.UtcNow</c>
    /// and <see cref="ReviewQueueEntry.ResolvedBy"/> to the given profile identifier.
    /// </summary>
    /// <param name="id">The review item ID.</param>
    /// <param name="status">The new status. Use <see cref="Constants.ReviewStatus"/> constants.</param>
    /// <param name="resolvedBy">The profile that resolved/dismissed this item (nullable).</param>
    /// <param name="ct">Cancellation token.</param>
    Task UpdateStatusAsync(
        Guid id,
        string status,
        string? resolvedBy = null,
        CancellationToken ct = default);

    /// <summary>
    /// Marks pending review rows for an entity as ready after automation reaches
    /// a terminal point where human input is required.
    /// </summary>
    Task<int> MarkPendingReadyByEntityAsync(Guid entityId, CancellationToken ct = default);

    /// <summary>
    /// Marks hidden pending review rows for an entity as ready and returns the
    /// rows that should now be announced to users.
    /// </summary>
    Task<IReadOnlyList<ReviewQueueEntry>> PromotePendingReadyByEntityAsync(
        Guid entityId,
        CancellationToken ct = default);

    /// <summary>
    /// Returns the number of pending review items.
    /// Used for sidebar badge count and global notification badge.
    /// </summary>
    Task<int> GetPendingCountAsync(CancellationToken ct = default);

    /// <summary>
    /// Bulk-dismisses all <c>Pending</c> review items that reference
    /// <paramref name="entityId"/>. Called by reconciliation when a MediaAsset
    /// is deleted so stale review items are cleaned up automatically.
    /// Returns the number of items dismissed.
    /// </summary>
    Task<int> DismissAllByEntityAsync(Guid entityId, CancellationToken ct = default);

    /// <summary>
    /// Resolves all <c>Pending</c> review items for a given entity by setting
    /// their status to <c>Resolved</c>. Called by <c>AutoOrganizeService</c> when
    /// a file is successfully promoted from staging to the library — the review
    /// items are moot once the file passes the organization gate.
    /// Returns the number of items resolved.
    /// </summary>
    Task<int> ResolveAllByEntityAsync(Guid entityId, string resolvedBy = "system:auto-organize", CancellationToken ct = default);

    /// <summary>
    /// Resolves only pending review rows whose trigger is superseded by a later
    /// successful automated identity outcome.
    /// </summary>
    Task<int> ResolvePendingByEntityAndTriggersAsync(
        Guid entityId,
        IReadOnlyCollection<string> triggers,
        string resolvedBy,
        CancellationToken ct = default);

    /// <summary>
    /// <see cref="ResolvePendingByEntityAndTriggersAsync"/> for many entities at once. The default asks one entity at a time;
    /// the data store implementation does it in a few statements.
    /// </summary>
    async Task<int> ResolvePendingByEntitiesAndTriggersAsync(
        IReadOnlyCollection<Guid> entityIds,
        IReadOnlyCollection<string> triggers,
        string resolvedBy,
        CancellationToken ct = default)
    {
        var resolved = 0;
        foreach (var entityId in entityIds)
        {
            resolved += await ResolvePendingByEntityAndTriggersAsync(entityId, triggers, resolvedBy, ct).ConfigureAwait(false);
        }

        return resolved;
    }

    /// <summary>Deletes review queue entries whose entity_id no longer exists in media_assets.</summary>
    Task<int> PurgeOrphanedAsync(CancellationToken ct = default);
}
