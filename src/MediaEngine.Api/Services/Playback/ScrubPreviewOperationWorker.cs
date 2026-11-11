using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Jobs;

namespace MediaEngine.Api.Services.Playback;

/// <summary>
/// Makes scrub previews from the durable media operation queue (Unified player v2 D9a). One worker, one file at a time, the lowest
/// priority: it waits while a transcode is being served (and stops the file it is on when one starts), while the switch is off, and
/// while FFmpeg is missing. It never touches ingestion, and a failure here is recorded on the operation, never thrown.
/// </summary>
public sealed class ScrubPreviewOperationWorker(
    IMediaOperationRepository operations,
    ScrubPreviewGenerator generator,
    ITranscodeActivitySignal transcodes,
    IConfigurationLoader configuration,
    ILogger<ScrubPreviewOperationWorker> logger) : BackgroundService
{
    private static readonly string[] OperationTypes = [MediaOperationType.PlaybackScrubPreviews];
    private static readonly TimeSpan BusyWait = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan IdleWait = TimeSpan.FromSeconds(15);
    private const int GiveUpAfterAttempts = 3;

    private bool _loggedFfmpegMissing;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var wait = await RunOnceAsync(stoppingToken).ConfigureAwait(false);
                await Task.Delay(wait, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "The scrub-preview worker failed; queued work will be retried.");
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Does at most one file and returns how long to wait before looking again (zero when there may be more).</summary>
    public async Task<TimeSpan> RunOnceAsync(CancellationToken ct)
    {
        if (!configuration.LoadTranscoding().ScrubPreviews.Enabled)
        {
            return BusyWait;
        }

        if (transcodes.IsBusy)
        {
            return BusyWait;
        }

        var leased = await operations.LeaseNextAsync(
            Environment.MachineName + ":scrub-previews",
            OperationTypes,
            batchSize: 1,
            leaseDuration: TimeSpan.FromHours(2),
            ct).ConfigureAwait(false);
        if (leased.Count == 0)
        {
            return IdleWait;
        }

        return await ProcessAsync(leased[0], ct).ConfigureAwait(false);
    }

    private async Task<TimeSpan> ProcessAsync(MediaOperation operation, CancellationToken ct)
    {
        if (operation.EntityId is not { } assetId)
        {
            await operations.MarkFailedTerminalAsync(operation.Id, "The queued operation has no media asset target.", ct).ConfigureAwait(false);
            return TimeSpan.Zero;
        }

        // Stop the file in hand as soon as a transcode starts being served or the switch is turned off.
        using var yield = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var watcher = new PeriodicTimer(TimeSpan.FromSeconds(5));
        var watching = WatchAsync(watcher, operation.Id, yield, ct);
        try
        {
            await operations.UpdateStageAsync(operation.Id, MediaOperationStage.WritingArtifact, 10, ct).ConfigureAwait(false);
            var result = await generator.GenerateAsync(assetId, yield.Token).ConfigureAwait(false);
            return await RecordAsync(operation, result, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (yield.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            // Stepped aside for a transcode (or the switch): put the file back in the queue for later.
            await operations.RequeueAsync(operation.Id, ct).ConfigureAwait(false);
            logger.LogInformation("Scrub previews for asset {AssetId} stepped aside and will resume later.", assetId);
            return BusyWait;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await operations.InterruptIfRunningAsync(operation.Id, "The Engine is stopping.", CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Scrub previews for asset {AssetId} failed.", assetId);
            return await FailAsync(operation, ex.Message, ct).ConfigureAwait(false);
        }
        finally
        {
            watcher.Dispose();
            await yield.CancelAsync().ConfigureAwait(false);
            try
            {
                await watching.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // The watcher ends with the file; nothing to report.
            }
        }
    }

    private async Task WatchAsync(PeriodicTimer timer, Guid operationId, CancellationTokenSource yield, CancellationToken ct)
    {
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                await operations.HeartbeatAsync(operationId, null, ct).ConfigureAwait(false);
                if (transcodes.IsBusy || !configuration.LoadTranscoding().ScrubPreviews.Enabled)
                {
                    await yield.CancelAsync().ConfigureAwait(false);
                    return;
                }
            }
        }
        catch (ObjectDisposedException)
        {
            // The timer was disposed because the file finished.
        }
    }

    private async Task<TimeSpan> RecordAsync(MediaOperation operation, ScrubPreviewResult result, CancellationToken ct)
    {
        switch (result.Outcome)
        {
            case ScrubPreviewOutcome.Generated:
            case ScrubPreviewOutcome.AlreadyComplete:
                await operations.MarkSucceededAsync(operation.Id, result.Message, ct).ConfigureAwait(false);
                return TimeSpan.Zero;
            case ScrubPreviewOutcome.Disabled:
                await operations.RequeueAsync(operation.Id, ct).ConfigureAwait(false);
                return BusyWait;
            case ScrubPreviewOutcome.FfmpegUnavailable:
                // Recorded as "not available", retried in an hour, and quiet: one log line, then the worker backs off.
                if (!_loggedFfmpegMissing)
                {
                    _loggedFfmpegMissing = true;
                    logger.LogInformation("FFmpeg is not available, so scrub previews are waiting. Install or locate FFmpeg in Settings > Tools and they will be made.");
                }

                await operations.MarkFailedRetryableForOutcomeAsync(
                    operation.Id, result.Message, DateTimeOffset.UtcNow.AddHours(1), BackgroundJobOutcomeCategory.UnavailableCapability, ct).ConfigureAwait(false);
                return TimeSpan.FromMinutes(5);
            case ScrubPreviewOutcome.SourceMissing:
                await operations.MarkNoResultAsync(operation.Id, "source_missing", result.Message, ct).ConfigureAwait(false);
                return TimeSpan.Zero;
            case ScrubPreviewOutcome.NotVideo:
                await operations.MarkNoResultAsync(operation.Id, "not_video", result.Message, ct).ConfigureAwait(false);
                return TimeSpan.Zero;
            default:
                return await FailAsync(operation, result.Message, ct).ConfigureAwait(false);
        }
    }

    private async Task<TimeSpan> FailAsync(MediaOperation operation, string error, CancellationToken ct)
    {
        // The queue counts a lease and a retry as two attempts each, and a wait for FFmpeg as one too. The poison count only grows when
        // FFmpeg really ran and failed, so this gives up after GiveUpAfterAttempts real tries.
        if (operation.PoisonAttemptCount + 1 >= GiveUpAfterAttempts)
        {
            await operations.MarkFailedTerminalAsync(operation.Id, error, ct).ConfigureAwait(false);
        }
        else
        {
            await operations.MarkFailedRetryableAsync(operation.Id, error, DateTimeOffset.UtcNow.AddMinutes(30), ct).ConfigureAwait(false);
        }

        return TimeSpan.Zero;
    }
}
