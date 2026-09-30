using MediaEngine.Api.Security;
using MediaEngine.Api.Services;
using MediaEngine.Contracts.Artwork;
using MediaEngine.Domain.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace MediaEngine.Api.Endpoints;

public static partial class MetadataEndpoints
{
    private static void MapArtworkWritebackEndpoints(RouteGroupBuilder group)
    {
        group.MapGet("/assets/{assetId:guid}/artwork-writeback", async (
            Guid assetId, ArtworkWritebackService service, CancellationToken ct) =>
        {
            var status = await service.GetStatusAsync(assetId, ct);
            return status is null ? Results.NotFound() : Results.Ok(status);
        })
        .WithName("GetArtworkWritebackStatus")
        .Produces<ArtworkWritebackStatusDto>(StatusCodes.Status200OK)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.MetadataRead)
        .RequireCatalogueAssetAccess(ApplicationPermissionIds.MetadataRead);

        group.MapPost("/assets/{assetId:guid}/artwork-writeback/retry", async (
            Guid assetId, ArtworkWritebackService service, CancellationToken ct) =>
        {
            var status = await service.ProcessAsync(assetId, retry: true, ct);
            return status is null ? Results.NotFound() : Results.Ok(status);
        })
        .WithName("RetryArtworkWriteback")
        .Produces<ArtworkWritebackStatusDto>(StatusCodes.Status200OK)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.MetadataWrite)
        .RequireCatalogueAssetAccess(ApplicationPermissionIds.MetadataWrite);
    }
}
