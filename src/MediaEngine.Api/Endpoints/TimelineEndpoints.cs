using MediaEngine.Api.Http;
using MediaEngine.Api.Security;
using MediaEngine.Contracts.Timeline;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Models;

namespace MediaEngine.Api.Endpoints;

/// <summary>
/// Timeline API endpoints — full event history and pipeline provenance for each entity.
/// All routes are grouped under <c>/timeline</c>.
///
/// <list type="bullet">
///   <item><c>GET  /timeline/{entityId}</c>                            — full event history, newest first</item>
///   <item><c>GET  /timeline/{entityId}/pipeline</c>                   — current pipeline state (latest per stage)</item>
///   <item><c>GET  /timeline/{entityId}/event/{eventId}/changes</c>    — field-level changes for one event</item>
/// </list>
/// </summary>
public static class TimelineEndpoints
{
    public static IEndpointRouteBuilder MapTimelineEndpoints(this IEndpointRouteBuilder app)
    {
        var grp = app.MapGroup("/timeline").WithTags("Timeline");

        // ── GET /timeline/{entityId} ──────────────────────────────────────────
        grp.MapGet("/{entityId:guid}", async (
            Guid entityId,
            IEntityTimelineRepository repo,
            CancellationToken ct) =>
        {
            var events = await repo.GetEventsByEntityAsync(entityId, ct);
            return Results.Ok(events.Select(MapEvent).ToList());
        })
        .WithName("GetEntityTimeline")
        .WithSummary("Returns the full event history for an entity, newest first.")
        .Produces<IReadOnlyList<EntityTimelineEventDto>>(StatusCodes.Status200OK)
        .RequireClientScope(ApplicationPermissionIds.MetadataEnrichmentRead.Value)
        .RequireAnyCatalogueEntityAccess(ApplicationPermissionIds.MetadataEnrichmentRead);

        // ── GET /timeline/{entityId}/pipeline ─────────────────────────────────
        grp.MapGet("/{entityId:guid}/pipeline", async (
            Guid entityId,
            IEntityTimelineRepository repo,
            CancellationToken ct) =>
        {
            var state = await repo.GetCurrentPipelineStateAsync(entityId, ct);
            return Results.Ok(state.Select(MapEvent).ToList());
        })
        .WithName("GetPipelineState")
        .WithSummary("Returns the most recent event per pipeline stage for an entity.")
        .Produces<IReadOnlyList<EntityTimelineEventDto>>(StatusCodes.Status200OK)
        .RequireClientScope(ApplicationPermissionIds.MetadataEnrichmentRead.Value)
        .RequireAnyCatalogueEntityAccess(ApplicationPermissionIds.MetadataEnrichmentRead);

        // ── GET /timeline/{entityId}/event/{eventId}/changes ──────────────────
        grp.MapGet("/{entityId:guid}/event/{eventId:guid}/changes", async (
            Guid entityId,
            Guid eventId,
            IEntityTimelineRepository repo,
            CancellationToken ct) =>
        {
            var ownsEvent = (await repo.GetEventsByEntityAsync(entityId, ct))
                .Any(entityEvent => entityEvent.Id == eventId);
            if (!ownsEvent)
            {
                return ApiErrors.NotFound($"Timeline event '{eventId}' was not found for entity '{entityId}'.");
            }

            var changes = await repo.GetFieldChangesByEventAsync(eventId, ct);
            return Results.Ok(changes.Select(MapFieldChange).ToList());
        })
        .WithName("GetEventFieldChanges")
        .WithSummary("Returns field-level changes for a specific event.")
        .Produces<IReadOnlyList<EntityTimelineFieldChangeDto>>(StatusCodes.Status200OK)
        .RequireClientScope(ApplicationPermissionIds.MetadataEnrichmentRead.Value)
        .RequireAnyCatalogueEntityAccess(ApplicationPermissionIds.MetadataEnrichmentRead);

        // ── POST /timeline/{entityId}/rematch ─────────────────────────────────
        grp.MapPost("/{entityId:guid}/rematch", async (
            Guid entityId,
            IEntityTimelineRepository timelineRepo,
            ICanonicalValueRepository canonicalRepo,
            IHydrationPipelineService pipeline,
            IMediaAssetRepository assetRepo,
            CancellationToken ct) =>
        {
            // Verify the entity exists
            var asset = await assetRepo.FindByIdAsync(entityId, ct);
            if (asset is null)
            {
                return ApiErrors.NotFound("Entity not found");
            }

            // Snapshot current canonicals for pre/post diff
            var beforeValues = await canonicalRepo.GetByEntityAsync(entityId, ct);

            // Resolve media type from canonical values (fallback to Unknown)
            var mediaTypeStr = beforeValues
                .FirstOrDefault(cv => string.Equals(cv.Key, "media_type", StringComparison.OrdinalIgnoreCase))
                ?.Value;
            if (!Enum.TryParse<MediaType>(mediaTypeStr, ignoreCase: true, out var mediaType))
            {
                mediaType = MediaType.Unknown;
            }

            // Record the re-match initiation event
            await timelineRepo.InsertEventAsync(new EntityEvent
            {
                EntityId = entityId,
                EntityType = "Work",
                EventType = "retail_rematched",
                Stage = 1,
                Trigger = "user_rematch",
                Detail = $"Re-match initiated by user — {beforeValues.Count} existing canonicals",
            }, ct);

            // Re-enqueue through the pipeline
            var hints = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var cv in beforeValues)
            {
                if (!string.IsNullOrWhiteSpace(cv.Value))
                {
                    hints[cv.Key] = cv.Value;
                }
            }

            await pipeline.EnqueueAsync(new HarvestRequest
            {
                EntityId = entityId,
                EntityType = EntityType.MediaAsset,
                MediaType = mediaType,
                Hints = hints,
            }, ct);

            return Results.Ok(new RematchEntityResponse(queued: true, entityId));
        })
        .WithName("RematchEntity")
        .WithSummary("Re-matches an entity through the full pipeline.")
        .Produces<RematchEntityResponse>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.MetadataMatch)
        .RequireCatalogueAssetAccess(ApplicationPermissionIds.MetadataMatch);

        return app;
    }

    private static EntityTimelineEventDto MapEvent(EntityEvent source) => new()
    {
        Id = source.Id,
        EntityId = source.EntityId,
        EntityType = source.EntityType,
        EventType = source.EventType,
        Stage = source.Stage,
        Trigger = source.Trigger,
        ProviderId = source.ProviderId,
        ProviderName = source.ProviderName,
        BridgeIdType = source.BridgeIdType,
        BridgeIdValue = source.BridgeIdValue,
        ResolvedQid = source.ResolvedQid,
        Confidence = source.Confidence,
        ScoreTitle = source.ScoreTitle,
        ScoreAuthor = source.ScoreAuthor,
        ScoreYear = source.ScoreYear,
        ScoreFormat = source.ScoreFormat,
        ScoreCrossField = source.ScoreCrossField,
        ScoreCoverArt = source.ScoreCoverArt,
        ScoreComposite = source.ScoreComposite,
        OccurredAt = source.OccurredAt,
        Detail = source.Detail,
    };

    private static EntityTimelineFieldChangeDto MapFieldChange(EntityFieldChange source) => new()
    {
        Id = source.Id,
        EventId = source.EventId,
        EntityId = source.EntityId,
        Field = source.Field,
        OldValue = source.OldValue,
        NewValue = source.NewValue,
        OldProviderId = source.OldProviderId,
        NewProviderId = source.NewProviderId,
        Confidence = source.Confidence,
        IsFileOriginal = source.IsFileOriginal,
    };
}
