using MediaEngine.Api.Http;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services.Metadata;
using MediaEngine.Contracts.Artwork;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Api.Endpoints;

public static partial class MetadataEndpoints
{
    /// <summary>
    /// The editor's "Frames from the video" source (review mock-ups 2026-11-18, D2): the frames of the item's video, one frame's picture,
    /// and saving a chosen frame as a picture of the item (the editor then chooses it through the edit session, like an upload).
    /// </summary>
    private static void MapVideoFrameEndpoints(RouteGroupBuilder group)
    {
        group.MapGet("/{entityId:guid}/artwork/{scopeId}/video-frames", async (
            Guid entityId, string scopeId, HttpContext http, [Microsoft.AspNetCore.Mvc.FromServices] VideoFrameArtworkService frames, CancellationToken ct) =>
        {
            var scope = await ResolveVideoFrameScopeAsync(http, entityId, scopeId, ApplicationPermissionIds.MetadataWrite);
            if (scope is null)
            {
                return ApiErrors.NotFound("Artwork scope not found.");
            }

            var owner = scope.ArtworkOwnerEntityId!.Value;
            return Results.Ok(await frames.ListAsync(
                entityId, scope.ScopeId, scope.RepresentativeMediaFilePath, scope.ArtworkOwnerEntityKind!, owner,
                scope.DisplayTitle, scope.MediaType, ct));
        }).WithName("GetScopedVideoFrames")
          .WithSummary("The frames taken from the item's video, for the Artwork tab's strip. A video with no cover picture also gets its best frame as its default cover.")
          .Produces<VideoFramesDto>(StatusCodes.Status200OK)
          .ProducesProblem(StatusCodes.Status404NotFound)
          .RequireAdministratorOrApplication(ApplicationPermissionIds.MetadataWrite)
          .RequireAnyCatalogueEntityAccess(ApplicationPermissionIds.MetadataWrite);

        group.MapGet("/{entityId:guid}/video-frames/{scopeId}/{index:int}", async (
            Guid entityId, string scopeId, int index, HttpContext http, [Microsoft.AspNetCore.Mvc.FromServices] VideoFrameArtworkService frames, CancellationToken ct) =>
        {
            var scope = await ResolveVideoFrameScopeAsync(http, entityId, scopeId, ApplicationPermissionIds.MetadataRead);
            var bytes = scope is null ? null : await frames.ImageAsync(scope.RepresentativeMediaFilePath, index, ct);
            if (bytes is null)
            {
                return ApiErrors.NotFound("That frame is no longer available.");
            }

            http.Response.Headers.CacheControl = "private, max-age=900";
            return Results.File(bytes, "image/jpeg");
        }).WithName("GetScopedVideoFrameImage")
          .WithSummary("The picture of one frame taken from the item's video.")
          .Produces(StatusCodes.Status200OK, contentType: "image/jpeg")
          .ProducesProblem(StatusCodes.Status404NotFound)
          .RequireAdministratorOrApplication(ApplicationPermissionIds.MetadataRead)
          .RequireAnyCatalogueEntityAccess(ApplicationPermissionIds.MetadataRead);

        group.MapPost("/{entityId:guid}/artwork/{scopeId}/video-frames/{index:int}/{slot}", async (
            Guid entityId, string scopeId, int index, string slot, HttpContext http, [Microsoft.AspNetCore.Mvc.FromServices] VideoFrameArtworkService frames, CancellationToken ct) =>
        {
            var scope = await ResolveVideoFrameScopeAsync(http, entityId, scopeId, ApplicationPermissionIds.MetadataWrite);
            if (scope is null)
            {
                return ApiErrors.NotFound("Artwork scope not found.");
            }

            if (!VideoFrameArtworkService.AcceptsFrames(slot)
                || !ArtworkScopeService.GetScopedArtworkSlots(scope.MediaType, scope.ScopeId).Contains(slot, StringComparer.OrdinalIgnoreCase))
            {
                return ApiErrors.BadRequest("A frame from the video cannot be used for that kind of picture.");
            }

            var saved = await frames.ImportAsync(
                scope.RepresentativeMediaFilePath, index, slot, scope.ArtworkOwnerEntityKind!, scope.ArtworkOwnerEntityId!.Value,
                scope.DisplayTitle, scope.MediaType, ct);
            return saved is null ? ApiErrors.NotFound("That frame is no longer available.") : Results.Ok(saved);
        }).WithName("UseScopedVideoFrame")
          .WithSummary("Saves one frame of the item's video as a picture of the item for a slot. The editor then chooses it through the edit session.")
          .Produces<ArtworkAssetDto>(StatusCodes.Status200OK)
          .ProducesProblem(StatusCodes.Status400BadRequest)
          .ProducesProblem(StatusCodes.Status404NotFound)
          .RequireAdministratorOrApplication(ApplicationPermissionIds.MetadataWrite)
          .RequireAnyCatalogueEntityAccess(ApplicationPermissionIds.MetadataWrite);
    }

    // The scope of an editor read, only when its pictures can be edited and the caller may use it. A scope without a video file answers like
    // one that is not there.
    private static async Task<EditorScopeResolution?> ResolveVideoFrameScopeAsync(
        HttpContext http, Guid entityId, string scopeId, ApplicationPermissionId permission)
    {
        var services = http.RequestServices;
        var context = await ResolveEditorScopeContextAsync(entityId,
            services.GetRequiredService<ICanonicalValueRepository>(), services.GetRequiredService<ILibraryItemRepository>(),
            services.GetRequiredService<IMetadataEditorRepository>(), http.RequestAborted);
        var scope = context?.Scopes.FirstOrDefault(candidate => string.Equals(candidate.ScopeId, scopeId, StringComparison.OrdinalIgnoreCase));
        return scope is not null
            && scope.CanEditArtwork
            && scope.ArtworkOwnerEntityId is not null
            && !string.IsNullOrWhiteSpace(scope.ArtworkOwnerEntityKind)
            && await HasEditorScopeAccessAsync(http, services.GetRequiredService<CatalogueResourceAuthorizationService>(), scope, permission, http.RequestAborted)
                ? scope
                : null;
    }
}
