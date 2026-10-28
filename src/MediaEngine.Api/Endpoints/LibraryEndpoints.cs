using MediaEngine.Api.Http;
using MediaEngine.Api.Models;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services.BatchEdit;
using MediaEngine.Api.Services.Display;
using MediaEngine.Api.Services.EditSession;
using MediaEngine.Api.Services.ReadServices;
using MediaEngine.Contracts.Collections;
using MediaEngine.Contracts.Editing;
using MediaEngine.Contracts.Library;
using MediaEngine.Contracts.Paging;
using MediaEngine.Domain;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Services;
using MediaEngine.Storage.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace MediaEngine.Api.Endpoints;

/// <summary>
/// Library endpoints for operational health, batch edits, and universe curation.
/// </summary>
public static class LibraryEndpoints
{
    public static IEndpointRouteBuilder MapLibraryEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/library")
                       .WithTags("Library");

        // ── GET /library/overview ─────────────────────────────────────────────
        group.MapGet("/overview", async (
            ILibraryItemRepository libraryItemRepo,
            ILibraryOverviewReadService overviewReadService,
            CancellationToken ct) =>
        {
            // The lifecycle counts and the projection summary come from one pass over the item projection, which is the
            // expensive part of this read (it was built three times, B17). The other two reads are small.
            var counts = await libraryItemRepo.GetOverviewCountsAsync(ct);
            var mediaTypeCountsTask = libraryItemRepo.GetOwnedMediaTypeCountsAsync(ct);
            var overviewTask = overviewReadService.GetOverviewAggregatesAsync(ct);
            await Task.WhenAll(mediaTypeCountsTask, overviewTask);

            var fourState = counts.Lifecycle;
            var projection = counts.Projection;
            // Admin overview media type counts should reflect owned media assets,
            // not catalogue-only works discovered during enrichment.
            var mediaTypeCounts = await mediaTypeCountsTask;
            var ownedTotal = mediaTypeCounts.Values.Sum();

            // 4. Review-ready count from the shared libraryItem projection
            var reviewTotal = fourState.InReview;

            var overview = await overviewTask;

            var dto = new LibraryOverviewDto
            {
                TotalItems = ownedTotal,
                Added24h = overview.Added24h,
                Added7d = overview.Added7d,
                Added30d = overview.Added30d,
                PipelineStates = overview.PipelineStates.ToDictionary(kv => kv.Key, kv => kv.Value),
                PipelineSuccessRate = overview.PipelineSuccessRate,
                ReviewCategories = fourState.TriggerCounts.ToDictionary(kv => kv.Key, kv => kv.Value),
                ReviewTotal = reviewTotal,
                WithQid = projection.WithQid,
                WithoutQid = projection.WithoutQid,
                EnrichedStage3 = projection.EnrichedStage3,
                NotEnrichedStage3 = projection.NotEnrichedStage3,
                UniverseAssigned = projection.UniverseAssigned,
                UniverseUnassigned = projection.UniverseUnassigned,
                StaleItems = projection.StaleItems,
                MediaTypeCounts = mediaTypeCounts,
                HiddenByQualityGate = projection.HiddenByQualityGate,
                ArtPending = projection.ArtPending,
                RetailNeedsReview = projection.RetailNeedsReview,
                QidNoMatch = projection.QidNoMatch,
                CompletedWithArt = projection.CompletedWithArt,
            };

            return Results.Ok(dto);
        })
        .WithName("GetLibraryOverview")
        .WithSummary("Aggregated operational health summary for the library dashboard.")
        .Produces<LibraryOverviewDto>(StatusCodes.Status200OK)
        .RequireEffectiveAdministrator();

