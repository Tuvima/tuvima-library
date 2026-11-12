using MediaEngine.Contracts.Metadata;
using MediaEngine.Contracts.Realtime;
using MediaEngine.Domain;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Models;

namespace MediaEngine.Api.Services.Metadata;

/// <summary>
/// Changes the media type of an item: the one write behind <c>POST /metadata/{entityId}/reclassify</c> and the Needs Review choice for an
/// ambiguous file type. It saves user-locked media-type claims, closes the item's "which type?" questions, re-runs identity under the new
/// type and tells open Dashboards.
/// </summary>
public sealed class MediaTypeReclassifyService(
    IMetadataClaimRepository claimRepo,
    ICanonicalValueRepository canonicalRepo,
    IReviewQueueRepository reviewRepo,
    IHydrationPipelineService pipeline,
    IEventPublisher publisher,
    ISystemActivityRepository activityRepo,
    IMetadataEditorRepository metadataData)
{
    public async Task<ReclassifyResponse> ReclassifyAsync(Guid entityId, MediaType newMediaType, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var requestedEntityId = entityId;
        var reclassifyTarget = await metadataData.ResolveReclassifyTargetAsync(entityId, ct);
        var targetAssetId = reclassifyTarget.TargetAssetId;
        var targetWorkId = reclassifyTarget.WorkId;

        // 1. Create user-locked media_type claims at confidence 1.0.
        var claimEntityIds = new[] { (Guid?)targetAssetId, targetWorkId, requestedEntityId }
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .Distinct()
            .ToList();

        var claims = claimEntityIds.Select(claimEntityId => new MetadataClaim
        {
            Id = Guid.NewGuid(),
            EntityId = claimEntityId,
            ProviderId = WellKnownProviders.UserManual,
            ClaimKey = MetadataFieldConstants.MediaTypeField,
            ClaimValue = newMediaType.ToString(),
            ClaimedAt = now,
            IsUserLocked = true,
        }).ToList();
        await claimRepo.InsertBatchAsync(claims, ct);

        // 2. Upsert canonical media_type values and the work discriminator used by detail/search screens.
        await canonicalRepo.UpsertBatchAsync(claimEntityIds.Select(claimEntityId => new CanonicalValue
        {
            EntityId = claimEntityId,
            Key = MetadataFieldConstants.MediaTypeField,
            Value = newMediaType.ToString(),
            LastScoredAt = now,
            IsConflicted = false,
        })
            .ToList(), ct);

        if (targetWorkId is { } workId)
        {
            await metadataData.UpdateWorkMediaTypeAsync(workId, newMediaType.ToString(), ct);
        }

        // 3. Resolve any pending AmbiguousMediaType review items for this entity.
        bool reviewResolved = false;
        foreach (var reviewEntityId in claimEntityIds)
        {
            var reviews = await reviewRepo.GetByEntityAsync(reviewEntityId, ct);
            foreach (var review in reviews.Where(r =>
                r.Status == ReviewStatus.Pending &&
                r.Trigger == ReviewTrigger.AmbiguousMediaType))
            {
                await reviewRepo.UpdateStatusAsync(review.Id, ReviewStatus.Resolved, "user", ct);
                reviewResolved = true;
            }
        }

        // 4. Re-trigger hydration with the correct media type.
        var canonicals = await canonicalRepo.GetByEntityAsync(targetAssetId, ct);
        var hints = canonicals.ToDictionary(
            c => c.Key, c => c.Value, StringComparer.OrdinalIgnoreCase);

        await pipeline.EnqueueAsync(new HarvestRequest
        {
            EntityId = targetAssetId,
            EntityType = EntityType.MediaAsset,
            MediaType = newMediaType,
            Hints = hints,
        }, ct);

        // 5. Log activity.
        await activityRepo.LogAsync(new SystemActivityEntry
        {
            ActionType = SystemActionType.MetadataRefreshed,
            EntityId = requestedEntityId,
            EntityType = "MediaAsset",
            Detail = $"Media type reclassified to {newMediaType} by user.",
        }, ct);

        // 6. Broadcast events.
        await publisher.PublishAsync(
            SignalREvents.MetadataHarvested,
            new ReclassifiedMetadataHarvestedEvent(
                requestedEntityId,
                newMediaType.ToString()),
            ct);

        if (reviewResolved)
        {
            await publisher.PublishAsync(
                SignalREvents.ReviewItemResolved,
                new EntityReviewResolvedEvent(requestedEntityId, "Resolved"),
                ct);
        }

        return new ReclassifyResponse
        {
            EntityId = requestedEntityId,
            NewMediaType = newMediaType.ToString(),
            ReclassifiedAt = now,
            ReviewResolved = reviewResolved,
        };
    }
}
