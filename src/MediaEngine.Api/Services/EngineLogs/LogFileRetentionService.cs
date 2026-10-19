using MediaEngine.Api.Services.ScheduledTasks;
using MediaEngine.Domain.Contracts;

namespace MediaEngine.Api.Services.EngineLogs;

/// <summary>
/// Background service that applies the Engine log file retention rule once a day: files older than
/// <c>log_retention_days</c> are deleted, then the oldest files until the total is under <c>log_max_total_mb</c>.
/// Pruning is silent by design: nothing is raised for it, and the Scheduled tasks list shows when it last ran.
/// </summary>
public sealed class LogFileRetentionService : BackgroundService
{
    internal const string TaskId = "log-file-retention";
    private const string DefaultSchedule = "30 3 * * *";
    private static readonly TimeSpan StartDelay = TimeSpan.FromSeconds(45);

    private readonly IConfigurationLoader _configLoader;
    private readonly EngineLogLocation _location;
    private readonly IScheduledTaskRegistry _registry;
    private readonly TimeProvider _clock;
    private readonly ILogger<LogFileRetentionService> _logger;

    public LogFileRetentionService(
        IConfigurationLoader configLoader,
        EngineLogLocation location,
        IScheduledTaskRegistry registry,
        TimeProvider clock,
        ILogger<LogFileRetentionService> logger)
    {
        ArgumentNullException.ThrowIfNull(configLoader);
        ArgumentNullException.ThrowIfNull(location);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);

        _configLoader = configLoader;
        _location = location;
        _registry = registry;
        _clock = clock;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var task = _registry.Register(Describe(DefaultSchedule));

        // Let the rest of the app start before the first pass.
        task.NextRunIn(StartDelay);
        await Task.Delay(StartDelay, stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var maintenance = _configLoader.LoadMaintenance();
                var result = LogFilePruner.Prune(
                    Path.GetFullPath(_location.Directory),
                    maintenance.LogRetentionDays,
                    maintenance.LogMaxTotalMegabytes * 1024L * 1024L,
                    _clock.GetUtcNow(),
                    _logger);

                if (result.DeletedForAge + result.DeletedForSize > 0)
                {
                    _logger.LogInformation(
                        "Log file prune: removed {ForAge} files older than {Days} days and {ForSize} files over the {Megabytes} MB total",
                        result.DeletedForAge, maintenance.LogRetentionDays, result.DeletedForSize, maintenance.LogMaxTotalMegabytes);
                }

                task.Done();
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Log file prune failed; will retry next cycle");
                task.Failed(ex);
            }

            var schedule = _configLoader.LoadMaintenance().Schedules.GetValueOrDefault("log_file_retention", DefaultSchedule);
            if (string.IsNullOrWhiteSpace(schedule))
            {
                schedule = DefaultSchedule;
            }

            _registry.Register(Describe(schedule));
            var delay = CronScheduler.UntilNext(schedule, TimeSpan.FromHours(24));
            task.NextRunIn(delay);
            await Task.Delay(delay, stoppingToken);
        }
    }

    private static ScheduledTaskDefinition Describe(string schedule) => new(
        TaskId,
        "Clean up old log files",
        "Deletes the Engine's technical log files once they are older than the limit set on the Logs page, or the folder grows past its size limit.",
        ScheduleDescriber.FromCron(schedule));
}
