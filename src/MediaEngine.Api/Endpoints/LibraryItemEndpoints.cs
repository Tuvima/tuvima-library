using MediaEngine.Api.Http;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services.FileWrites;
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
            MediaEngine.Api.Services.Unmatched.ItemLookupStateService lookupStates,
            CancellationToken ct) =>
        {
            var detail = await repo.GetDetailAsync(entityId, ct);
            return detail is null
                ? ApiErrors.NotFound($"Library item '{entityId}' not found.")
                : Results.Ok(detail.ToContract(await lookupStates.GetAsync(entityId, ct)));
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
        group.MapGet("/recycle-bin", async (HttpContext http, [FromServices] IRequestAuthorityResolver authorities, [FromServices] VaultAccessResolver vaultAccess,
            [FromServices] RecycleBinService bin, CancellationToken ct) => Results.Ok(bin.List(await HiddenLibrariesAsync(http, authorities, vaultAccess, ct)).Select(entry => new RecycleBinItemDto
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

        group.MapPost("/recycle-bin/{id:guid}/restore", async (Guid id, HttpContext http, IRequestAuthorityResolver authorities,
            [FromServices] VaultAccessResolver vaultAccess, [FromServices] RecycleBinService bin,
            [FromServices] RestoredFileWriteQueuer restoredWrites, CancellationToken ct) =>
        {
            var hidden = await HiddenLibrariesAsync(http, authorities, vaultAccess, ct);
            var originalPath = bin.List(hidden).FirstOrDefault(entry => entry.Id == id)?.OriginalPath;
            var result = bin.Restore(id, hidden);
            if (result == RecycleBinRestoreResult.Restored && originalPath is not null)
            {
                // A restored file whose item is still in the library gets a fresh entry on the file write list.
                await restoredWrites.QueueAsync(originalPath, ct);
            }

            return result switch
            {
                RecycleBinRestoreResult.Restored => Results.NoContent(),
                RecycleBinRestoreResult.NotFound => ApiErrors.NotFound("That file is no longer in the recycle bin."),
                RecycleBinRestoreResult.PlaceTaken => ApiErrors.Conflict("Something is already at that place, so nothing was put back."),
                _ => ApiErrors.BadRequest("The file could not be put back. It is still in the recycle bin."),
            };
        })
        .WithName("RestoreFromRecycleBin")
        .WithSummary("Put a file from the recycle bin back where it was. Nothing is overwritten.")
        .Produces(StatusCodes.Status204NoContent)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status409Conflict)
        .RequireEffectiveAdministratorInPerson();

        group.MapPost("/recycle-bin/empty", async (HttpContext http, [FromServices] IRequestAuthorityResolver authorities, [FromServices] VaultAccessResolver vaultAccess,
            [FromServices] RecycleBinService bin, CancellationToken ct) =>
            Results.Ok(new RecycleBinEmptiedDto { Removed = bin.Empty(await HiddenLibrariesAsync(http, authorities, vaultAccess, ct)) }))
        .WithName("EmptyRecycleBin")
        .WithSummary("Remove every file in the recycle bin from the disk for good.")
        .Produces<RecycleBinEmptiedDto>(StatusCodes.Status200OK)
        .RequireEffectiveAdministratorInPerson();

        MapMoveEndpoints(group);

        return app;
    }

    // The Vault's entries in the recycle bin are visible only inside Vault mode (Videos spec D34). Anyone else, administrators included,
    // sees the bin as if those files were not there, and cannot restore or empty them.
    internal static async Task<IReadOnlySet<string>> HiddenLibrariesAsync(
        HttpContext http, IRequestAuthorityResolver authorities, VaultAccessResolver vaultAccess, CancellationToken ct)
    {
        var authority = await authorities.ResolveAsync(http, ct);
        return (await vaultAccess.ResolveAsync(authority, ct)).HiddenLibraryIds;
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
        .RequireAdministratorOrApplication(ApplicationPermissionIds.MetadataWrite)
        .RequireAnyCatalogueEntityAccess(ApplicationPermissionIds.MetadataWrite, "id");

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
