using MediaEngine.Api.Http;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services;
using MediaEngine.Api.Services.FileWrites;
using MediaEngine.Contracts.Artwork;
using MediaEngine.Contracts.WriteBack;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Contracts;
using MediaEngine.Storage;
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
            return status is null ? ApiErrors.NotFound("Artwork write-back status was not found.") : Results.Ok(status);
        })
        .WithName("GetArtworkWritebackStatus")
        .Produces<ArtworkWritebackStatusDto>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.MetadataRead)
        .RequireCatalogueAssetAccess(ApplicationPermissionIds.MetadataRead);

        // What is waiting to be written into this file (the File section of Editing Mode).
        group.MapGet("/assets/{assetId:guid}/file-write", async (
            Guid assetId, MediaFileWriteIntentRepository intents, FileWriteScheduler scheduler, CancellationToken ct) =>
        {
            var state = await intents.GetAssetStateAsync(assetId, ct);
            return Results.Ok(new AssetFileWriteStatusDto
            {
                Waiting = state.Waiting,
                NextRunAt = state.Waiting ? await scheduler.GetNextRunAtAsync(ct) : null,
                LastError = state.LastError,
                LastWrittenAt = DateTimeOffset.TryParse(state.LastWrittenAt, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var written) ? written : null,
            });
        })
        .WithName("GetAssetFileWriteStatus")
        .WithSummary("Whether changes are waiting to be written into this file, when the next run is, and why the last attempt failed.")
        .Produces<AssetFileWriteStatusDto>(StatusCodes.Status200OK)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.MetadataRead)
        .RequireCatalogueAssetAccess(ApplicationPermissionIds.MetadataRead);

        group.MapPost("/assets/{assetId:guid}/artwork-writeback/retry", async (
            Guid assetId, ArtworkWritebackService service, IFileWriteQueue fileWrites, CancellationToken ct) =>
        {
            // The retry joins the file write list and is carried out by the next write run (or Write now).
            await fileWrites.EnqueueAssetAsync(assetId, MediaFileWriteTriggers.ArtworkRetry, ct);
            var status = await service.GetStatusAsync(assetId, ct);
            return status is null ? ApiErrors.NotFound("Artwork write-back status was not found.") : Results.Ok(status);
        })
        .WithName("RetryArtworkWriteback")
        .Produces<ArtworkWritebackStatusDto>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.MetadataWrite)
        .RequireCatalogueAssetAccess(ApplicationPermissionIds.MetadataWrite);
    }
}
