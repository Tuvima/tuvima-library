namespace MediaEngine.Api.Services.Playback;

/// <summary>
/// Keeps the bookmark-frame cache honest in the background (Unified player v2 D21a): removes frames whose bookmark is gone every few
/// hours, and gradually queues a frame for older video bookmarks that have none. Low-key work; the frames themselves are made by
/// <see cref="BookmarkFrameOperationWorker"/>.
/// </summary>
public sealed class BookmarkFrameMaintenanceService(BookmarkFrameService frames, ILogger<BookmarkFrameMaintenanceService> logger) : BackgroundService
{
    private const int QueuedPerPass = 10;
    private static readonly TimeSpan PassInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan SweepInterval = TimeSpan.FromHours(6);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Give start-up (ingestion recovery, scans) a head start before this low-priority work begins.
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        var lastSweep = DateTimeOffset.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (DateTimeOffset.UtcNow - lastSweep > SweepInterval)
                {
                    lastSweep = DateTimeOffset.UtcNow;
                    await frames.SweepOrphansAsync(stoppingToken).ConfigureAwait(false);
                }

                await frames.BackfillAsync(QueuedPerPass, stoppingToken).ConfigureAwait(false);
                await Task.Delay(PassInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "The bookmark-frame maintenance pass failed; it will try again.");
                await Task.Delay(PassInterval, stoppingToken).ConfigureAwait(false);
            }
        }
    }
}
