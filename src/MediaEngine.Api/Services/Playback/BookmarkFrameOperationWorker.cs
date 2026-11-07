using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Jobs;
using MediaEngine.Processors;
using MediaEngine.Storage.Playback;

namespace MediaEngine.Api.Services.Playback;

/// <summary>
/// Takes the still frame of video bookmarks from the durable media operation queue (Unified player v2 D21a). One bookmark at a time:
/// one frame is a single quick seek, so unlike the scrub previews it does not wait for transcodes, and a person who has just saved a
/// bookmark sees the picture within moments. A failure is recorded on the operation, never thrown; FFmpeg being missing is one log line
/// and the job waits.
/// </summary>
public sealed class BookmarkFrameOperationWorker(
    IMediaOperationRepository operations,
    AudiobookBookmarkRepository bookmarks,
    IMediaAssetRepository assets,
    VideoFrameExtractor extractor,
    BookmarkFrameCache cache,
    ILogger<BookmarkFrameOperationWorker> logger) : BackgroundService
{
    private static readonly string[] OperationTypes = [MediaOperationType.PlaybackBookmarkFrame];
    private static readonly TimeSpan IdleWait = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan MissingFfmpegWait = TimeSpan.FromMinutes(5);
    private const int GiveUpAfterAttempts = 3;

    private bool _loggedFfmpegMissing;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var wait = await RunOnceAsync(stoppingToken).ConfigureAwait(false);
                if (wait > TimeSpan.Zero)
                {
                    await Task.Delay(wait, stoppingToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "The bookmark-frame worker failed; queued work will be retried.");
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Does at most one bookmark and returns how long to wait before looking again (zero when there may be more).</summary>
    public async Task<TimeSpan> RunOnceAsync(CancellationToken ct)
    {
        var leased = await operations.LeaseNextAsync(
            Environment.MachineName + ":bookmark-frames",
            OperationTypes,
            batchSize: 1,
            leaseDuration: TimeSpan.FromMinutes(10),
            ct).ConfigureAwait(false);
        if (leased.Count == 0)
        {
            return IdleWait;
        }

        return await ProcessAsync(leased[0], ct).ConfigureAwait(false);
    }

    private async Task<TimeSpan> ProcessAsync(MediaOperation operation, CancellationToken ct)
    {
        if (operation.EntityId is not { } bookmarkId)
        {
            await operations.MarkFailedTerminalAsync(operation.Id, "The queued operation has no bookmark target.", ct).ConfigureAwait(false);
            return TimeSpan.Zero;
        }

        try
        {
            var bookmark = await bookmarks.GetByIdAsync(bookmarkId, ct).ConfigureAwait(false);
            if (bookmark is null)
            {
                await operations.MarkNoResultAsync(operation.Id, "bookmark_missing", "The bookmark was removed before its frame was made.", ct).ConfigureAwait(false);
                return TimeSpan.Zero;
            }

            var asset = await assets.FindByIdAsync(bookmark.AssetId, ct).ConfigureAwait(false);
            if (asset is null || !File.Exists(asset.FilePathRoot))
            {
                await operations.MarkNoResultAsync(operation.Id, "source_missing", "The video file is not there.", ct).ConfigureAwait(false);
                return TimeSpan.Zero;
            }

            if (!extractor.IsAvailable)
            {
                // One log line, then the job waits an hour and the worker backs off; installing FFmpeg is all it takes.
                if (!_loggedFfmpegMissing)
                {
                    _loggedFfmpegMissing = true;
                    logger.LogInformation("FFmpeg is not available, so bookmark frames are waiting. Install or locate FFmpeg in Settings > Tools and they will be made.");
                }

                await operations.MarkFailedRetryableForOutcomeAsync(
                    operation.Id, "FFmpeg is not available.", DateTimeOffset.UtcNow.AddHours(1), BackgroundJobOutcomeCategory.UnavailableCapability, ct).ConfigureAwait(false);
                return MissingFfmpegWait;
            }

            await operations.UpdateStageAsync(operation.Id, MediaOperationStage.WritingArtifact, 10, ct).ConfigureAwait(false);
            var jpeg = await extractor.ExtractFrameAsync(asset.FilePathRoot, bookmark.PositionSeconds, BookmarkFrameCache.FrameWidth, ct).ConfigureAwait(false);
            if (jpeg is null)
            {
                logger.LogWarning("A frame for bookmark {BookmarkId} could not be taken at {Seconds}s.", bookmarkId, bookmark.PositionSeconds);
                return await FailAsync(operation, "FFmpeg could not take a frame at that time.", ct).ConfigureAwait(false);
            }

            // The bookmark may have been deleted while FFmpeg was working; a picture for nothing would only wait for the next sweep.
            if (await bookmarks.GetByIdAsync(bookmarkId, ct).ConfigureAwait(false) is null)
            {
                await operations.MarkNoResultAsync(operation.Id, "bookmark_missing", "The bookmark was removed while its frame was being made.", ct).ConfigureAwait(false);
                return TimeSpan.Zero;
            }

            cache.Write(bookmark.AssetId, bookmarkId, jpeg);
            await operations.MarkSucceededAsync(operation.Id, $"{jpeg.Length} bytes", ct).ConfigureAwait(false);
            return TimeSpan.Zero;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await operations.InterruptIfRunningAsync(operation.Id, "The Engine is stopping.", CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "The frame for bookmark {BookmarkId} failed.", bookmarkId);
            return await FailAsync(operation, ex.Message, ct).ConfigureAwait(false);
        }
    }

    private async Task<TimeSpan> FailAsync(MediaOperation operation, string error, CancellationToken ct)
    {
        if (operation.AttemptCount >= GiveUpAfterAttempts)
        {
            await operations.MarkFailedTerminalAsync(operation.Id, error, ct).ConfigureAwait(false);
        }
        else
        {
            await operations.MarkFailedRetryableAsync(operation.Id, error, DateTimeOffset.UtcNow.AddMinutes(10), ct).ConfigureAwait(false);
        }

        return TimeSpan.Zero;
    }
}
