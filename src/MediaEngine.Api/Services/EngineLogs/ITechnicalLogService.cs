namespace MediaEngine.Api.Services.EngineLogs;

/// <summary>Reads the Engine's technical log files for the Logs page.</summary>
public interface ITechnicalLogService
{
    Task<TechnicalLogPage> ReadAsync(TechnicalLogQuery query, CancellationToken ct = default);

    /// <summary>
    /// Every matching entry, oldest first, for the log export. Only the newest <paramref name="maxLines"/> are kept;
    /// the result says when older ones were left out. The query's cursor and limit are not used.
    /// </summary>
    Task<TechnicalLogRange> ReadRangeAsync(TechnicalLogQuery query, int maxLines, CancellationToken ct = default);
}
