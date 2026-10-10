using System.Text.Json;
using MediaEngine.Api.Http;
using MediaEngine.Api.Models;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services.ReadServices;
using MediaEngine.Api.Services.Review;
using MediaEngine.Contracts.Paging;
using MediaEngine.Contracts.Realtime;
using MediaEngine.Contracts.Review;
using MediaEngine.Domain;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Models;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Api.Endpoints;

/// <summary>
/// Review queue API endpoints for managing ambiguous or low-confidence
/// hydration results that require user intervention.
///
/// Spec: Sprint 4 — Review Queue Endpoints.
/// </summary>
public static class ReviewEndpoints
{

    public static IEndpointRouteBuilder MapReviewEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/review")
                       .WithTags("Review Queue");

        // ── GET /review/pending ──────────────────────────────────────────────
        group.MapGet("/pending", async (
            int? limit,
            IReviewQueueReadService reviewReadService,
            CancellationToken ct) =>
        {
            // Recently Added owns the complete unresolved queue. The caller first
            // reads the exact count, then requests that many rows; unlike history,
            // this intentionally is not paged in the UI.
            var paged = PagedRequest.From(null, limit, defaultLimit: 50, maxLimit: int.MaxValue);
            var dtos = await reviewReadService.GetPendingAsync(paged.Limit, ct);
            return Results.Ok(dtos);
        })
        .WithName("GetPendingReviews")
        .WithSummary("List pending review queue items.")
        .Produces<List<MediaEngine.Contracts.Review.ReviewItemDto>>(StatusCodes.Status200OK)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.ReviewRead);

        // ── GET /review/count ────────────────────────────────────────────────
        group.MapGet("/count", async (
            IReviewQueueReadService reviewReadService,
            CancellationToken ct) =>
        {
            var count = await reviewReadService.GetPendingCountAsync(ct);
            return Results.Ok(new MediaEngine.Contracts.Review.ReviewCountResponse { PendingCount = count });
        })
        .WithName("GetReviewCount")
        .WithSummary("Get the number of pending review queue items (for sidebar badge).")
        .Produces<MediaEngine.Contracts.Review.ReviewCountResponse>(StatusCodes.Status200OK)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.ReviewRead);

        // ── GET /review/{id} ─────────────────────────────────────────────────
        group.MapGet("/{id:guid}", async (
            Guid id,
            IReviewQueueReadService reviewReadService,
            CancellationToken ct) =>
        {
            var item = await reviewReadService.GetByIdAsync(id, ct);
            return item is null ? ApiErrors.NotFound($"Review item '{id}' not found.") : Results.Ok(item);
        })
        .WithName("GetReviewItem")
        .WithSummary("Get a single review queue item with full details.")
        .Produces<MediaEngine.Contracts.Review.ReviewItemDto>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.ReviewRead);

        // ── POST /review/{id}/resolve ────────────────────────────────────────
        group.MapPost("/{id:guid}/resolve", async (
            Guid id,
            ReviewResolveRequestDto request,
            IReviewQueueRepository reviewRepo,
            IMetadataClaimRepository claimRepo,
            IHydrationPipelineService pipeline,
            ICanonicalValueRepository canonicalRepo,
            IEventPublisher publisher,
            ISystemActivityRepository activityRepo,
            CancellationToken ct) =>
        {
            var item = await reviewRepo.GetByIdAsync(id, ct);
            if (item is null)
            {
                return ApiErrors.NotFound($"Review item '{id}' not found.");
            }

            if (item.Status != ReviewStatus.Pending)
            {
                return ApiErrors.BadRequest("Review item is not pending.");
            }

            // 1. Apply user field overrides (creates user-locked claims).
            if (request.FieldOverrides is { Count: > 0 })
            {
                foreach (var ov in request.FieldOverrides)
                {
                    var claim = new MetadataClaim
                    {
                        Id = Guid.NewGuid(),
                        EntityId = item.EntityId,
                        ProviderId = WellKnownProviders.UserManual,
                        ClaimKey = ov.Key,
                        ClaimValue = ov.Value,
                        Confidence = 1.0,
                        ClaimedAt = DateTimeOffset.UtcNow,
                        IsUserLocked = true,
                    };
                    await claimRepo.InsertBatchAsync([claim], ct);
                }
            }

            // 1b. Always persist the selected QID as a user-locked claim.
            //     This guarantees the QID survives re-scoring and startup sweeps
            //     regardless of whether the pipeline deposits its own QID claim.
            if (!string.IsNullOrWhiteSpace(request.SelectedQid))
            {
                var qidClaim = new MetadataClaim
                {
                    Id = Guid.NewGuid(),
                    EntityId = item.EntityId,
                    ProviderId = WellKnownProviders.UserManual,
                    ClaimKey = "wikidata_qid",
                    ClaimValue = request.SelectedQid,
                    Confidence = 1.0,
                    ClaimedAt = DateTimeOffset.UtcNow,
                    IsUserLocked = true,
                };
                await claimRepo.InsertBatchAsync([qidClaim], ct);

                // Also upsert the canonical value immediately so it's visible
                // even if the pipeline run adds zero claims.
                await canonicalRepo.UpsertBatchAsync([new Domain.Entities.CanonicalValue
                {
                    EntityId     = item.EntityId,
                    Key          = "wikidata_qid",
                    Value        = request.SelectedQid,
                    LastScoredAt = DateTimeOffset.UtcNow,
                }], ct);
            }

            // 2. If a QID was selected, re-run pipeline with PreResolvedQid.
            if (!string.IsNullOrWhiteSpace(request.SelectedQid))
            {
                var canonicals = await canonicalRepo.GetByEntityAsync(item.EntityId, ct);
                var hints = canonicals.ToDictionary(
                    c => c.Key, c => c.Value, StringComparer.OrdinalIgnoreCase);

                await pipeline.RunSynchronousAsync(new HarvestRequest
                {
                    EntityId = item.EntityId,
                    EntityType = EntityType.MediaAsset,
                    MediaType = Domain.Enums.MediaType.Unknown,
                    Hints = hints,
                    PreResolvedQid = request.SelectedQid,
                    SuppressReviewCreation = true,
                }, ct);
            }

            // 3. Update review queue status.
            await reviewRepo.UpdateStatusAsync(id, ReviewStatus.Resolved, "user", ct);

            // 4. Log activity with canonical values for Dashboard rich card.
            var resolvedCanonicals = await canonicalRepo.GetByEntityAsync(item.EntityId, ct);
            var resolvedLookup = resolvedCanonicals.ToDictionary(
                c => c.Key, c => c.Value, StringComparer.OrdinalIgnoreCase);
            resolvedLookup.TryGetValue(MetadataFieldConstants.Title, out var rTitle);
            if (string.IsNullOrWhiteSpace(rTitle))
            {
                resolvedLookup.TryGetValue("file_name", out rTitle);
            }

            resolvedLookup.TryGetValue(MetadataFieldConstants.Author, out var rAuthor);
            resolvedLookup.TryGetValue(MetadataFieldConstants.Year, out var rYear);
            resolvedLookup.TryGetValue(MetadataFieldConstants.Description, out var rDesc);
            resolvedLookup.TryGetValue(MetadataFieldConstants.MediaTypeField, out var rMediaType);
            if (!resolvedLookup.TryGetValue(MetadataFieldConstants.CoverUrl, out var rCover))
            {
                resolvedLookup.TryGetValue(MetadataFieldConstants.Cover, out rCover);
            }

            await activityRepo.LogAsync(new SystemActivityEntry
            {
                ActionType = SystemActionType.ReviewItemResolved,
                EntityId = item.EntityId,
                ChangesJson = JsonSerializer.Serialize(new
                {
                    title = rTitle,
                    author = rAuthor,
                    year = rYear,
                    description = rDesc,
                    media_type = rMediaType,
                    entity_id = item.EntityId.ToString(),
                    action = "resolved",
                    qid = request.SelectedQid,
                    field_overrides = request.FieldOverrides?.Count ?? 0,
                    cover_url = rCover,
                }),
                Detail = $"Review resolved — QID: {request.SelectedQid ?? "none"}, "
                            + $"{request.FieldOverrides?.Count ?? 0} field overrides.",
            }, ct);

            // 5. Broadcast event.
            await publisher.PublishAsync(
                SignalREvents.ReviewItemResolved,
                new ReviewItemResolvedSupplementaryEvent(
                    id,
                    item.EntityId,
                    "Resolved"),
                ct);

            return Results.Ok(new ReviewResolveResponse(resolved: true, review_item_id: id));
        })
        .WithName("ResolveReviewItem")
        .WithSummary("Resolve a review queue item: select a QID and/or override fields.")
        .Produces<ReviewResolveResponse>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.ReviewResolve);

        // ── POST /review/{id}/dismiss ────────────────────────────────────────
        group.MapPost("/{id:guid}/dismiss", async (
            Guid id,
            IReviewQueueRepository reviewRepo,
            ICanonicalValueRepository canonicalRepo,
            IEventPublisher publisher,
            ISystemActivityRepository activityRepo,
            CancellationToken ct) =>
        {
            var item = await reviewRepo.GetByIdAsync(id, ct);
            if (item is null)
            {
                return ApiErrors.NotFound($"Review item '{id}' not found.");
            }

            if (item.Status != ReviewStatus.Pending)
            {
                return ApiErrors.BadRequest("Review item is not pending.");
            }

            await reviewRepo.UpdateStatusAsync(id, ReviewStatus.Dismissed, "user", ct);

            var dismissCanonicals = await canonicalRepo.GetByEntityAsync(item.EntityId, ct);
            var dismissLookup = dismissCanonicals.ToDictionary(
                c => c.Key, c => c.Value, StringComparer.OrdinalIgnoreCase);
            dismissLookup.TryGetValue(MetadataFieldConstants.Title, out var dTitle);
            if (string.IsNullOrWhiteSpace(dTitle))
            {
                dismissLookup.TryGetValue("file_name", out dTitle);
            }

            dismissLookup.TryGetValue(MetadataFieldConstants.Author, out var dAuthor);
            dismissLookup.TryGetValue(MetadataFieldConstants.Year, out var dYear);
            dismissLookup.TryGetValue(MetadataFieldConstants.Description, out var dDesc);
            dismissLookup.TryGetValue(MetadataFieldConstants.MediaTypeField, out var dMediaType);
            if (!dismissLookup.TryGetValue(MetadataFieldConstants.CoverUrl, out var dCover))
            {
                dismissLookup.TryGetValue(MetadataFieldConstants.Cover, out dCover);
            }

            await activityRepo.LogAsync(new SystemActivityEntry
            {
                ActionType = SystemActionType.ReviewItemResolved,
                EntityId = item.EntityId,
                ChangesJson = JsonSerializer.Serialize(new
                {
                    title = dTitle,
                    author = dAuthor,
                    year = dYear,
                    description = dDesc,
                    media_type = dMediaType,
                    entity_id = item.EntityId.ToString(),
                    action = "dismissed",
                    cover_url = dCover,
                }),
                Detail = "Review item dismissed by user.",
            }, ct);

            await publisher.PublishAsync(
                SignalREvents.ReviewItemResolved,
                new ReviewItemResolvedSupplementaryEvent(
                    id,
                    item.EntityId,
                    "Dismissed"),
                ct);

            return Results.Ok(new ReviewDismissResponse(dismissed: true, review_item_id: id));
        })
        .WithName("DismissReviewItem")
        .WithSummary("Dismiss a review queue item as irrelevant.")
        .Produces<ReviewDismissResponse>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.ReviewResolve);

        // ── POST /review/{id}/move-to-tv ─────────────────────────────────────
        // The user accepted the "Found as a TV title" suggestion. Logical move only: the file on
        // disk is never touched.
        group.MapPost("/{id:guid}/move-to-tv", async (
            Guid id,
            IReviewMoveToTvService moveToTv,
            CancellationToken ct) =>
        {
            var result = await moveToTv.MoveAsync(id, "user", ct);
            return result.Outcome switch
            {
                ReviewMoveToTvOutcome.Moved => Results.Ok(result.Response),
                ReviewMoveToTvOutcome.ReviewItemNotFound => ApiErrors.NotFound(result.Message ?? "Review item not found."),
                ReviewMoveToTvOutcome.NotPending or ReviewMoveToTvOutcome.WrongTrigger
                    => ApiErrors.BadRequest(result.Message ?? "Review item cannot be moved to TV."),
                _ => ApiErrors.Conflict(result.Message ?? "The item could not be moved to TV."),
            };
        })
        .WithName("MoveReviewItemToTv")
        .WithSummary("Accept a 'Found as a TV title' suggestion: re-file the film as Season 0 Episode 1 of the TMDB show in the TV library. The file on disk is not moved.")
        .Produces<ReviewMoveToTvResponse>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status409Conflict)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.ReviewResolve);

        // ── POST /review/{id}/skip-universe ────────────────────────────────
        group.MapPost("/{id:guid}/skip-universe", async (
            Guid id,
            IReviewQueueRepository reviewRepo,
            ICollectionRepository collectionRepo,
            ICanonicalValueRepository canonicalRepo,
            IEventPublisher publisher,
            ISystemActivityRepository activityRepo,
            CancellationToken ct) =>
        {
            var item = await reviewRepo.GetByIdAsync(id, ct);
            if (item is null)
            {
                return ApiErrors.NotFound($"Review item '{id}' not found.");
            }

            if (item.Status != ReviewStatus.Pending)
            {
                return ApiErrors.BadRequest("Review item is not pending.");
            }

            // 1. Set universe_mismatch flag on the Work.
            await collectionRepo.SetUniverseMismatchAsync(item.EntityId, ct);

            // 2. Dismiss the review item.
            await reviewRepo.UpdateStatusAsync(id, ReviewStatus.Dismissed, "user", ct);

            var skipCanonicals = await canonicalRepo.GetByEntityAsync(item.EntityId, ct);
            var skipLookup = skipCanonicals.ToDictionary(
                c => c.Key, c => c.Value, StringComparer.OrdinalIgnoreCase);
            skipLookup.TryGetValue(MetadataFieldConstants.Title, out var sTitle);
            if (string.IsNullOrWhiteSpace(sTitle))
            {
                skipLookup.TryGetValue("file_name", out sTitle);
            }

            skipLookup.TryGetValue(MetadataFieldConstants.Author, out var sAuthor);
            skipLookup.TryGetValue(MetadataFieldConstants.Year, out var sYear);
            skipLookup.TryGetValue(MetadataFieldConstants.Description, out var sDesc);
            skipLookup.TryGetValue(MetadataFieldConstants.MediaTypeField, out var sMediaType);
            if (!skipLookup.TryGetValue(MetadataFieldConstants.CoverUrl, out var sCover))
            {
                skipLookup.TryGetValue(MetadataFieldConstants.Cover, out sCover);
            }

            // 3. Log activity.
            await activityRepo.LogAsync(new SystemActivityEntry
            {
                ActionType = SystemActionType.ReviewItemResolved,
                EntityId = item.EntityId,
                ChangesJson = JsonSerializer.Serialize(new
                {
                    title = sTitle,
                    author = sAuthor,
                    year = sYear,
                    description = sDesc,
                    media_type = sMediaType,
                    entity_id = item.EntityId.ToString(),
                    action = "skipped",
                    cover_url = sCover,
                }),
                Detail = "Universe matching skipped by user.",
            }, ct);

            // 4. Broadcast event.
            await publisher.PublishAsync(
                SignalREvents.ReviewItemResolved,
                new ReviewItemResolvedSupplementaryEvent(
                    id,
                    item.EntityId,
                    "Dismissed"),
                ct);

            return Results.Ok(new ReviewSkipUniverseResponse(skipped: true, review_item_id: id));
        })
        .WithName("SkipUniverseMatch")
        .WithSummary("Skip Universe (Wikidata) matching for this item. Sets universe_mismatch on the Work and dismisses the review item.")
        .Produces<ReviewSkipUniverseResponse>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.ReviewResolve);

        return app;
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

}
