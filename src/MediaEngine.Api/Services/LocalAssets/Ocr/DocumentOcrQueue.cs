using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Api.Services.LocalAssets.Ocr;

/// <summary>
/// Puts text recognition work on the durable queue. Work is only queued while recognition is switched on
/// and the helper is installed, so a missing helper never fills the queue with jobs that cannot run.
/// </summary>
public sealed class DocumentOcrQueue(
    IMediaOperationRepository operations,
    ILocalAssetRepository assets,
    IOcrService ocr,
    IConfigurationLoader configuration,
    ILogger<DocumentOcrQueue> logger)
{
    public const string QueueName = "view_document_ocr";

    private const int RereadPageSize = 200;
    private int _rereadRunning;

    /// <summary>Items queued by the most recent "read all again" run, and whether it is still going.</summary>
    public (bool Running, int Queued) RereadProgress => (Volatile.Read(ref _rereadRunning) == 1, _rereadQueued);

    private int _rereadQueued;

    /// <summary>True when recognition is switched on and the helper and its language data are in place.</summary>
    public async Task<bool> IsReadyAsync(CancellationToken ct)
    {
        if (!configuration.LoadOcr().Enabled)
        {
            return false;
        }

        return (await ocr.GetCapabilityAsync(ct).ConfigureAwait(false)).IsAvailable;
    }

    /// <summary>Queues one file for recognition. The key includes the content hash, so an edited file queues again.</summary>
    public async Task<bool> TryQueueAsync(
        Guid itemId,
        string contentHash,
        string sourcePath,
        CancellationToken ct,
        string? rereadStamp = null)
    {
        if (!await IsReadyAsync(ct).ConfigureAwait(false))
        {
            return false;
        }

        var key = $"view-document-ocr:{itemId:D}:{contentHash}";
        if (rereadStamp is not null)
        {
            key += ":" + rereadStamp;
        }

        await operations.EnsureAsync(new MediaOperation
        {
            OperationType = MediaOperationType.ViewDocumentOcr,
            OperationKind = MediaOperationKind.ViewDocument,
            EntityId = itemId,
            EntityKind = "LocalItem",
            SourcePath = sourcePath,
            ContentHash = contentHash,
            Status = MediaOperationStatus.Queued,
            Stage = MediaOperationStage.Queued,
            QueueName = QueueName,
            IdempotencyKey = key,
        }, ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Starts queueing every unread PDF and photo again, a page of items at a time so the data store is
    /// never hammered. Returns false when a run is already going or recognition is not ready.
    /// </summary>
    public async Task<bool> StartRereadAsync(CancellationToken ct)
    {
        if (!await IsReadyAsync(ct).ConfigureAwait(false) || Interlocked.CompareExchange(ref _rereadRunning, 1, 0) != 0)
        {
            return false;
        }

        Interlocked.Exchange(ref _rereadQueued, 0);
        var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture);
        _ = Task.Run(() => RereadAsync(stamp), CancellationToken.None);
        return true;
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, bool> _photoScans = new();

    /// <summary>True while the words in this library's photos are still being queued.</summary>
    public bool IsPhotoScanRunning(Guid libraryId) => _photoScans.ContainsKey(libraryId);

    /// <summary>
    /// Queues every photo of one person's own library whose words have not been read yet (spec view-people-search WP5), a page at a time.
    /// Only that one library is ever read, never the Vault and never a shared or another person's library. Returns false when text recognition
    /// is not ready or a scan of this library is already going.
    /// </summary>
    public async Task<bool> StartPhotoWordsScanAsync(Guid libraryId, CancellationToken ct)
    {
        if (!await IsReadyAsync(ct).ConfigureAwait(false) || !_photoScans.TryAdd(libraryId, true))
        {
            return false;
        }

        _ = Task.Run(() => ScanPhotosAsync(libraryId), CancellationToken.None);
        return true;
    }

    /// <summary>Queues the photos of a library now (awaitable); used by the background scan and by tests.</summary>
    public async Task<int> QueuePhotoWordsAsync(Guid libraryId, CancellationToken ct)
    {
        var queued = 0;
        Guid? after = null;
        while (true)
        {
            var page = await assets.GetPhotoWordsCandidatesAsync([libraryId], after, RereadPageSize, OcrFormats.Images, ct)
                .ConfigureAwait(false);
            if (page.Count == 0)
            {
                return queued;
            }

            foreach (var candidate in page)
            {
                if (await TryQueueAsync(candidate.ItemId, candidate.ContentHash, candidate.FilePath, ct).ConfigureAwait(false))
                {
                    queued++;
                }
            }

            after = page[^1].ItemId;
            await Task.Delay(TimeSpan.FromMilliseconds(100), ct).ConfigureAwait(false);
        }
    }

    private async Task ScanPhotosAsync(Guid libraryId)
    {
        try
        {
            var queued = await QueuePhotoWordsAsync(libraryId, CancellationToken.None).ConfigureAwait(false);
            logger.LogInformation("Queued {Count} photos to have their words read.", queued);
        }
        catch (Exception ex)
        {
            // A background batch has no caller to report to; log it. Switching the option off and on again starts it again.
            logger.LogError(ex, "Queueing photos to have their words read stopped early.");
        }
        finally
        {
            _photoScans.TryRemove(libraryId, out _);
        }
    }

    private async Task<bool> HasActiveJobAsync(Guid itemId) =>
        (await operations.GetByEntityAsync(itemId, CancellationToken.None).ConfigureAwait(false)).Any(operation =>
            operation.OperationType == MediaOperationType.ViewDocumentOcr
            && operation.Status is MediaOperationStatus.Queued or MediaOperationStatus.Leased
                or MediaOperationStatus.Running or MediaOperationStatus.RetryWaiting);

    private async Task RereadAsync(string stamp)
    {
        try
        {
            Guid? after = null;
            while (true)
            {
                var page = await assets.GetOcrCandidatesAsync(after, RereadPageSize, OcrFormats.Images, CancellationToken.None)
                    .ConfigureAwait(false);
                if (page.Count == 0)
                {
                    break;
                }

                foreach (var candidate in page)
                {
                    if (await HasActiveJobAsync(candidate.ItemId).ConfigureAwait(false))
                    {
                        continue;
                    }

                    await TryQueueAsync(candidate.ItemId, candidate.ContentHash, candidate.FilePath, CancellationToken.None, stamp)
                        .ConfigureAwait(false);
                    Interlocked.Increment(ref _rereadQueued);
                }

                after = page[^1].ItemId;
                await Task.Delay(TimeSpan.FromMilliseconds(100)).ConfigureAwait(false);
            }

            logger.LogInformation("Queued {Count} documents to be read again.", _rereadQueued);
        }
        catch (Exception ex)
        {
            // A background batch has no caller to report to; log it so the person can try again.
            logger.LogError(ex, "Queueing documents to be read again stopped early.");
        }
        finally
        {
            Volatile.Write(ref _rereadRunning, 0);
        }
    }
}
