using MediaEngine.Domain.Contracts;

namespace MediaEngine.Api.Services;

public sealed class MediaOperationRecoveryHostedService : BackgroundService
{
    private readonly IMediaOperationRepository _operations;
    private readonly ILogger<MediaOperationRecoveryHostedService> _logger;

    public MediaOperationRecoveryHostedService(
        IMediaOperationRepository operations,
        ILogger<MediaOperationRecoveryHostedService> logger)
    {
        _operations = operations;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            var reclaimed = await _operations.ReclaimStuckAsync(TimeSpan.FromMinutes(2), stoppingToken);
            if (reclaimed > 0)
            {
                _logger.LogInformation("Recovered {Count} interrupted media operations after restart.", reclaimed);
            }

            // A file that vanished before its intake started (for example a short-lived temporary file) is never found by
            // the start-up scan, so nothing would ever finish its job; close it so the live numbers can reach idle.
            var closed = 0;
            foreach (var (id, sourcePath) in await _operations.ListWaitingIngestionFilesAsync(stoppingToken))
            {
                if (!File.Exists(sourcePath))
                {
                    await _operations.MarkMissingConfirmedAsync(
                        id, "The file was no longer there when the Engine started.", stoppingToken);
                    closed++;
                }
            }

            if (closed > 0)
            {
                _logger.LogInformation("Closed {Count} waiting file intake jobs whose files are gone.", closed);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Media operation recovery failed during startup.");
        }
    }
}
