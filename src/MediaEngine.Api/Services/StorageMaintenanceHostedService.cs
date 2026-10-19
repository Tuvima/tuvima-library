using MediaEngine.Api.Services.ScheduledTasks;
using MediaEngine.Domain.Contracts;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Api.Services;

public sealed class StorageMaintenanceHostedService : BackgroundService
{
    private const string ScheduleKey = "storage_maintenance";
    private const string DefaultSchedule = "0 2 * * *";

    private readonly IStorageMaintenanceService _maintenanceService;
    private readonly IConfigurationLoader _configLoader;
    private readonly ScheduledTaskRunner _runner;
    private readonly ILogger<StorageMaintenanceHostedService> _logger;

    public StorageMaintenanceHostedService(
        IStorageMaintenanceService maintenanceService,
        IConfigurationLoader configLoader,
        ScheduledTaskRunner runner,
        ILogger<StorageMaintenanceHostedService> logger)
    {
        _maintenanceService = maintenanceService;
        _configLoader = configLoader;
        _runner = runner;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("StorageMaintenanceHostedService started - schedule: {Schedule}", DefaultSchedule);

        await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _runner.RunAsync(ScheduledTaskKeys.StorageMaintenance, async ct =>
                {
                    var maintenance = _configLoader.LoadMaintenance();
                    if (!maintenance.StorageMaintenance.Enabled)
                    {
                        return TaskRunResult.Skipped("Storage maintenance is off");
                    }

                    var result = await _maintenanceService.RunAsync(new StorageMaintenanceRequest(), ct)
                        .ConfigureAwait(false);
                    return TaskRunResult.Succeeded(result.TotalAffectedRows);
                }, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Storage maintenance pass failed; will retry next cycle");
            }

            var schedule = DefaultSchedule;
            try
            {
                var maintenance = _configLoader.LoadMaintenance();
                schedule = maintenance.Schedules.GetValueOrDefault(ScheduleKey, DefaultSchedule);
                if (string.IsNullOrWhiteSpace(schedule))
                {
                    schedule = DefaultSchedule;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not load storage maintenance schedule; using default");
            }

            var delay = CronScheduler.UntilNext(schedule, TimeSpan.FromHours(24));
            _logger.LogInformation("Next storage maintenance pass at {NextRun}", DateTimeOffset.Now.Add(delay));
            await Task.Delay(delay, stoppingToken);
        }
    }
}
