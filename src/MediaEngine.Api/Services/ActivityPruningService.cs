using MediaEngine.Api.Services.ScheduledTasks;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Api.Services;

/// <summary>
/// Background service that runs once daily and prunes system activity entries: first those older than
/// <c>config/maintenance.json → activity_retention_days</c> (default 90), then the oldest of the rest while
/// more than <c>activity_max_events</c> (default 500,000) remain. Personal activity is never pruned here.
///
/// Pruning is silent: no notification is raised. It logs an <c>ActivityPruned</c> event after a prune that removed
/// something, and each run is recorded through <see cref="ScheduledTaskRunner"/>.
/// </summary>
public sealed class ActivityPruningService : BackgroundService
{
    private readonly ISystemActivityRepository _activityRepo;
    private readonly ActivityRetentionPruner _pruner;
    private readonly IConfigurationLoader _configLoader;
    private readonly IEntityTimelineRepository _timelineRepo;
    private readonly ScheduledTaskRunner _runner;
    private readonly ILogger<ActivityPruningService> _logger;

    /// <summary>Cron expression for the prune schedule. Default: 3 AM daily.</summary>
    private const string DefaultSchedule = "0 3 * * *";

    public ActivityPruningService(
        ISystemActivityRepository activityRepo,
        ActivityRetentionPruner pruner,
        IConfigurationLoader configLoader,
        IEntityTimelineRepository timelineRepo,
        ScheduledTaskRunner runner,
        ILogger<ActivityPruningService> logger)
    {
        ArgumentNullException.ThrowIfNull(activityRepo);
        ArgumentNullException.ThrowIfNull(pruner);
        ArgumentNullException.ThrowIfNull(configLoader);
        ArgumentNullException.ThrowIfNull(timelineRepo);
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(logger);

        _activityRepo = activityRepo;
        _pruner = pruner;
        _configLoader = configLoader;
        _timelineRepo = timelineRepo;
        _runner = runner;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("ActivityPruningService started — default schedule: {Schedule}", DefaultSchedule);

        // Initial delay to let the rest of the app start.
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _runner.RunAsync(ScheduledTaskKeys.ActivityPruning, RunPruneCycleAsync, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Activity prune cycle failed; will retry next cycle");
            }

            var maintenanceConfig = _configLoader.LoadMaintenance();
            var schedule = maintenanceConfig.Schedules.GetValueOrDefault("activity_pruning", DefaultSchedule);
            if (string.IsNullOrWhiteSpace(schedule))
            {
                schedule = DefaultSchedule;
            }

            var delay = CronScheduler.UntilNext(schedule, TimeSpan.FromHours(24));
            _logger.LogInformation("Next activity prune at {NextRun}", DateTimeOffset.Now.Add(delay));
            await Task.Delay(delay, stoppingToken);
        }
    }

    /// <summary>
    /// One cycle: the activity log prune (days first, then the event cap), then the timeline cull. A failure in either
    /// is logged and recorded as the run's failure message, and the other part still runs.
    /// </summary>
    private async Task<TaskRunResult> RunPruneCycleAsync(CancellationToken stoppingToken)
    {
        var removed = 0;
        string? failure = null;

        // ── Activity log prune ────────────────────────────────────────
        try
        {
            var maintenance = _configLoader.LoadMaintenance();
            var result = await _pruner.PruneAsync(maintenance, stoppingToken);
            removed += result.Total;

            if (result.Total > 0)
            {
                _logger.LogInformation(
                    "Activity prune: removed {ForAge} entries older than {Days} days and {ForCap} over the {Max} event limit",
                    result.DeletedForAge, result.RetentionDays, result.DeletedForCap, result.MaxEvents);

                // Log the prune itself so the Logs page shows when it happened.
                await _activityRepo.LogAsync(new SystemActivityEntry
                {
                    ActionType = SystemActionType.ActivityPruned,
                    Detail = $"Pruned {result.Total} entries ({result.DeletedForAge} older than {result.RetentionDays} days, {result.DeletedForCap} over the {result.MaxEvents:N0} event limit)",
                    ChangesJson = $"{{\"deleted\":{result.Total},\"deleted_for_age\":{result.DeletedForAge},\"deleted_for_cap\":{result.DeletedForCap},\"retention_days\":{result.RetentionDays},\"max_events\":{result.MaxEvents}}}",
                }, stoppingToken);
            }
            else
            {
                _logger.LogDebug("Activity prune: nothing to remove (retention={Days}d, cap={Max})",
                    result.RetentionDays, result.MaxEvents);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Activity prune failed; will retry next cycle");
            failure = ex.Message;
        }

        // ── Timeline cull ─────────────────────────────────────────────
        try
        {
            var hydration = _configLoader.LoadHydration();
            var retentionDays = hydration.TimelineRetentionDays > 0
                                    ? hydration.TimelineRetentionDays
                                    : 365;
            var culled = await _timelineRepo.CullOldEventsAsync(
                TimeSpan.FromDays(retentionDays), stoppingToken);
            removed += culled;

            if (culled > 0)
            {
                _logger.LogInformation(
                    "Timeline cull: removed {Count} events older than {Days} days",
                    culled, retentionDays);
            }
            else
            {
                _logger.LogDebug("Timeline cull: nothing to remove (retention={Days}d)", retentionDays);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Timeline cull failed; will retry next cycle");
            failure ??= ex.Message;
        }

        return failure is null
            ? TaskRunResult.Succeeded(removed, $"Removed {removed} old entries.")
            : TaskRunResult.Failed(failure, removed);
    }
}
