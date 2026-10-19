namespace MediaEngine.Api.Services.EngineLogs;

/// <summary>Reads the Engine's technical log files for the Logs page.</summary>
public interface ITechnicalLogService
{
    Task<TechnicalLogPage> ReadAsync(TechnicalLogQuery query, CancellationToken ct = default);

    /// <summary>
    /// Every matching entry, oldest first, for the log export. The time, level, text and <paramref name="areas"/> filters
    /// (empty or null means every area) apply first; then only the newest <paramref name="maxLines"/> lines and about
    /// <paramref name="maxBytes"/> bytes are kept, and the result says which limit dropped older ones. The query's cursor,
    /// limit and single area are not used.
    /// </summary>
    Task<TechnicalLogRange> ReadRangeAsync(
        TechnicalLogQuery query, IReadOnlySet<string>? areas, int maxLines, long maxBytes, CancellationToken ct = default);
}
