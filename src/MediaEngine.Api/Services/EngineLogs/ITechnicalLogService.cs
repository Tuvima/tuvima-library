namespace MediaEngine.Api.Services.EngineLogs;

/// <summary>Reads the Engine's technical log files for the Logs page.</summary>
public interface ITechnicalLogService
{
    Task<TechnicalLogPage> ReadAsync(TechnicalLogQuery query, CancellationToken ct = default);
}
