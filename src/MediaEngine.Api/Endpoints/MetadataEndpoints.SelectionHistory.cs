using MediaEngine.Api.Http;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services.ReadServices;
using MediaEngine.Contracts.Metadata;
using MediaEngine.Domain.Authorization;

namespace MediaEngine.Api.Endpoints;

public static partial class MetadataEndpoints
{
    private static void MapMediaEditorSelectionHistoryEndpoints(RouteGroupBuilder group)
    {
        group.MapPost("/{entityId:guid}/owned-children/history", async (
            Guid entityId,
            MediaEditorSelectionHistoryRequestDto request,
            HttpContext http,
            CatalogueResourceAuthorizationService resources,
            MediaEditorSelectionHistoryReadService history,
            ILogger<MediaEditorSelectionHistoryReadService> logger,
            CancellationToken ct) =>
        {
            if (request.AssetIds is null || request.AssetIds.Count is < 1 or > 1000
                || request.AssetIds.Any(id => id == Guid.Empty)
                || request.AssetIds.Distinct().Count() != request.AssetIds.Count)
                return ApiErrors.BadRequest("Choose 1 to 1,000 distinct owned files for History.");

            foreach (var id in request.AssetIds)
                if (await resources.EvaluateAssetAsync(http, id, ApplicationPermissionIds.MetadataRead, ct)
                    != CatalogueResourceAccess.Allowed
                    || await resources.EvaluateAssetAsync(http, id, ApplicationPermissionIds.ReviewRead, ct)
                    != CatalogueResourceAccess.Allowed)
                    return ApiErrors.NotFound("One or more selected files are unavailable.");

            MediaEditorSelectionHistoryResult? result;
            try
            {
                result = await history.ReadAsync(entityId, request.AssetIds, ct);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                logger.LogError(error, "Selection History read failed for editor parent {EntityId}", entityId);
                return ApiErrors.Problem(StatusCodes.Status503ServiceUnavailable,
                    "History unavailable.", "History could not be loaded. Try again.");
            }
            if (result is null)
                return ApiErrors.NotFound("The selected files are not owned children of this editor parent.");

            foreach (var owner in result.Items
                         .Where(item => item.Scope is "edition" or "work" or "parent")
                         .Select(item => (item.EntityId,
                             EntityType: item.Scope == "edition" ? "Edition" : "Work"))
                         .Distinct())
                if (await resources.EvaluateEntityAsync(http, owner.EntityType, owner.EntityId,
                        ApplicationPermissionIds.ReviewRead, ct) != CatalogueResourceAccess.Allowed)
                    return ApiErrors.NotFound("A related History owner is unavailable.");

            // The read can span multiple library segments. Access is rechecked
            // after its snapshot before any event detail is returned.
            foreach (var id in request.AssetIds)
                if (await resources.EvaluateAssetAsync(http, id, ApplicationPermissionIds.MetadataRead, ct)
                    != CatalogueResourceAccess.Allowed
                    || await resources.EvaluateAssetAsync(http, id, ApplicationPermissionIds.ReviewRead, ct)
                    != CatalogueResourceAccess.Allowed)
                    return ApiErrors.NotFound("Access to a selected file changed while loading History.");

            return Results.Ok(new MediaEditorSelectionHistoryDto(result.ParentEntityId,
                result.SelectedAssetIds, result.Items.Select(item =>
                    new MediaEditorSelectionHistoryEntryDto(item.Id, item.EntityId,
                        item.OccurredAt, item.EventType, item.Label, item.Detail,
                        item.Category, item.ActorLabel, item.Scope, item.SelectedAssetIds)).ToArray()));
        })
        .WithName("GetMediaEditorSelectionHistory")
        .WithSummary("Read file and relevant parent events for up to 1,000 checked owned files.")
        .Produces<MediaEditorSelectionHistoryDto>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.ReviewRead)
        .RequireAnyCatalogueEntityAccess(ApplicationPermissionIds.ReviewRead);
    }
}
