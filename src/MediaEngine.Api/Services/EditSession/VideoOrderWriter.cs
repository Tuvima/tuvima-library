using System.Globalization;
using MediaEngine.Domain;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Models;

namespace MediaEngine.Api.Services.EditSession;

/// <summary>
/// Saves the hand order of the videos of one series (or section): the positions in the order store, and the same positions as locked
/// claims so a re-scan does not undo them. One write path for the Save action and for the editor's <c>video_order</c> change. The order is
/// Library's own data; no file is ever renamed here.
/// </summary>
public sealed class VideoOrderWriter(
    IVideoOrderRepository order,
    IMetadataClaimRepository claimRepo,
    ICanonicalValueRepository canonicalRepo,
    ISystemActivityRepository activityRepo)
{
    /// <summary>The placements saved, or null when the list does not name exactly the videos that are there now (nothing is saved).</summary>
    public async Task<IReadOnlyList<VideoOrderPlacement>?> SaveAsync(Guid parentWorkId, IReadOnlyList<Guid> orderedWorkIds, CancellationToken ct)
    {
        var placements = await order.ReorderAsync(parentWorkId, orderedWorkIds, ct).ConfigureAwait(false);
        if (placements is null)
        {
            return null;
        }

        var now = DateTimeOffset.UtcNow;
        var claims = new List<MetadataClaim>();
        var canonicals = new List<CanonicalValue>();
        foreach (var placement in placements)
        {
            foreach (var key in new[] { MetadataFieldConstants.EpisodeNumber, MetadataFieldConstants.VideoEpisodeIndex })
            {
                var value = placement.Position.ToString(CultureInfo.InvariantCulture);
                claims.Add(new MetadataClaim
                {
                    Id = Guid.NewGuid(),
                    EntityId = placement.AssetId,
                    ProviderId = WellKnownProviders.UserManual,
                    ClaimKey = key,
                    ClaimValue = value,
                    ClaimedAt = now,
                    Confidence = 1.0,
                    IsUserLocked = true,
                });
                canonicals.Add(new CanonicalValue
                {
                    EntityId = placement.AssetId,
                    Key = key,
                    Value = value,
                    LastScoredAt = now,
                    WinningProviderId = WellKnownProviders.UserManual,
                });
            }
        }

        await claimRepo.InsertBatchAsync(claims, ct).ConfigureAwait(false);
        await canonicalRepo.UpsertBatchAsync(canonicals, ct).ConfigureAwait(false);
        await activityRepo.LogAsync(new SystemActivityEntry
        {
            OccurredAt = now,
            ActionType = SystemActionType.MetadataManualOverride,
            EntityId = parentWorkId,
            EntityType = "Work",
            Detail = $"Put {placements.Count} videos in a new order.",
        }, ct).ConfigureAwait(false);
        return placements;
    }
}
