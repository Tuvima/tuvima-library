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

        return app;
    }
}
