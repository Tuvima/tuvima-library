using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Storage;

namespace MediaEngine.Api.Services.Playback;

/// <summary>
/// Finds video files with no scrub previews and queues them (Unified player v2 D9a). Newest files come first, so a film that was
/// just identified is queued on the next pass; the rest of the library is worked through gradually, a few files per pass, and only
/// while the preview queue is short, so it never floods the queue.
/// </summary>
public sealed class ScrubPreviewBackfillService(
    ScrubPreviewCandidateRepository candidates,
    ScrubPreviewQueue queue,
    ScrubPreviewCache store,
    IMediaTypeExtensionCatalog extensions,
    IMediaOperationRepository operations,
    IMediaAssetRepository assets,
    IConfigurationLoader configuration,
    ILogger<ScrubPreviewBackfillService> logger) : BackgroundService
{
    private const int PageSize = 100;

    // Where the gradual pass over the older library stopped. The newest page is always looked at from the top.
    private int _deepOffset = PageSize;
    private DateTimeOffset _lastPrune = DateTimeOffset.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Give start-up (ingestion recovery, scans) a head start before this low-priority work begins.
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(45), stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            var minutes = 2;
            try
            {
                var settings = configuration.LoadTranscoding().ScrubPreviews;
                minutes = Math.Clamp(settings.BackfillIntervalMinutes, 1, 120);
                if (settings.Enabled)
                {
                    if (DateTimeOffset.UtcNow - _lastPrune > TimeSpan.FromHours(6))
                    {
                        _lastPrune = DateTimeOffset.UtcNow;
                        await PruneOrphansAsync(stoppingToken).ConfigureAwait(false);
                    }

                    await RunPassAsync(Math.Clamp(settings.BackfillPerPass, 1, 50), stoppingToken).ConfigureAwait(false);
                }

                await Task.Delay(TimeSpan.FromMinutes(minutes), stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "The scrub-preview back-fill pass failed; it will try again.");
                await Task.Delay(TimeSpan.FromMinutes(minutes), stoppingToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>One pass: the newest page, then (if the queue is short) the next page of the older library. Returns how many files were queued.</summary>
    public async Task<int> RunPassAsync(int perPass, CancellationToken ct)
    {
        var videoExtensions = extensions.GetAllMediaExtensions().Where(extensions.IsVideoExtension).ToList();
        var queued = await QueuePageAsync(videoExtensions, 0, perPass, ct).ConfigureAwait(false);
        if (queued >= perPass)
        {
            return queued;
        }

        // The gradual part only runs while few previews are waiting, so a big library is fed in slowly.
        var waiting = (await operations.GetQueueAsync(MediaOperationQueue.ScrubPreviews, 50, ct).ConfigureAwait(false))
            .Count(operation => operation.Status is MediaOperationStatus.Queued
                or MediaOperationStatus.RetryWaiting
                or MediaOperationStatus.Interrupted);
        if (waiting >= perPass * 2)
        {
            return queued;
        }

        var page = await candidates.ListVideoAssetsAsync(videoExtensions, _deepOffset, PageSize, ct).ConfigureAwait(false);
        if (page.Count == 0)
        {
            _deepOffset = PageSize; // The end of the library: start the next round from the top.
            return queued;
        }

        var consumed = 0;
        foreach (var candidate in page)
        {
            if (queued >= perPass)
            {
                break;
            }

            consumed++;
            queued += await TryQueueAsync(candidate, ct).ConfigureAwait(false) ? 1 : 0;
        }

        _deepOffset += consumed;
        return queued;
    }

    /// <summary>
    /// Removes the previews of files that no longer exist in the library (however they went) and any half-made folder left by a
    /// crash. Previews are only a cache, so removing too much is harmless; it is rebuilt.
    /// </summary>
    public async Task<int> PruneOrphansAsync(CancellationToken ct)
    {
        if (!Directory.Exists(store.Root))
        {
            return 0;
        }

        var removed = 0;
        foreach (var folder in Directory.EnumerateDirectories(store.Root))
        {
            ct.ThrowIfCancellationRequested();
            var name = Path.GetFileName(folder);
            try
            {
                if (name.Contains(".tmp-", StringComparison.Ordinal))
                {
                    if (Directory.GetLastWriteTimeUtc(folder) < DateTime.UtcNow.AddDays(-1))
                    {
                        store.DeleteStaging(folder);
                        removed++;
                    }
                }
                else if (Guid.TryParse(name, out var assetId) && await assets.FindByIdAsync(assetId, ct).ConfigureAwait(false) is null)
                {
                    store.Delete(assetId);
                    removed++;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogDebug(ex, "Could not remove the scrub-preview folder {Folder}", name);
            }
        }

        if (removed > 0)
        {
            logger.LogInformation("Removed {Count} scrub-preview folder(s) that no longer belong to a file.", removed);
        }

        return removed;
    }

    private async Task<int> QueuePageAsync(IReadOnlyCollection<string> videoExtensions, int offset, int perPass, CancellationToken ct)
    {
        var queued = 0;
        foreach (var candidate in await candidates.ListVideoAssetsAsync(videoExtensions, offset, PageSize, ct).ConfigureAwait(false))
        {
            if (queued >= perPass)
            {
                break;
            }

            queued += await TryQueueAsync(candidate, ct).ConfigureAwait(false) ? 1 : 0;
        }

        return queued;
    }

    private async Task<bool> TryQueueAsync(ScrubPreviewCandidate candidate, CancellationToken ct)
    {
        if (store.TryReadComplete(candidate.AssetId) is not null)
        {
            return false;
        }

        return await queue.EnqueueAsync(candidate.AssetId, candidate.FilePath, candidate.ContentHash, ct).ConfigureAwait(false);
    }
}
