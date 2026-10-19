using MediaEngine.Api.Services.ScheduledTasks;

namespace MediaEngine.Api.Services;

/// <summary>Promotes due calendar entries into the ordinary enrichment queues.</summary>
public sealed class EnrichmentRefreshScheduleWorker : BackgroundService
{
    private readonly EnrichmentRefreshScheduleService _schedule;
    private readonly IScheduledTaskRegistry _registry;
    private readonly ILogger<EnrichmentRefreshScheduleWorker> _logger;

    public EnrichmentRefreshScheduleWorker(
        EnrichmentRefreshScheduleService schedule,
        IScheduledTaskRegistry registry,
        ILogger<EnrichmentRefreshScheduleWorker> logger)
    {
        _schedule = schedule;
        _registry = registry;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromHours(1);
        var task = _registry.Register(new ScheduledTaskDefinition(
            "enrichment-refresh",
            "Refresh details on a schedule",
            "Queues the metadata refreshes you scheduled for your items once they are due.",
            ScheduleDescriber.Every(interval)));
        task.NextRunIn(interval);
        using var timer = new PeriodicTimer(interval);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                var queued = await _schedule.QueueDueAsync(50, stoppingToken).ConfigureAwait(false);
                if (queued > 0)
                {
                    _logger.LogInformation("Queued {Count} scheduled enrichment refreshes", queued);
                }

                task.Done();
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Scheduled enrichment refresh sweep failed");
                task.Failed(ex);
            }

            task.NextRunIn(interval);
        }
    }
}
