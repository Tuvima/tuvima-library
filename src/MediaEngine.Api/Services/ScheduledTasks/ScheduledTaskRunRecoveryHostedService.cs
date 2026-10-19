using MediaEngine.Storage.Contracts;

namespace MediaEngine.Api.Services.ScheduledTasks;

/// <summary>
/// At Engine startup, marks every task run still recorded as running as interrupted: the Engine stopped before it
/// finished. Registered ahead of the task services so no new run can start first.
/// </summary>
public sealed class ScheduledTaskRunRecoveryHostedService : BackgroundService
{
    private readonly IScheduledTaskRunRepository _runs;
    private readonly ILogger<ScheduledTaskRunRecoveryHostedService> _logger;

    public ScheduledTaskRunRecoveryHostedService(
        IScheduledTaskRunRepository runs,
        ILogger<ScheduledTaskRunRecoveryHostedService> logger)
    {
        _runs = runs;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            var interrupted = await _runs.MarkRunningAsInterruptedAsync(DateTimeOffset.UtcNow, stoppingToken);
            if (interrupted > 0)
            {
                _logger.LogInformation("Marked {Count} scheduled task run(s) interrupted by the restart.", interrupted);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down before the sweep finished; the next start sweeps again.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Scheduled task run recovery failed during startup.");
        }
    }
}
