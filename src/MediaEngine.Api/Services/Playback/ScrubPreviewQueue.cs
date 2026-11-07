using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;

namespace MediaEngine.Api.Services.Playback;

/// <summary>Queues scrub-preview work on the durable media operation queue, at the lowest priority.</summary>
public sealed class ScrubPreviewQueue(IMediaOperationRepository operations, ScrubPreviewCache store)
{
    /// <summary>Behind every other kind of work in the queue (the default is 100).</summary>
    public const int LowestPriority = 10_000;

    public static string KeyFor(Guid assetId, string contentHash) => $"scrub-previews:{assetId:N}:{contentHash}";

    /// <summary>
    /// Queues one file. The key holds the file's content hash, so a changed file is queued again and an unchanged one never twice. A
    /// job that finished but whose previews were removed since (the switch was turned off and on) goes back in the queue. Returns true when work was queued.
    /// </summary>
    public async Task<bool> EnqueueAsync(Guid assetId, string filePath, string contentHash, CancellationToken ct)
    {
        var key = KeyFor(assetId, contentHash);
        var existing = await operations.GetByIdempotencyKeyAsync(key, ct).ConfigureAwait(false);
        if (existing is not null)
        {
            if (existing.Status == MediaOperationStatus.Succeeded && store.TryReadComplete(assetId) is null)
            {
                await operations.RequeueAsync(existing.Id, ct).ConfigureAwait(false);
                return true;
            }

            return false;
        }

        await operations.EnsureAsync(new MediaOperation
        {
            OperationType = MediaOperationType.PlaybackScrubPreviews,
            OperationKind = MediaOperationKind.Maintenance,
            EntityId = assetId,
            EntityKind = "MediaAsset",
            SourcePath = filePath,
            ContentHash = contentHash,
            Status = MediaOperationStatus.Queued,
            Stage = MediaOperationStage.Queued,
            Priority = LowestPriority,
            QueueName = MediaOperationQueue.ScrubPreviews,
            IdempotencyKey = key,
        }, ct).ConfigureAwait(false);
        return true;
    }
}
