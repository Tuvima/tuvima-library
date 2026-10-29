using MediaEngine.Domain.Activity;
using MediaEngine.Domain.Authorization;

namespace MediaEngine.Domain.Contracts;

/// <summary>
/// A person's own record of what they watched, listened to, read and added to My list. Rows carry a snapshot of the
/// item, are never pruned automatically, and are removed only by <see cref="DeleteBeforeAsync"/> (a server
/// administrator's manual action).
/// </summary>
public interface IPersonalActivityRepository
{
    /// <summary>
    /// How long songs can be apart and still belong to one music session. A different activity by the same person
    /// in between also ends the session.
    /// </summary>
    public static readonly TimeSpan MusicSessionGap = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Starts the row for a playback session. Does nothing when the session already has a row, so repeated calls are
    /// safe. A listen row for music joins the person's open music session or starts a new one.
    /// </summary>
    /// <returns>True when a row was added.</returns>
    Task<bool> AddPlaybackAsync(
        Guid profileId,
        string kind,
        bool isMusic,
        Guid telemetrySessionId,
        DateTimeOffset startedAt,
        PersonalActivityDevice device,
        PersonalActivitySnapshot snapshot,
        PersonalActivityProgress progress,
        CancellationToken ct = default);

    /// <summary>Moves a playback session's row forward: the last time seen, time played, furthest point and finished flag only ever grow.</summary>
    Task UpdateProgressAsync(Guid telemetrySessionId, PersonalActivityProgress progress, CancellationToken ct = default);

    /// <summary>Records that a title was added to My list. Removing it later does not remove this row.</summary>
    Task AddMyListAsync(Guid profileId, DateTimeOffset at, PersonalActivitySnapshot snapshot, CancellationToken ct = default);

    Task<PersonalActivityItemPage> GetItemsAsync(
        PersonalActivityFilter filter,
        PersonalActivityGrouping grouping,
        string? cursor,
        int limit,
        CancellationToken ct = default);

    /// <summary>The matching entries one by one, newest first (songs of a music session are not collapsed).</summary>
    Task<(IReadOnlyList<PersonalActivityEntry> Entries, string? NextCursor)> GetEntriesAsync(
        PersonalActivityFilter filter,
        string? cursor,
        int limit,
        CancellationToken ct = default);

    Task<IReadOnlyList<PersonalActivityPeriodCount>> GetPeriodsAsync(
        PersonalActivityFilter filter,
        PersonalActivityGrouping grouping,
        CancellationToken ct = default);

    /// <summary>True when the small picture exists.</summary>
    Task<bool> HasArtworkAsync(string key, CancellationToken ct = default);

    /// <summary>Stores the small picture; does nothing when the key is already stored.</summary>
    Task SaveArtworkAsync(PersonalActivityArtwork artwork, CancellationToken ct = default);

    Task<PersonalActivityArtwork?> GetArtworkAsync(string key, CancellationToken ct = default);

    /// <summary>True when at least one row of the given people (null for everyone) uses this picture.</summary>
    Task<bool> IsArtworkUsedAsync(string key, IReadOnlySet<Guid>? profileIds, CancellationToken ct = default);

    /// <summary>How many entries started before the date, for the given people (null for everyone).</summary>
    Task<int> CountBeforeAsync(DateTimeOffset before, IReadOnlySet<Guid>? profileIds, CancellationToken ct = default);

    /// <summary>
    /// Deletes the entries that started before the date and the pictures only they used. The deletion and its audit
    /// entry (built from the number of entries removed) are written in one transaction: if the audit entry cannot be
    /// written nothing is deleted. Returns the entries removed.
    /// </summary>
    Task<int> DeleteBeforeAsync(
        DateTimeOffset before,
        IReadOnlySet<Guid>? profileIds,
        Func<int, AuthorizationAuditEvent> audit,
        CancellationToken ct = default);

    /// <summary>The lifetime totals of one person's activity.</summary>
    Task<PersonalActivitySummary> GetSummaryAsync(Guid profileId, CancellationToken ct = default);

    /// <summary>Titles finished and days active in the last <paramref name="days"/> days ending on the date of <paramref name="now"/> (UTC), with each active day.</summary>
    Task<PersonalActivityRolling> GetRollingAsync(Guid profileId, DateTimeOffset now, int days, CancellationToken ct = default);

    /// <summary>Titles by the month they were first finished and their genre, from <paramref name="since"/> on. A summarised read, never one row per entry.</summary>
    Task<IReadOnlyList<PersonalFinishedMonthGenre>> GetFinishedByMonthAsync(Guid profileId, DateTimeOffset since, CancellationToken ct = default);
}
