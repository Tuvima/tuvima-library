using MediaEngine.Domain.Entities;

namespace MediaEngine.Domain.Contracts;

/// <summary>
/// Reads system activity entries for a time range, oldest first, for the log export. A separate contract so the
/// main activity ledger contract stays as small as the code that fakes it.
/// </summary>
public interface ISystemActivityRangeReader
{
    /// <summary>
    /// Returns up to <paramref name="limit"/> entries that occurred between <paramref name="from"/> and <paramref name="to"/>,
    /// oldest first. When <paramref name="matchId"/> is set, only entries of that ingestion run or about that entity.
    /// </summary>
    Task<IReadOnlyList<SystemActivityEntry>> GetRangeAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        Guid? matchId,
        int limit,
        CancellationToken ct = default);
}
