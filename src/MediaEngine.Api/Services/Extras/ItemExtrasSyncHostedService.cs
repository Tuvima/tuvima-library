using MediaEngine.Ingestion.Contracts;
using MediaEngine.Storage;

namespace MediaEngine.Api.Services.Extras;

/// <summary>
/// Keeps the Extras beside titles up to date (Extras spec WP1). It checks every title folder once after start-up and then once a day,
/// and in between re-checks the few folders the ingestion pipeline or the file watcher queued, so a guidebook added to a show's
/// folder appears within about half a minute. A failed pass is logged and tried again; it never stops the Engine.
/// </summary>
public sealed class ItemExtrasSyncHostedService(
    IItemExtrasScanner scanner,
    ILogger<ItemExtrasSyncHostedService> logger,
    OnboardingActivationGate? onboardingGate = null) : BackgroundService
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan QueueInterval = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan FullPassInterval = TimeSpan.FromHours(24);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            if (onboardingGate is not null && !onboardingGate.IsComplete)
            {
                await onboardingGate.WaitAsync(stoppingToken).ConfigureAwait(false);
            }

            await Task.Delay(StartupDelay, stoppingToken).ConfigureAwait(false);
            var nextFullPass = DateTimeOffset.UtcNow;
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    if (DateTimeOffset.UtcNow >= nextFullPass)
                    {
                        var folders = await scanner.SyncAllAsync(stoppingToken).ConfigureAwait(false);
                        logger.LogInformation("Extras: checked {Count} title folder(s)", folders);
                        nextFullPass = DateTimeOffset.UtcNow + FullPassInterval;
                    }
                    else
                    {
                        await scanner.SyncQueuedAsync(stoppingToken).ConfigureAwait(false);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Extras: a check failed; trying again shortly");
                }

                await Task.Delay(QueueInterval, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // The Engine is stopping.
        }
    }
}
