namespace MediaEngine.Api.Services.LocalAssets;

/// <summary>
/// Keeps View honest about files deleted outside Library (Safer deleting in View): once an hour it looks at every folder again (this is
/// also how an unplugged drive is noticed and shown as "Folder offline"), and it removes the records of items that have been Missing for
/// longer than the retention setting. It changes records only, never a file.
/// </summary>
public sealed class ViewMissingFileHostedService(
    ViewMissingFileService service,
    ILogger<ViewMissingFileHostedService> logger) : BackgroundService
{
    private static readonly TimeSpan InitialDelay = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(InitialDelay, stoppingToken).ConfigureAwait(false);
            while (!stoppingToken.IsCancellationRequested)
            {
                await RunOnceAsync(stoppingToken).ConfigureAwait(false);
                await Task.Delay(Interval, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }

    /// <summary>One pass: mark what is gone, then purge what has been gone long enough. Public so a test can run it.</summary>
    public async Task RunOnceAsync(CancellationToken ct)
    {
        try
        {
            await service.SweepAsync(null, ct).ConfigureAwait(false);
            await service.PurgeAsync(ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "The check for files deleted outside Library could not finish; it tries again in an hour.");
        }
    }
}
