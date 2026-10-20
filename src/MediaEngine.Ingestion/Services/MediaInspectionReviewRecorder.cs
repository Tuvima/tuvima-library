using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Providers.Helpers;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Ingestion.Services;

/// <summary>
/// Sends a file the media tool could not read to Needs Review, then clears its "waiting for the tool" hold. The review
/// item is visible straight away: nothing the identity pipeline finds later explains an unreadable file.
/// </summary>
public sealed class MediaInspectionReviewRecorder(
    IReviewQueueRepository reviews,
    IMediaInspectionHoldRepository holds,
    ILogger<MediaInspectionReviewRecorder> logger,
    StageOutcomeFactory? outcomes = null)
{
    /// <summary>Plain-language reason shown to a person when the tool works but cannot read a file.</summary>
    public const string UnreadableFileDetail = "This file couldn't be read. It may be damaged or incomplete.";

    public async Task RecordFailureAsync(Guid assetId, CancellationToken ct)
    {
        if (outcomes is not null)
        {
            await outcomes.CreateMediaInspectionFailedAsync(assetId, UnreadableFileDetail, ct).ConfigureAwait(false);
        }
        else
        {
            var existing = await reviews.GetByEntityAsync(assetId, ct).ConfigureAwait(false);
            if (!existing.Any(r => r.Status == ReviewStatus.Pending && r.Trigger == ReviewTrigger.MediaInspectionFailed))
            {
                var now = DateTimeOffset.UtcNow;
                await reviews.InsertAsync(new ReviewQueueEntry
                {
                    Id = Guid.NewGuid(),
                    EntityId = assetId,
                    EntityType = "MediaAsset",
                    Trigger = ReviewTrigger.MediaInspectionFailed,
                    ConfidenceScore = 0.0,
                    Detail = UnreadableFileDetail,
                    CreatedAt = now,
                    ReviewReadyAt = now,
                    AutomationCompletedAt = now,
                }, ct).ConfigureAwait(false);
            }
        }

        await holds.DeleteAsync(assetId, ct).ConfigureAwait(false);
        logger.LogInformation("Asset {AssetId} could not be read by the media tool and was sent to Needs Review.", assetId);
    }
}
