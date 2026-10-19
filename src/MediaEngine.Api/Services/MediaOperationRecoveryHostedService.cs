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
