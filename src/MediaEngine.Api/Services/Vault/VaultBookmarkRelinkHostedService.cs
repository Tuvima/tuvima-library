using MediaEngine.Storage;

namespace MediaEngine.Api.Services.Vault;

/// <summary>
/// After a restore, keeps trying to re-link the waiting Vault bookmarks while the rescan finds and identifies the videos. It costs one
/// count while nothing is waiting, so a library that was never restored pays nothing.
/// </summary>
public sealed class VaultBookmarkRelinkHostedService(
    VaultBookmarkRelinkRepository repository,
    VaultBookmarkRelinkService relink,
    ILogger<VaultBookmarkRelinkHostedService> logger) : BackgroundService
{
    internal static readonly TimeSpan Interval = TimeSpan.FromMinutes(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        try
        {
            do
            {
                try
                {
                    if (await repository.CountWaitingAsync(stoppingToken).ConfigureAwait(false) > 0)
                    {
                        await relink.RelinkAsync(stoppingToken).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // The kind of error only: a bookmark's video is exactly what must not be written down.
                    logger.LogWarning("Could not re-link saved Vault bookmarks ({Type}); it will be tried again.", ex.GetType().Name);
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            logger.LogDebug("Vault bookmark re-link stopped.");
        }
    }
}
