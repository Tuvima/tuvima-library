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