        group.MapGet("/works", async (
            ILibraryWorkFeedReadService workFeedReadService,
            IDisplayProjectionReadService display,
            ILoggerFactory loggerFactory,
            int? offset,
            int? limit,
            CancellationToken ct) =>
        {
            ct.ThrowIfCancellationRequested();

            var page = PagedRequest.From(offset, limit, defaultLimit: 100, maxLimit: 500);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var logger = loggerFactory.CreateLogger("MediaEngine.Api.LibraryWorks");
            var allowedWorkIds = await display.LoadVisibleWorkIdsAsync(ct);
            var response = await workFeedReadService.GetWorksAsync(page, ct, allowedWorkIds);
            sw.Stop();
            if (sw.ElapsedMilliseconds >= 1000)
            {
                logger.LogWarning(
                    "Large-list read {Operation} took {ElapsedMs} ms with offset {Offset}, limit {Limit}, returned {ItemCount}, has_more {HasMore}",
                    "library.works",
                    sw.ElapsedMilliseconds,
                    response.Offset,
                    response.Limit,
                    response.Items.Count,
                    response.HasMore);
            }
            else
            {
                logger.LogDebug(
                    "Large-list read {Operation} took {ElapsedMs} ms with offset {Offset}, limit {Limit}, returned {ItemCount}, has_more {HasMore}",
                    "library.works",
                    sw.ElapsedMilliseconds,
                    response.Offset,
                    response.Limit,
                    response.Items.Count,
                    response.HasMore);
            }

            return Results.Ok(response);
        })
        .WithName("GetLibraryWorks")
        .WithSummary("Returns library-owned works for the home and browse surfaces.")
        .Produces<PagedResponse<LibraryWorkListItemDto>>(StatusCodes.Status200OK)
        .RequireClientScope(ApplicationPermissionIds.LibraryRead.Value);

        // ── POST /library/batch-edit/preview ────────────────────────────────
        group.MapPost("/batch-edit/preview", async (
            LibraryBatchEditPreviewRequest request,
            ILibraryBatchEditService batchEdit,
            LibraryBatchEditAccessService access,
            HttpContext http,
            CancellationToken ct) =>
        {
            if (request.EntityIds.Count == 0)
            {
                return ApiErrors.BadRequest("Must provide entity IDs.");
            }

            if (request.EntityIds.Count > LibraryBatchEditRequest.MaxEntityIds)
            {
                return ApiErrors.BadRequest($"A batch edit takes at most {LibraryBatchEditRequest.MaxEntityIds} items.");
            }

            var fieldKeys = (request.FieldKeys ?? []).Where(key => !string.IsNullOrWhiteSpace(key)).ToList();
            var unknown = fieldKeys.Where(key => !BatchEditFieldCatalog.TryGet(key, out _)).ToList();
            if (unknown.Count > 0)
            {
                return ApiErrors.BadRequest($"Unknown batch field(s): {string.Join(", ", unknown)}.");
            }

            var refusals = await access.FindRefusalsAsync(http, request.EntityIds, ct);
            return Results.Ok(await batchEdit.PreviewAsync(request.EntityIds, fieldKeys, refusals, ct));
        })
        .WithName("PreviewBatchEdit")
        .WithSummary("Shows how the selected items stand on each batch-editable field: the shared value or Mixed, list or single value, and which items are locked.")
        .Produces<LibraryBatchEditPreview>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .RequireEffectiveAdministrator();

        // ── POST /library/batch-edit ────────────────────────────────────────
        group.MapPost("/batch-edit", async (
            LibraryBatchEditRequest request,
            ILibraryBatchEditService batchEdit,
            LibraryBatchEditAccessService access,
            HttpContext http,
            CancellationToken ct) =>
        {
            if (request.EntityIds.Count == 0 || request.FieldChanges.Count == 0)
            {
                return ApiErrors.BadRequest("Must provide entity IDs and field changes.");
            }

            if (request.EntityIds.Count > LibraryBatchEditRequest.MaxEntityIds)
            {
                return ApiErrors.BadRequest($"A batch edit takes at most {LibraryBatchEditRequest.MaxEntityIds} items.");
            }

            var changes = new List<BatchEditChange>(request.FieldChanges.Count);
            foreach (var requested in request.FieldChanges)
            {
                if (!BatchEditFieldCatalog.TryValidateChange(
                        requested.Key, requested.Op, requested.Value, requested.Values, out var change, out var error))
                {
                    return ApiErrors.BadRequest(error ?? "Invalid batch change.");
                }

                if (changes.Any(existing => string.Equals(existing.Field.Key, change.Field.Key, StringComparison.OrdinalIgnoreCase)))
                {
                    return ApiErrors.BadRequest($"Only one change per field is allowed ({change.Field.Label}).");
                }

                changes.Add(change);
            }

            var refusals = await access.FindRefusalsAsync(http, request.EntityIds, ct);
            return Results.Ok(await batchEdit.ApplyAsync(request.EntityIds, changes, refusals, ct));
        })
        .WithName("ApplyBatchEdit")
        .WithSummary("Apply metadata changes to several items at once; an item that cannot take them goes to Needs Review and the rest still apply.")
        .Produces<LibraryBatchEditResult>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .RequireEffectiveAdministrator();

