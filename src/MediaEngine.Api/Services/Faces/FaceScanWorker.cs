using MediaEngine.Domain.Contracts;

namespace MediaEngine.Api.Services.Faces;

/// <summary>
/// Looks through every person's own photos for faces in the background (spec view-people-search WP7). It steps aside while anyone is
/// watching or listening, handles a few photos at a time, and picks up where it stopped after a restart because looked-at photos are recorded.
/// </summary>
public sealed class FaceScanWorker(
    FaceScanService scanner,
    IViewPersonalSpaceRepository spaces,
    IPlaybackActivityProbe playback,
    ILogger<FaceScanWorker> logger) : BackgroundService
{
    private static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan BusyDelay = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var handled = await RunOnceAsync(stoppingToken).ConfigureAwait(false);
                if (handled == 0)
                {
                    await Task.Delay(IdleDelay, stoppingToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "The face scan failed; it will try again.");
                await Task.Delay(IdleDelay, stoppingToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Looks at one small batch for every profile, unless someone is watching or listening. Returns how many photos were handled.</summary>
    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        if (await playback.IsAnyonePlayingAsync(ct).ConfigureAwait(false))
        {
            await Task.Delay(BusyDelay, ct).ConfigureAwait(false);
            return 1;
        }

        var handled = 0;
        foreach (var space in await spaces.GetAllAsync(ct).ConfigureAwait(false))
        {
            handled += await scanner.ScanBatchAsync(space.OwnerProfileId, 20, ct).ConfigureAwait(false);
        }

        return handled;
    }
}
