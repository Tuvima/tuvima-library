namespace MediaEngine.Api.Services.Plugins;

/// <summary>Once at startup, cleans what plugins that are no longer installed left behind (Plugin architecture spec C23).</summary>
public sealed class PluginOrphanSweepHostedService(PluginRemovalService removal, ILogger<PluginOrphanSweepHostedService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await removal.SweepOrphansAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Cleaning up after removed plugins did not finish; it will be tried again next start");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
