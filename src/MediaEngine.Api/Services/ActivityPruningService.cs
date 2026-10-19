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
/// something, and reports each run to the Scheduled tasks list.
/// </summary>
public sealed class ActivityPruningService : BackgroundService
{
    internal const string TaskId = "activity-pruning";

    private readonly ISystemActivityRepository _activityRepo;
    private readonly ActivityRetentionPruner _pruner;
    private readonly IScheduledTaskRegistry _registry;
    private readonly IConfigurationLoader _configLoader;
    private readonly IEntityTimelineRepository _timelineRepo;
    private readonly ILogger<ActivityPruningService> _logger;

    /// <summary>Cron expression for the prune schedule. Default: 3 AM daily.</summary>
    private const string DefaultSchedule = "0 3 * * *";

    public ActivityPruningService(
        ISystemActivityRepository activityRepo,
        ActivityRetentionPruner pruner,
        IScheduledTaskRegistry registry,
        IConfigurationLoader configLoader,
        IEntityTimelineRepository timelineRepo,
        ILogger<ActivityPruningService> logger)
    {
        ArgumentNullException.ThrowIfNull(activityRepo);
        ArgumentNullException.ThrowIfNull(pruner);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(configLoader);
        ArgumentNullException.ThrowIfNull(timelineRepo);
        ArgumentNullException.ThrowIfNull(logger);

        _activityRepo = activityRepo;
        _pruner = pruner;
        _registry = registry;
        _configLoader = configLoader;
        _timelineRepo = timelineRepo;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("ActivityPruningService started — default schedule: {Schedule}", DefaultSchedule);

        var task = _registry.Register(Describe(DefaultSchedule));

        // Initial delay to let the rest of the app start.
        task.NextRunIn(TimeSpan.FromSeconds(30));
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            // ── Activity log prune ────────────────────────────────────────
            var runFailed = false;
            try
            {
                var maintenance = _configLoader.LoadMaintenance();
                var result = await _pruner.PruneAsync(maintenance, stoppingToken);

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
                break;
            }
            catch (Exception ex)
            {
                runFailed = true;
                _logger.LogWarning(ex, "Activity prune failed; will retry next cycle");
                task.Failed(ex);
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
                break;
            }
            catch (Exception ex)
            {
                runFailed = true;
                _logger.LogWarning(ex, "Timeline cull failed; will retry next cycle");
                task.Failed(ex);
            }

            if (!runFailed)
            {
                task.Done();
            }

            var maintenanceConfig = _configLoader.LoadMaintenance();
            var schedule = maintenanceConfig.Schedules.GetValueOrDefault("activity_pruning", DefaultSchedule);
            if (string.IsNullOrWhiteSpace(schedule))
            {
                schedule = DefaultSchedule;
            }

            _registry.Register(Describe(schedule));
            var delay = CronScheduler.UntilNext(schedule, TimeSpan.FromHours(24));
            task.NextRunIn(delay);
            _logger.LogInformation("Next activity prune at {NextRun}", DateTimeOffset.Now.Add(delay));
            await Task.Delay(delay, stoppingToken);
        }
    }

    private static ScheduledTaskDefinition Describe(string schedule) => new(
        TaskId,
        "Tidy the Events log",
        "Removes events older than the limit set on the Logs page, and the oldest events once there are more than the size limit allows.",
        ScheduleDescriber.FromCron(schedule));
}
