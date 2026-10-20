using MediaEngine.Api.Http;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services.Libraries;
using MediaEngine.Contracts.Items;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;

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

        return app;
    }
}
