using MediaEngine.Api.Services.ScheduledTasks;

namespace MediaEngine.Api.Services.Networking;

public sealed class RemoteConnectivityMonitor(
    IEnumerable<IRemoteConnectivityProvider> providers,
    NetworkRuntimeState runtime,
    IScheduledTaskRegistry registry,
    ILogger<RemoteConnectivityMonitor> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    private readonly IReadOnlyList<IRemoteConnectivityProvider> _providers = providers.ToList();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var task = registry.Register(new ScheduledTaskDefinition(
            "remote-connectivity",
            "Check remote access",
            "Checks whether the Library can be reached from outside your home network.",
            ScheduleDescriber.Every(Interval)));
        while (!stoppingToken.IsCancellationRequested)
        {
            Exception? failure = null;
            foreach (var provider in _providers)
            {
                try
                {
                    runtime.RecordRemoteProvider(await provider.GetStateAsync(stoppingToken));
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    failure = ex;
                    logger.LogWarning(ex, "Remote connectivity provider {Provider} status failed", provider.Key);
                }
            }

            if (failure is null)
            {
                task.Done();
            }
            else
            {
                task.Failed(failure);
            }

            task.NextRunIn(Interval);
            await Task.Delay(Interval, stoppingToken);
        }
    }
}
