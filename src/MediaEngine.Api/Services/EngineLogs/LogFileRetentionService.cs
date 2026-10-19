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
    private const string DefaultSchedule = "30 3 * * *";
    private static readonly TimeSpan StartDelay = TimeSpan.FromSeconds(45);

    private readonly IConfigurationLoader _configLoader;
    private readonly EngineLogLocation _location;
    private readonly ScheduledTaskRunner _runner;
    private readonly TimeProvider _clock;
    private readonly ILogger<LogFileRetentionService> _logger;

    public LogFileRetentionService(
        IConfigurationLoader configLoader,
        EngineLogLocation location,
        ScheduledTaskRunner runner,
        TimeProvider clock,
        ILogger<LogFileRetentionService> logger)
    {
        ArgumentNullException.ThrowIfNull(configLoader);
        ArgumentNullException.ThrowIfNull(location);
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);

        _configLoader = configLoader;
        _location = location;
        _runner = runner;
        _clock = clock;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Let the rest of the app start before the first pass.
        await Task.Delay(StartDelay, stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _runner.RunAsync(ScheduledTaskKeys.LogFileRetention, PruneAsync, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Log file prune failed; will retry next cycle");
            }

            var schedule = _configLoader.LoadMaintenance().Schedules.GetValueOrDefault(ScheduledTaskKeys.LogFileRetention, DefaultSchedule);
            if (string.IsNullOrWhiteSpace(schedule))
            {
                schedule = DefaultSchedule;
            }

            var delay = CronScheduler.UntilNext(schedule, TimeSpan.FromHours(24));
            await Task.Delay(delay, stoppingToken);
        }
    }

    private Task<TaskRunResult> PruneAsync(CancellationToken ct)
    {
        var maintenance = _configLoader.LoadMaintenance();
        var result = LogFilePruner.Prune(
            Path.GetFullPath(_location.Directory),
            maintenance.LogRetentionDays,
            maintenance.LogMaxTotalMegabytes * 1024L * 1024L,
            _clock.GetUtcNow(),
            _logger);

        var removed = result.DeletedForAge + result.DeletedForSize;
        if (removed > 0)
        {
            _logger.LogInformation(
                "Log file prune: removed {ForAge} files older than {Days} days and {ForSize} files over the {Megabytes} MB total",
                result.DeletedForAge, maintenance.LogRetentionDays, result.DeletedForSize, maintenance.LogMaxTotalMegabytes);
        }

        return Task.FromResult(TaskRunResult.Succeeded(removed, $"Removed {removed} old log files."));
    }
}
