using MediaEngine.Domain.Entities;

namespace MediaEngine.Domain.Contracts;

/// <summary>
/// Reads system activity entries for a time range, oldest first, for the log export. A separate contract so the
/// main activity ledger contract stays as small as the code that fakes it.
/// </summary>
public interface ISystemActivityRangeReader
{
    /// <summary>
    /// Returns entries that occurred between <paramref name="from"/> and <paramref name="to"/>, oldest first. The filters
    /// run before the limit: only entries of the ingestion run or entity <paramref name="matchId"/>, only the given
    /// <paramref name="actionTypes"/>, and only those whose detail contains <paramref name="detailContains"/> (each
    /// ignored when null). When more than <paramref name="limit"/> match, the NEWEST <paramref name="limit"/> are returned,
    /// still oldest first.
    /// </summary>
    Task<IReadOnlyList<SystemActivityEntry>> GetRangeAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        Guid? matchId,
        IReadOnlyCollection<string>? actionTypes,
        string? detailContains,
        int limit,
        CancellationToken ct = default);

    /// <summary>The distinct action types that occurred between <paramref name="from"/> and <paramref name="to"/>.</summary>
    Task<IReadOnlyList<string>> GetActionTypesAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default);
}
