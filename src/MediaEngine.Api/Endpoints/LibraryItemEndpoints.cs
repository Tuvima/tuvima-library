using MediaEngine.Api.Http;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services.Libraries;
using MediaEngine.Contracts.Items;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;
using MediaEngine.Storage;
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

        // The recycle bin of the library types that have it switched on. Same audience as deleting: effective administrators at
        // the Dashboard. Restoring never overwrites; emptying is the only step that removes files from the disk.
        group.MapGet("/recycle-bin", ([FromServices] RecycleBinService bin) => Results.Ok(bin.List().Select(entry => new RecycleBinItemDto
        {
            Id = entry.Id,
            FileName = entry.FileName,
            Title = entry.Title,
            SizeBytes = entry.SizeBytes,
            DeletedAt = entry.DeletedAt,
        }).ToList()))
        .WithName("ListRecycleBin")
        .WithSummary("The files waiting in the recycle bin.")
        .Produces<IReadOnlyList<RecycleBinItemDto>>(StatusCodes.Status200OK)
        .RequireEffectiveAdministratorInPerson();

        group.MapPost("/recycle-bin/{id:guid}/restore", (Guid id, [FromServices] RecycleBinService bin) =>
            bin.Restore(id) switch
            {
                RecycleBinRestoreResult.Restored => Results.NoContent(),
                RecycleBinRestoreResult.NotFound => ApiErrors.NotFound("That file is no longer in the recycle bin."),
                RecycleBinRestoreResult.PlaceTaken => ApiErrors.Conflict("Something is already at that place, so nothing was put back."),
                _ => ApiErrors.BadRequest("The file could not be put back. It is still in the recycle bin."),
            })
        .WithName("RestoreFromRecycleBin")
        .WithSummary("Put a file from the recycle bin back where it was. Nothing is overwritten.")
        .Produces(StatusCodes.Status204NoContent)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status409Conflict)
        .RequireEffectiveAdministratorInPerson();

        group.MapPost("/recycle-bin/empty", ([FromServices] RecycleBinService bin) => Results.Ok(new RecycleBinEmptiedDto { Removed = bin.Empty() }))
        .WithName("EmptyRecycleBin")
        .WithSummary("Remove every file in the recycle bin from the disk for good.")
        .Produces<RecycleBinEmptiedDto>(StatusCodes.Status200OK)
        .RequireEffectiveAdministratorInPerson();

        // Match first, then move (details overlay spec D12 to D17): where a title can go, what moving it would do, and the confirmed
        // move. The preview changes nothing; the move acts only on the plan the person confirmed by its fingerprint.
        group.MapPost("/{entityId:guid}/move-destinations", async (
            Guid entityId, ItemMoveDestinationsRequest request, HttpContext http,
            [FromServices] CatalogueResourceAuthorizationService resources, [FromServices] ItemMoveService moves, CancellationToken ct) =>
        {
            if (request.WorkId != entityId || !await CanMoveAsync(http, resources, entityId, ct))
            {
                return ApiErrors.NotFound("This title is unavailable for moving.");
            }

            var result = await moves.FindDestinationsAsync(entityId, request.Query, ct);
            return result.IsSuccess ? Results.Ok(result.Value) : ApiErrors.BadRequest(result.Refusal!.Message);
        })
        .WithName("FindItemMoveDestinations")
        .WithSummary("The places a title can move to: only places that accept this kind of title.")
        .Produces<ItemMoveDestinationsResponse>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.MetadataMatch)
        .RequireAnyCatalogueEntityAccess(ApplicationPermissionIds.MetadataMatch);

        group.MapPost("/{entityId:guid}/move-preview", async (
            Guid entityId, ItemMovePreviewRequest request, HttpContext http,
            [FromServices] CatalogueResourceAuthorizationService resources, [FromServices] ItemMoveService moves, CancellationToken ct) =>
        {
            if (request.WorkId != entityId || !await CanMoveAsync(http, resources, entityId, ct))
            {
                return ApiErrors.NotFound("This title is unavailable for moving.");
            }

            var result = await moves.PreviewAsync(request, ct);
            return result.IsSuccess ? Results.Ok(result.Value) : ApiErrors.BadRequest(result.Refusal!.Message);
        })
        .WithName("PreviewItemMove")
        .WithSummary("Preview moving a title: the destination's own list, the Engine's best guess, the plain sentence to confirm and a fingerprint of the plan.")
        .Produces<ItemMovePreview>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.MetadataMatch)
        .RequireAnyCatalogueEntityAccess(ApplicationPermissionIds.MetadataMatch);

        group.MapPost("/{entityId:guid}/move", async (
            Guid entityId, ItemMoveRequest request, HttpContext http,
            [FromServices] CatalogueResourceAuthorizationService resources, [FromServices] ItemMoveService moves, CancellationToken ct) =>
        {
            if (request.WorkId != entityId || !await CanMoveAsync(http, resources, entityId, ct))
            {
                return ApiErrors.NotFound("This title is unavailable for moving.");
            }

            var result = await moves.MoveAsync(request, ct);
            if (result.IsSuccess)
            {
                return Results.Ok(result.Value);
            }

            var refusal = result.Refusal!;
            return refusal.Kind switch
            {
                ItemMoveRefusalKind.Changed => Results.Json(
                    new ItemMoveConflictDto { Message = refusal.Message, Preview = refusal.FreshPreview },
                    statusCode: StatusCodes.Status409Conflict),
                _ => ApiErrors.BadRequest(refusal.Message),
            };
        })
        .WithName("MoveItem")
        .WithSummary("Move a title to another place in the same kind of list. Confirms the plan by its fingerprint: a changed plan answers 409 with a fresh preview and nothing moves.")
        .Produces<ItemMoveResultDto>(StatusCodes.Status200OK)
        .Produces<ItemMoveConflictDto>(StatusCodes.Status409Conflict)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.MetadataMatch)
        .RequireAnyCatalogueEntityAccess(ApplicationPermissionIds.MetadataMatch);

        return app;
    }

    // Moving changes where a title sits, so it needs both the match and write permissions on the title (like the music track move).
    private static async Task<bool> CanMoveAsync(HttpContext http, CatalogueResourceAuthorizationService resources, Guid workId, CancellationToken ct) =>
        await resources.EvaluateEntityAsync(http, "Work", workId, ApplicationPermissionIds.MetadataMatch, ct) == CatalogueResourceAccess.Allowed
        && await resources.EvaluateEntityAsync(http, "Work", workId, ApplicationPermissionIds.MetadataWrite, ct) == CatalogueResourceAccess.Allowed;
}
