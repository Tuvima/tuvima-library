using MediaEngine.Contracts.Settings;
using MediaEngine.Domain.Contracts;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Ingestion.Services;

/// <summary>
/// Finishes the inspection of files that were waiting for FFmpeg, as soon as a tools check finds it. Runs after every
/// check, including the one at start-up, so waiting files continue without a re-scan. The pass runs in the background,
/// one file at a time, so a check (and the person who pressed Check again) never waits for thousands of files.
/// </summary>
public sealed class PendingMediaInspectionResumer(
    IMediaInspectionHoldRepository holds,
    IMediaAssetRepository assets,
    PlaybackInspectionWriter writer,
    MediaInspectionReviewRecorder reviews,
    ILogger<PendingMediaInspectionResumer> logger) : IMediaToolStatusListener
{
    private const int PageSize = 50;

    private int _running;
    private Task _pass = Task.CompletedTask;

    /// <summary>
    /// Starts a pass when FFmpeg is available and none is running. Returns at once; <paramref name="ct"/> should live as long
    /// as the Engine, because it also stops the background pass.
    /// </summary>
    public Task OnMediaToolsCheckedAsync(MediaToolAvailability availability, CancellationToken ct)
    {
        if (!availability.FfmpegAvailable)
        {
            return Task.CompletedTask;
        }

        // One pass at a time: a second check while a pass runs has nothing to add.
        if (Interlocked.Exchange(ref _running, 1) == 1)
        {
            return Task.CompletedTask;
        }

        _pass = Task.Run(() => RunPassAsync(ct), CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <summary>Completes when the current pass (if any) has finished. For shutdown and tests.</summary>
    public Task WhenIdleAsync() => Volatile.Read(ref _pass);

    private async Task RunPassAsync(CancellationToken ct)
    {
        try
        {
            await ResumeAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The Engine is stopping; the rest stay held and resume on the next start-up check.
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Resuming the waiting media inspections stopped early; the rest stay waiting.");
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
    }

    private async Task ResumeAsync(CancellationToken ct)
    {
        int resumed = 0, failed = 0, kept = 0;
        var keepGoing = true;
        while (keepGoing && !ct.IsCancellationRequested)
        {
            var page = await holds.ListAssetIdsByToolAsync(ToolRequirementIds.Ffmpeg, PageSize, kept, ct).ConfigureAwait(false);
            if (page.Count == 0)
            {
                break;
            }

            foreach (var assetId in page)
            {
                if (ct.IsCancellationRequested)
                {
                    keepGoing = false;
                    break;
                }

                try
                {
                    var asset = await assets.FindByIdAsync(assetId, ct).ConfigureAwait(false);
                    if (asset is null)
                    {
                        await holds.DeleteAsync(assetId, ct).ConfigureAwait(false);
                        continue;
                    }

                    switch (await writer.InspectAsync(asset, ct).ConfigureAwait(false))
                    {
                        case PlaybackInspectionOutcome.Failed:
                            await reviews.RecordFailureAsync(assetId, null, ct).ConfigureAwait(false);
                            failed++;
                            break;
                        case PlaybackInspectionOutcome.WaitingForTool:
                            // The tool went away mid-pass; leave this and the rest held for the next check.
                            kept++;
                            keepGoing = false;
                            break;
                        case PlaybackInspectionOutcome.NotApplicable:
                            await holds.DeleteAsync(assetId, ct).ConfigureAwait(false);
                            break;
                        default:
                            resumed++;
                            break;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    kept++;
                    logger.LogWarning(ex, "Could not resume the media inspection of asset {AssetId}; it stays waiting.", assetId);
                }

                if (!keepGoing)
                {
                    break;
                }
            }
        }

        if (resumed + failed + kept > 0)
        {
            logger.LogInformation(
                "Resumed {Resumed} waiting media inspections ({Failed} could not be read, {Kept} still waiting).",
                resumed, failed, kept);
        }
    }
}
