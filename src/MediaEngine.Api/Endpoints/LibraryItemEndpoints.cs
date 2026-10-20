using MediaEngine.Api.Http;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services.Libraries;
using MediaEngine.Api.Services.Libraries.Moves;
using MediaEngine.Api.Services.Matching;
using MediaEngine.Contracts.Items;
using MediaEngine.Contracts.Library;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace MediaEngine.Api.Endpoints;

/// <summary>
/// Unified curator-facing view of ingested media items. Endpoint handlers own HTTP
/// mapping while persistence is delegated to typed repositories and curation services.
/// </summary>
public static class LibraryItemEndpoints
{
    public static IEndpointRouteBuilder MapLibraryItemEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/library/items")
            .WithTags("LibraryItems");

        group.MapGet("/{entityId}/detail", async (
            Guid entityId,
            ILibraryItemRepository repo,
            CancellationToken ct) =>
        {
            var detail = await repo.GetDetailAsync(entityId, ct);
            return detail is null
                ? ApiErrors.NotFound($"Library item '{entityId}' not found.")
                : Results.Ok(detail.ToContract());
        })
        .WithName("GetLibraryItemDetail")
        .WithSummary("Full detail for a single library item.")
        .Produces<LibraryItemDetailDto>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.ReviewRead)
        .RequireAnyCatalogueEntityAccess(ApplicationPermissionIds.ReviewRead);

        group.MapGet("/{entityId:guid}/history", async (
            Guid entityId,
            ILibraryItemCurationRepository store,
            CancellationToken ct) =>
            Results.Ok((await store.GetHistoryAsync(entityId, ct)).Select(item => item.ToContract()).ToList()))
        .WithName("GetLibraryCatalogItemHistory")
        .WithSummary("Get processing history timeline for a library item.")
        .Produces<IReadOnlyList<LibraryItemHistoryDto>>(StatusCodes.Status200OK)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.ReviewRead)
        .RequireAnyCatalogueEntityAccess(ApplicationPermissionIds.ReviewRead);

        // Delete from library: the first half. Works out exactly what would happen and changes nothing. Effective
        // administrators signed in at the Dashboard only (surface PIN unlock included); apps and native clients are refused,
        // even one acting for an administrator.
        group.MapPost("/deletion-preview", async (
            LibraryItemDeletionPreviewRequest request,
            LibraryItemDeletionService deletion,
            CancellationToken ct) =>
        {
            var result = await deletion.PreviewAsync(request.WorkIds ?? [], ct);
            if (result.IsSuccess)
            {
                return Results.Ok(result.Value);
            }

            var refusal = result.Refusal!;
            return refusal.Kind == LibraryItemDeletionRefusalKind.NotFound
                ? ApiErrors.NotFound($"{refusal.Message} Unknown: {string.Join(", ", refusal.WorkIds.Select(id => id.ToString("D")))}.")
                : ApiErrors.BadRequest(refusal.Message);
        })
        .WithName("PreviewLibraryItemDeletion")
        .WithSummary("Preview deleting titles from the library: every file and what happens to it, the hand-picked collections affected, and a fingerprint of the plan.")
        .Produces<LibraryItemDeletionPreview>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .RequireEffectiveAdministratorInPerson();

        // Delete from library: the second half. Acts on the plan the person confirmed (by its fingerprint). Same audience as the
        // preview. Files are removed only through the source-file safety gate; a changed plan is refused with a fresh preview and
        // nothing is touched.
        group.MapPost("/delete", async (
            LibraryItemDeletionRequest request,
            HttpContext http,
            IRequestAuthorityResolver resolver,
            IProfileRepository profiles,
            LibraryItemDeletionService deletion,
            CancellationToken ct) =>
        {
            var authority = await resolver.ResolveAsync(http, ct);
            var profile = authority.ActiveProfileId is { } profileId
                ? await profiles.GetByIdAsync(profileId, ct)
                : null;
            var actor = new LibraryItemDeletionActor(
                authority.AccountId,
                authority.ActiveProfileId,
                string.IsNullOrWhiteSpace(profile?.DisplayName) ? "An administrator" : profile.DisplayName);

            var result = await deletion.DeleteAsync(request.WorkIds ?? [], request.Fingerprint, actor, ct);
            if (result.IsSuccess)
            {
                return Results.Ok(result.Value);
            }

            var refusal = result.Refusal!;
            return refusal.Kind switch
            {
                LibraryItemDeletionRefusalKind.NotFound => ApiErrors.NotFound("This item is no longer in your library."),
                // A 409 that carries the fresh preview so the dialog can show it. It is a typed body, not a bare error, so it does not use the problem helper.
                LibraryItemDeletionRefusalKind.Changed => Results.Json(
                    new LibraryItemDeletionConflictDto { Message = refusal.Message, Preview = refusal.FreshPreview! },
                    statusCode: StatusCodes.Status409Conflict),
                _ => ApiErrors.BadRequest(refusal.Message),
            };
        })
        .WithName("DeleteLibraryItems")
        .WithSummary("Delete titles from the library and, where the safety gate allows, their files from disk. Confirms the plan by its fingerprint: a changed plan answers 409 with a fresh preview and nothing is deleted.")
        .Produces<LibraryItemDeletionResultDto>(StatusCodes.Status200OK)
        .Produces<LibraryItemDeletionConflictDto>(StatusCodes.Status409Conflict)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .RequireEffectiveAdministratorInPerson();

        MapMoveEndpoints(group);

        return app;
    }

    // Moving items into another album, season, series, audiobook or hand-picked collection through the Match step. These change
    // library structure, so they are authorised exactly like the other structural edit actions (membership apply, music track
    // move): an administrator, or an application with the metadata-write permission. Each item and the destination are also
    // checked against the caller's resource scope.
    private static void MapMoveEndpoints(RouteGroupBuilder group)
    {
        group.MapGet("/{id:guid}/move-destinations", async (
            Guid id,
            string? query,
            HttpContext http,
            [FromServices] ILibraryMoveService moves,
            [FromServices] CatalogueResourceAuthorizationService resources,
            CancellationToken ct) =>
        {
            var result = await moves.GetDestinationsAsync(id, query, CanEdit(http, resources), ct);
            return result.IsSuccess ? Results.Ok(result.Value) : MoveRefusal(result.Refusal!);
        })
        .WithName("GetLibraryItemMoveDestinations")
        .WithSummary("The places this item can be moved into. Only places that accept the item's kind are offered.")
        .Produces<LibraryMoveDestinationsResponse>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.MetadataWrite);

        group.MapPost("/move-preview", async (
            LibraryMovePreviewRequest request,
            HttpContext http,
            IRequestAuthorityResolver resolver,
            IProfileRepository profiles,
            [FromServices] ILibraryMoveService moves,
            [FromServices] CatalogueResourceAuthorizationService resources,
            CancellationToken ct) =>
        {
            var actor = await ResolveMoveActorAsync(http, resolver, profiles, ct);
            if (actor is null)
            {
                return ApiErrors.NotFound("The editing session is unavailable.");
            }

            var result = await moves.PreviewAsync(request, actor, CanEdit(http, resources), ct);
            return result.IsSuccess ? Results.Ok(result.Value) : MoveRefusal(result.Refusal!);
        })
        .WithName("PreviewLibraryItemMove")
        .WithSummary("Preview moving items into another album, season, series, audiobook or collection: the destination's own list, the Engine's best guess for each item, and what happens.")
        .Produces<LibraryMovePreview>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.MetadataWrite);

        group.MapPost("/move", async (
            LibraryMoveCommitRequest request,
            HttpContext http,
            IRequestAuthorityResolver resolver,
            IProfileRepository profiles,
            [FromServices] ILibraryMoveService moves,
            [FromServices] CatalogueResourceAuthorizationService resources,
            CancellationToken ct) =>
        {
            var actor = await ResolveMoveActorAsync(http, resolver, profiles, ct);
            if (actor is null)
            {
                return ApiErrors.NotFound("The editing session is unavailable.");
            }

            var result = await moves.CommitAsync(request, actor, CanEdit(http, resources), ct);
            return result.IsSuccess ? Results.Ok(result.Value) : MoveRefusal(result.Refusal!);
        })
        .WithName("MoveLibraryItems")
        .WithSummary("Move items as the person chose in the Match step. Confirms the preview by its token: if anything changed since, answers 409 with a fresh preview and moves nothing.")
        .Produces<LibraryMoveCommitResult>(StatusCodes.Status200OK)
        .Produces<LibraryMoveConflictDto>(StatusCodes.Status409Conflict)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.MetadataWrite);
    }

    // Writing needs the metadata-write permission on the item or place; matching an entry or adding a version also needs metadata-match,
    // as the music track move does (both are checked, on the same resource).
    private static LibraryMoveAccess CanEdit(HttpContext http, CatalogueResourceAuthorizationService resources) =>
        async (id, right, ct) =>
            await resources.EvaluateAnyEntityAsync(http, id, ApplicationPermissionIds.MetadataWrite, ct) == CatalogueResourceAccess.Allowed
            && (right != LibraryMoveRight.Match
                || await resources.EvaluateAnyEntityAsync(http, id, ApplicationPermissionIds.MetadataMatch, ct) == CatalogueResourceAccess.Allowed);

    private static async Task<LibraryMoveActor?> ResolveMoveActorAsync(
        HttpContext http,
        IRequestAuthorityResolver resolver,
        IProfileRepository profiles,
        CancellationToken ct)
    {
        var authority = await resolver.ResolveAsync(http, ct);
        if (!TvPairingReviewTokenService.TryBindActor(http, authority, out var credential))
        {
            return null;
        }

        var profile = authority.ActiveProfileId is { } profileId ? await profiles.GetByIdAsync(profileId, ct) : null;
        return new LibraryMoveActor(
            authority,
            credential,
            string.IsNullOrWhiteSpace(profile?.DisplayName) ? "An administrator" : profile.DisplayName);
    }

    private static IResult MoveRefusal(LibraryMoveRefusal refusal) => refusal.Kind switch
    {
        LibraryMoveRefusalKind.NotFound => ApiErrors.NotFound(refusal.Message),
        LibraryMoveRefusalKind.Unavailable => ApiErrors.NotFound(refusal.Message),
        LibraryMoveRefusalKind.Invalid => ApiErrors.BadRequest(refusal.Message),
        // A 409 that carries the fresh preview so the Match step can show it. A typed body, not a bare error.
        LibraryMoveRefusalKind.Changed => Results.Json(
            new LibraryMoveConflictDto { Message = refusal.Message, Preview = refusal.FreshPreview! },
            statusCode: StatusCodes.Status409Conflict),
        _ => ApiErrors.Conflict(refusal.Message),
    };
}