        // ── POST /library/edit-session ──────────────────────────────────────
        group.MapPost("/edit-session", async (
            EditSessionRequest request,
            IEditSessionService editSession,
            EditSessionAccessService access,
            [FromServices] EditSessionCallerAccessor callers,
            [FromServices] IRequestAuthorityResolver authorityResolver,
            [FromServices] IProfileRepository profiles,
            [FromServices] CatalogueResourceAuthorizationService resources,
            HttpContext http,
            CancellationToken ct) =>
        {
            if (request.Changes.Count == 0)
            {
                return ApiErrors.BadRequest("There are no changes to check or save.");
            }

            if (request.Changes.Count > EditSessionRequest.MaxChanges)
            {
                return ApiErrors.BadRequest($"An edit session takes at most {EditSessionRequest.MaxChanges} changes.");
            }

            // Moving an item between albums and seasons runs the library move, which needs to know who is asking and what they may edit.
            // A session with no such change never needs it.
            EditSessionCaller? caller = null;
            if (request.Changes.Any(change => change.Kind == EditSessionChangeKinds.Relationship))
            {
                var actor = await LibraryItemEndpoints.ResolveMoveActorAsync(http, authorityResolver, profiles, ct);
                if (actor is null)
                {
                    return ApiErrors.NotFound("The editing session is unavailable.");
                }

                caller = new EditSessionCaller(actor, LibraryItemEndpoints.CanEdit(http, resources));
            }

            var refusals = await access.FindRefusalsAsync(http, request.Changes.Select(change => change.EntityId).ToList(), ct);
            using (callers.Use(caller))
            {
                return Results.Ok(await editSession.RunAsync(request, refusals, ct));
            }
        })
        .WithName("RunEditSession")
        .WithSummary("Check, or check and save, everything staged in the universal editor: all data changes together or none, file moves last.")
        .Produces<EditSessionResult>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .RequireEffectiveAdministrator();

        // ── GET /library/editor-subjects/{id} and /library/editor-subjects/universes/{qid} ─────
        // What the universal editor opens on for a person, Series, curated collection or Universe (spec D17, D17a): the Details fields
        // with their current and canonical values, and the pictures the subject can have.
        group.MapGet("/editor-subjects/{id:guid}", async (
            Guid id,
            EditSubjectDirectory subjects,
            EditSessionAccessService access,
            HttpContext http,
            CancellationToken ct) =>
        {
            var refusals = await access.FindRefusalsAsync(http, [id], ct);
            if (refusals.TryGetValue(id, out var refusal))
            {
                return ApiErrors.Forbidden(refusal);
            }

            var subject = await subjects.LoadAsync(id, ct);
            return subject is null ? ApiErrors.NotFound(EditSessionReasons.SubjectUnavailable) : Results.Ok(subject);
        })
        .WithName("GetEditorSubject")
        .WithSummary("The Details fields and picture slots the universal editor shows for a person, Series, curated collection or Universe.")
        .Produces<EditorSubjectDto>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .RequireEffectiveAdministrator();

        group.MapGet("/editor-subjects/universes/{qid}", async (
            string qid,
            ICollectionRepository collections,
            EditSubjectDirectory subjects,
            EditSessionAccessService access,
            HttpContext http,
            CancellationToken ct) =>
        {
            var normalized = UniverseClientReadService.NormalizeQid(qid);
            var universe = normalized is null ? null : await collections.FindUniverseByQidAsync(normalized, ct);
            if (universe is null)
            {
                return ApiErrors.NotFound(EditSessionReasons.SubjectUnavailable);
            }

            var refusals = await access.FindRefusalsAsync(http, [universe.Id], ct);
            if (refusals.TryGetValue(universe.Id, out var refusal))
            {
                return ApiErrors.Forbidden(refusal);
            }

            var subject = await subjects.LoadAsync(universe.Id, ct);
            return subject is null ? ApiErrors.NotFound(EditSessionReasons.SubjectUnavailable) : Results.Ok(subject);
        })
        .WithName("GetUniverseEditorSubject")
        .WithSummary("The universal editor's subject for a Universe, found by its Wikidata ID.")
        .Produces<EditorSubjectDto>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .RequireEffectiveAdministrator();

        return app;
    }
}


