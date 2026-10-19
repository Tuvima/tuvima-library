using MediaEngine.Domain.Enums;

namespace MediaEngine.Storage.Contracts;

/// <summary>
/// One request that reached the Local AI engine room, as stored in <c>ai_usage_records</c>.
/// </summary>
/// <param name="CallerId">A stable key: a feature key such as <c>smart_labeling</c>, or the plugin manifest id.</param>
/// <param name="WaitMs">Milliseconds spent waiting in the queue.</param>
/// <param name="DurationMs">Milliseconds spent running once it had a turn.</param>
/// <param name="Outcome">How it ended; deferred requests are never stored.</param>
/// <param name="Message">Plain-English result or reason. Never the prompt or the text read.</param>
public sealed record AiUsageRecord(
    Guid Id,
    DateTimeOffset StartedAt,
    AiCallerKind CallerKind,
    string CallerId,
    AiAbility Ability,
    AiEngine Engine,
    int WaitMs,
    int DurationMs,
    AiRequestStatus Outcome,
    string? Message);

/// <summary>What one caller has used since a point in time. Refused and unavailable requests are not counted.</summary>
/// <param name="Requests">Requests that were let in (they ran, failed, timed out or were cancelled).</param>
/// <param name="BusyMs">Summed running time of those requests.</param>
public sealed record AiUsageTotals(int Requests, long BusyMs);

/// <summary>Usage per caller and ability over a period, for the Usage page.</summary>
public sealed record AiUsageSummaryRow(
    AiCallerKind CallerKind,
    string CallerId,
    AiAbility Ability,
    int Requests,
    int Succeeded,
    long BusyMs,
    DateTimeOffset LastUsedAt);

/// <summary>
/// Durable history of Local AI use. Plugin hourly allowances are counted from here, so they survive an Engine restart.
/// </summary>
public interface IAiUsageRecordRepository
{
    /// <summary>Days of history kept.</summary>
    public const int RetentionDays = 90;

    /// <summary>Adds one record.</summary>
    Task AddAsync(AiUsageRecord record, CancellationToken ct = default);

    /// <summary>
    /// How many requests a caller has made, and for how long they ran, since <paramref name="since"/>. Requests refused for
    /// being over budget, and requests that were unavailable, did not use any allowance and are not counted.
    /// </summary>
    Task<AiUsageTotals> GetTotalsSinceAsync(AiCallerKind kind, string callerId, DateTimeOffset since, CancellationToken ct = default);

    /// <summary>The newest records first, optionally for one caller, <paramref name="skip"/> rows in.</summary>
    Task<IReadOnlyList<AiUsageRecord>> ListRecentAsync(
        int limit,
        int skip = 0,
        AiCallerKind? kind = null,
        string? callerId = null,
        CancellationToken ct = default);

    /// <summary>Requests per caller and ability since <paramref name="since"/>, busiest first.</summary>
    Task<IReadOnlyList<AiUsageSummaryRow>> SummariseSinceAsync(DateTimeOffset since, CancellationToken ct = default);

    /// <summary>Deletes records older than <see cref="RetentionDays"/> before <paramref name="now"/>; returns how many.</summary>
    Task<int> PruneAsync(DateTimeOffset now, CancellationToken ct = default);
}
