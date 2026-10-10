using MediaEngine.Api.Http;
using MediaEngine.Api.Security;
using MediaEngine.Contracts.Metadata;
using MediaEngine.Domain.Authorization;
using MediaEngine.Storage;
using Microsoft.AspNetCore.Mvc;

namespace MediaEngine.Api.Endpoints;

public static partial class MetadataEndpoints
{
    private static void MapFileCoverageEndpoints(RouteGroupBuilder group)
    {
        group.MapGet("/{entityId:guid}/file-coverage", async (
            Guid entityId, [FromQuery] Guid assetId, HttpContext http,
            CatalogueResourceAuthorizationService resources,
            [FromServices] MediaEditorFileCoverageRepository coverage, CancellationToken ct) =>
        {
            if (assetId == Guid.Empty)
            {
                return ApiErrors.BadRequest("Choose an owned file.");
            }
            if (await resources.EvaluateAssetAsync(http, assetId, ApplicationPermissionIds.MetadataRead, ct) != CatalogueResourceAccess.Allowed)
            {
                return ApiErrors.NotFound("This file is unavailable.");
            }
            var result = await coverage.GetAsync(assetId, ct);
            return ToFileCoverageResponse(entityId, result);
        }).WithName("GetMediaEditorFileCoverage")
          .Produces<MediaEditorFileCoverageDto>(StatusCodes.Status200OK)
          .ProducesProblem(StatusCodes.Status404NotFound)
          .RequireAdministratorOrApplication(ApplicationPermissionIds.MetadataRead)
          .RequireAnyCatalogueEntityAccess(ApplicationPermissionIds.MetadataRead);

        group.MapPut("/{entityId:guid}/file-coverage", async (
            Guid entityId, MediaEditorFileCoverageSaveRequestDto request, HttpContext http,
            CatalogueResourceAuthorizationService resources,
            [FromServices] MediaEditorFileCoverageRepository coverage, CancellationToken ct) =>
        {
            // The operation id marks one deliberate save; replacing a file's list with the
            // same list is a no-op, so a retried request can never double-apply.
            if (request.OperationId == Guid.Empty || request.AssetId == Guid.Empty || request.WorkIds is null)
            {
                return ApiErrors.BadRequest("Choose an owned file and the episodes it covers.");
            }
            if (await resources.EvaluateAssetAsync(http, request.AssetId, ApplicationPermissionIds.MetadataRead, ct) != CatalogueResourceAccess.Allowed
                || await resources.EvaluateAssetAsync(http, request.AssetId, ApplicationPermissionIds.MetadataWrite, ct) != CatalogueResourceAccess.Allowed)
            {
                return ApiErrors.NotFound("This file is no longer editable.");
            }
            var current = await coverage.GetAsync(request.AssetId, ct);
            if (current.View is { } view && !RouteMatchesFile(entityId, view))
            {
                return ApiErrors.NotFound("This file is unavailable.");
            }
            var result = current.View is null ? current : await coverage.ReplaceAsync(request.AssetId, request.WorkIds, ct);
            return ToFileCoverageResponse(entityId, result);
        }).WithName("SaveMediaEditorFileCoverage")
          .Produces<MediaEditorFileCoverageDto>(StatusCodes.Status200OK)
          .ProducesProblem(StatusCodes.Status400BadRequest)
          .ProducesProblem(StatusCodes.Status409Conflict)
          .RequireAdministratorOrApplication(ApplicationPermissionIds.MetadataWrite)
          .RequireAnyCatalogueEntityAccess(ApplicationPermissionIds.MetadataWrite);
    }

    // The editor can be open on the file, its edition, its own episode or an episode it covers.
    private static bool RouteMatchesFile(Guid entityId, FileCoverageView view) =>
        entityId == view.AssetId || entityId == view.HostWorkId || entityId == view.HostEditionId
        || view.Episodes.Any(episode => episode.IsCovered && episode.WorkId == entityId);

    private static IResult ToFileCoverageResponse(Guid entityId, FileCoverageResult result)
    {
        if (result.View is not { } view || result.Outcome != FileCoverageOutcome.Ok)
        {
            return result.Outcome switch
            {
                FileCoverageOutcome.Invalid => ApiErrors.BadRequest(result.Message ?? "That list of episodes is not allowed."),
                FileCoverageOutcome.Conflict => ApiErrors.Conflict(result.Message ?? "Those episodes changed. Reload and try again."),
                _ => ApiErrors.NotFound(result.Message ?? "This file is unavailable."),
            };
        }
        if (!RouteMatchesFile(entityId, view))
        {
            return ApiErrors.NotFound("This file is unavailable.");
        }
        return Results.Ok(new MediaEditorFileCoverageDto(
            view.AssetId,
            view.HostWorkId,
            view.MaxEpisodes,
            view.Episodes.Select(episode => new MediaEditorFileCoverageEpisodeDto(
                episode.WorkId, episode.EpisodeNumber, episode.Title,
                episode.IsHost, episode.IsCovered, episode.OwnedByOtherFile)).ToArray()));
    }
}
