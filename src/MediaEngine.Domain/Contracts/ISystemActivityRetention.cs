namespace MediaEngine.Domain.Contracts;

/// <summary>
/// Size-limit pruning for the system activity ledger. Age-based pruning lives on <see cref="ISystemActivityRepository"/>;
/// this separate contract is the second half of the Logs retention rule (days first, then the event cap).
/// </summary>
public interface ISystemActivityRetention
{
    /// <summary>
    /// Keeps the newest <paramref name="maxEvents"/> entries and deletes everything older.
    /// Returns the number of rows deleted (0 when the ledger is already within the cap).
    /// </summary>
    Task<int> PruneToMaxEventsAsync(long maxEvents, CancellationToken ct = default);

    /// <summary>
    /// Deletes entries of one action type older than <paramref name="retentionDays"/> days. Used for the record of what people did with
    /// plugins, which has its own retention and is left out of the general age and size pruning.
    /// </summary>
    Task<int> PruneActionTypeOlderThanAsync(string actionType, int retentionDays, CancellationToken ct = default);
}
