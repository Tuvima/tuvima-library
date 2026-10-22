using MediaEngine.Domain.Entities;

namespace MediaEngine.Domain.Contracts;

/// <summary>
/// Async-first persistence contract for the system activity ledger.
///
/// All writes are non-blocking — callers should fire-and-forget in background
/// contexts (e.g. after ingestion completes) to avoid slowing the critical path.
/// </summary>
public interface ISystemActivityRepository
{
    /// <summary>
    /// Appends a single activity entry to the ledger.
    /// Must never throw on duplicate or constraint violations.
    /// </summary>
    Task LogAsync(SystemActivityEntry entry, CancellationToken ct = default);

    /// <summary>
    /// Returns the most recent activity entries, ordered newest-first.
    /// </summary>
    /// <param name="limit">Maximum entries to return (default 50).</param>
    Task<IReadOnlyList<SystemActivityEntry>> GetRecentAsync(
        int limit = 50,
        CancellationToken ct = default);

    /// <summary>
    /// Deletes all entries older than <paramref name="retentionDays"/> days.
    /// Returns the number of rows deleted.
    /// </summary>
    Task<int> PruneOlderThanAsync(int retentionDays, CancellationToken ct = default);

    /// <summary>
    /// Returns the total number of entries in the ledger.
    /// </summary>
    Task<long> CountAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns recent activity entries filtered by one or more action types.
    /// Used by activity summaries to show events of specific categories
    /// (ingestion, universe, reports, curator actions).
    /// </summary>
    Task<IReadOnlyList<SystemActivityEntry>> GetRecentByTypesAsync(
        IReadOnlyList<string> actionTypes,
        int limit = 50,
        CancellationToken ct = default);

    /// <summary>
    /// Returns recent activity entries for one profile, ordered newest-first.
    /// Used by user-facing account surfaces so operational/admin activity is not mixed in.
    /// </summary>
    Task<IReadOnlyList<SystemActivityEntry>> GetRecentByProfileAsync(
        Guid profileId,
        int limit = 50,
        CancellationToken ct = default);

    /// <summary>
    /// Returns one page of entries whose action type is in <paramref name="actionTypes"/>, newest first, with the total
    /// number of matching entries. A non-blank <paramref name="search"/> keeps only entries whose name or detail (the words the
    /// Activity log shows) contains it (case-insensitive, matched literally). A non-blank <paramref name="mediaType"/> keeps only entries recorded
    /// for that media type (for example <c>TV</c>); entries with no media type are left out. Used by the Activity log.
    /// </summary>
    Task<(IReadOnlyList<SystemActivityEntry> Items, int Total)> QueryAsync(
        IReadOnlyList<string> actionTypes,
        string? search,
        int offset,
        int limit,
        string? mediaType = null,
        CancellationToken ct = default);
}
