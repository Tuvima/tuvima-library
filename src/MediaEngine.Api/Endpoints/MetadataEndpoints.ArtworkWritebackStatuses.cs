using MediaEngine.Api.Security;
using MediaEngine.Api.Services;
using MediaEngine.Contracts.Artwork;
using MediaEngine.Domain.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace MediaEngine.Api.Endpoints;

public static partial class MetadataEndpoints
{
    private static void MapArtworkWritebackStatusesEndpoint(RouteGroupBuilder group)
    {
        group.MapPost("/artwork-writeback/statuses", async (
            ArtworkWritebackStatusesRequestDto request,
            HttpContext http,
            CatalogueResourceAuthorizationService authorization,
            ArtworkWritebackService writeback,
            CancellationToken ct) =>
        {
            if (request.MediaAssetIds is null || request.MediaAssetIds.Count is < 1 or > 100
                || request.MediaAssetIds.Any(id => id == Guid.Empty))
                return Results.BadRequest("Provide between 1 and 100 media asset IDs.");

            var ids = request.MediaAssetIds.Distinct().ToArray();
            var statuses = new List<ArtworkWritebackStatusDto>(ids.Length);
            foreach (var id in ids)
            {
                var access = await authorization.EvaluateAssetAsync(http, id, ApplicationPermissionIds.MetadataRead, ct);
                if (access != CatalogueResourceAccess.Allowed)
                    return Results.NotFound();
                var status = await writeback.GetStatusAsync(id, ct);
                if (status is null) return Results.NotFound();
                statuses.Add(status);
            }
            return Results.Ok(statuses);
        })
        .WithName("GetArtworkWritebackStatuses")
        .WithSummary("Returns truthful artwork file outcomes for a bounded authorized page of owned media assets.")
        .Produces<IReadOnlyList<ArtworkWritebackStatusDto>>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.MetadataRead);
    }
}
