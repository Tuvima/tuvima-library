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
    private readonly IScheduledTaskRegistry _registry;
    private readonly ILogger<StorageMaintenanceHostedService> _logger;

    public StorageMaintenanceHostedService(
        IStorageMaintenanceService maintenanceService,
        IConfigurationLoader configLoader,
        IScheduledTaskRegistry registry,
        ILogger<StorageMaintenanceHostedService> logger)
    {
        _maintenanceService = maintenanceService;
        _configLoader = configLoader;
        _registry = registry;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("StorageMaintenanceHostedService started - schedule: {Schedule}", DefaultSchedule);

        var task = _registry.Register(Describe(DefaultSchedule));
        task.NextRunIn(TimeSpan.FromMinutes(1));
        await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var maintenance = _configLoader.LoadMaintenance();
                if (maintenance.StorageMaintenance.Enabled)
                {
                    await _maintenanceService.RunAsync(new StorageMaintenanceRequest(), stoppingToken)
                        .ConfigureAwait(false);
                }

                task.Done();
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Storage maintenance pass failed; will retry next cycle");
                task.Failed(ex);
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

            _registry.Register(Describe(schedule));
            var delay = CronScheduler.UntilNext(schedule, TimeSpan.FromHours(24));
            task.NextRunIn(delay);
            _logger.LogInformation("Next storage maintenance pass at {NextRun}", DateTimeOffset.Now.Add(delay));
            await Task.Delay(delay, stoppingToken);
        }
    }

    private static ScheduledTaskDefinition Describe(string schedule) => new(
        "storage-maintenance",
        "Tidy the data store and caches",
        "Compacts the data store and clears old search results and unused cached images.",
        ScheduleDescriber.FromCron(schedule));
}
