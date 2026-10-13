using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;

namespace MediaEngine.Api.Services.LocalAssets.Ocr;

/// <summary>Waiting times for the text recognition worker. Shortened in tests.</summary>
public sealed record DocumentOcrWorkerTiming(
    TimeSpan PlaybackPollInterval,
    TimeSpan IdleDelay,
    TimeSpan UnavailableDelay,
    TimeSpan LowEffortPause)
{
    public static DocumentOcrWorkerTiming Default { get; } = new(
        TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(3));
}

/// <summary>
/// Reads scanned documents and photos from the durable media-operation queue, one file at a time,
/// and steps aside while anyone is watching or listening. Queued work survives an Engine restart.
/// </summary>
public sealed class DocumentOcrOperationWorker(
    IMediaOperationRepository operations,
    DocumentOcrService reader,
    DocumentOcrQueue queue,
    IPlaybackActivityProbe playback,
    IConfigurationLoader configuration,
    ILogger<DocumentOcrOperationWorker> logger,
    DocumentOcrWorkerTiming? timing = null) : BackgroundService
{
    private static readonly string[] OperationTypes = [MediaOperationType.ViewDocumentOcr];

    private readonly DocumentOcrWorkerTiming _timing = timing ?? DocumentOcrWorkerTiming.Default;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var delay = await RunOnceAsync(stoppingToken).ConfigureAwait(false) switch
                {
                    WorkerStep.Worked => TimeSpan.Zero,
                    WorkerStep.Unavailable => _timing.UnavailableDelay,
                    _ => _timing.IdleDelay,
                };
                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, stoppingToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Text recognition worker failed; queued work will be retried.");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);
            }
        }
    }

    public enum WorkerStep
    {
        /// <summary>One file was handled.</summary>
        Worked,

        /// <summary>The queue was empty.</summary>
        Idle,

        /// <summary>Recognition is off or not installed, so queued files are left alone.</summary>
        Unavailable,
    }

    /// <summary>Waits while media is playing, then handles at most one queued file.</summary>
    public async Task<WorkerStep> RunOnceAsync(CancellationToken ct)
    {
        if (!await queue.IsReadyAsync(ct).ConfigureAwait(false))
        {
            return WorkerStep.Unavailable;
        }

        await WaitWhilePlayingAsync(ct).ConfigureAwait(false);
        var settings = configuration.LoadOcr();
        var leased = await operations.LeaseNextAsync(
            Environment.MachineName + ":document-ocr",
            OperationTypes,
            batchSize: 1,
            leaseDuration: TimeSpan.FromSeconds(Math.Max(60, settings.PerFileTimeoutSeconds) + 120),
            ct).ConfigureAwait(false);
        if (leased.Count == 0)
        {
            return WorkerStep.Idle;
        }

        await ProcessAsync(leased[0], ct).ConfigureAwait(false);
        if (OcrEffort.Normalize(settings.Effort) == OcrEffort.Low && _timing.LowEffortPause > TimeSpan.Zero)
        {
            await Task.Delay(_timing.LowEffortPause, ct).ConfigureAwait(false);
        }

        return WorkerStep.Worked;
    }

    private async Task WaitWhilePlayingAsync(CancellationToken ct)
    {
        while (await playback.IsAnyonePlayingAsync(ct).ConfigureAwait(false))
        {
            await Task.Delay(_timing.PlaybackPollInterval, ct).ConfigureAwait(false);
        }
    }

    private async Task ProcessAsync(MediaOperation operation, CancellationToken ct)
    {
        if (operation.EntityId is not { } itemId)
        {
            await operations.MarkFailedTerminalAsync(operation.Id, "The queued operation has no document target.", ct).ConfigureAwait(false);
            return;
        }

        try
        {
            await operations.UpdateStageAsync(operation.Id, MediaOperationStage.Analyzing, 5, ct).ConfigureAwait(false);
            var outcome = await reader.ReadItemAsync(
                itemId,
                (percent, token) => operations.HeartbeatAsync(operation.Id, Math.Clamp(percent, 5, 95), token),
                ct).ConfigureAwait(false);
            switch (outcome.Status)
            {
                case DocumentOcrStatus.Read:
                    await operations.MarkSucceededAsync(operation.Id, outcome.Summary, ct).ConfigureAwait(false);
                    break;
                case DocumentOcrStatus.NoText:
                    await operations.MarkNoResultAsync(operation.Id, "no_text", outcome.Summary, ct).ConfigureAwait(false);
                    break;
                case DocumentOcrStatus.Missing:
                    await operations.MarkMissingConfirmedAsync(operation.Id, outcome.Summary, ct).ConfigureAwait(false);
                    break;
                case DocumentOcrStatus.Unavailable:
                    await operations.MarkFailedRetryableAsync(operation.Id, outcome.Summary, DateTimeOffset.UtcNow.AddMinutes(5), ct)
                        .ConfigureAwait(false);
                    break;
                default:
                    await operations.MarkFailedTerminalAsync(operation.Id, outcome.Summary, ct).ConfigureAwait(false);
                    break;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // One bad file must not stop the rest of the queue; the job is retried later.
            logger.LogWarning(ex, "Text recognition {OperationId} failed for item {ItemId}.", operation.Id, itemId);
            await operations.MarkFailedRetryableAsync(operation.Id, ex.GetType().Name, DateTimeOffset.UtcNow.AddMinutes(10), ct)
                .ConfigureAwait(false);
        }
    }
}
