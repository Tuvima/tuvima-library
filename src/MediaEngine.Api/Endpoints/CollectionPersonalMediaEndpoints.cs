using MediaEngine.Api.Http;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services.Collections;
using MediaEngine.Api.Services.View;
using MediaEngine.Contracts.Collections;
using MediaEngine.Domain.Authorization;

namespace MediaEngine.Api.Endpoints;

public static class CollectionPersonalMediaEndpoints
{
    public static RouteGroupBuilder MapCollectionPersonalMediaEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/personal-media/galleries", async (
            IViewRequestProfileContext profileContext,
            CollectionPersonalMediaService service,
            CancellationToken ct) =>
        {
            var authority = await profileContext.ResolveAuthorityAsync(ct);
            if (authority.ActiveProfileId is null)
            {
                return MissingTrustedProfile();
            }

            return Results.Ok(await service.ListEligibleGalleriesAsync(authority, ct));
        })
        .WithName("GetCollectionPersonalMediaGalleryReferences")
        .WithSummary("Lists Gallery references the trusted administrator may attach to a Custom Collection.")
        .Produces<IReadOnlyList<CollectionGalleryReferenceDto>>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.CollectionsWrite);

        group.MapGet("/{id:guid}/personal-media", async (
            Guid id,
            IViewRequestProfileContext profileContext,
            CollectionPersonalMediaService service,
            CancellationToken ct) =>
        {
            var authority = await profileContext.ResolveAuthorityAsync(ct);
            if (authority.ActiveProfileId is null)
            {
                return MissingTrustedProfile();
            }

            var result = await service.ListForViewerAsync(id, authority, ct);
            if (!result.Found)
            {
                return ApiErrors.NotFound($"Collection '{id}' not found.");
            }

            if (!result.Allowed)
            {
                return ApiErrors.Forbidden("The active profile cannot view this Collection.");
            }

            return Results.Ok(result.Sources);
        })
        .WithName("GetCollectionPersonalMediaSources")
        .WithSummary("Returns count-free personal-media source projections authorized for the current viewer.")
        .Produces<IReadOnlyList<CollectionPersonalMediaSourceDto>>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .RequireClientScope(ApplicationPermissionIds.CollectionsRead.Value)
        .RequireCatalogueEntityAccess(ApplicationPermissionIds.CollectionsRead, "Collection", "id");

        group.MapPost("/{id:guid}/personal-media", async (
            Guid id,
            CollectionPersonalMediaSourceWriteRequest body,
            IViewRequestProfileContext profileContext,
            CollectionPersonalMediaService service,
            CancellationToken ct) =>
        {
            var authority = await profileContext.ResolveAuthorityAsync(ct);
            if (authority.ActiveProfileId is null)
            {
                return MissingTrustedProfile();
            }

            return ToWriteResult(id, await service.AddAsync(id, authority, body, ct));
        })
        .WithName("AddCollectionPersonalMediaSource")
        .WithSummary("Adds one whole Gallery reference or one versioned smart View rule to a Custom Collection.")
        .Produces<CollectionPersonalMediaSourceDto>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.CollectionsWrite)
        .RequireCatalogueEntityAccess(ApplicationPermissionIds.CollectionsWrite, "Collection", "id");

        group.MapPut("/{id:guid}/personal-media/{sourceId:guid}", async (
            Guid id,
            Guid sourceId,
            CollectionPersonalMediaSourceWriteRequest body,
            IViewRequestProfileContext profileContext,
            CollectionPersonalMediaService service,
            CancellationToken ct) =>
        {
            var authority = await profileContext.ResolveAuthorityAsync(ct);
            if (authority.ActiveProfileId is null)
            {
                return MissingTrustedProfile();
            }

            return ToWriteResult(id, await service.UpdateAsync(id, sourceId, authority, body, ct));
        })
        .WithName("UpdateCollectionPersonalMediaSource")
        .WithSummary("Updates a Collection-owned Gallery reference or smart View rule without materializing assets.")
        .Produces<CollectionPersonalMediaSourceDto>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.CollectionsWrite)
        .RequireCatalogueEntityAccess(ApplicationPermissionIds.CollectionsWrite, "Collection", "id");

        group.MapDelete("/{id:guid}/personal-media/{sourceId:guid}", async (
            Guid id,
            Guid sourceId,
            IViewRequestProfileContext profileContext,
            CollectionPersonalMediaService service,
            CancellationToken ct) =>
        {
            var authority = await profileContext.ResolveAuthorityAsync(ct);
            if (authority.ActiveProfileId is null)
            {
                return MissingTrustedProfile();
            }

            var result = await service.RemoveAsync(id, sourceId, authority, ct);
            if (!result.Found)
            {
                return ApiErrors.NotFound($"Collection '{id}' or personal-media source '{sourceId}' was not found.");
            }

            if (!result.Allowed)
            {
                return ApiErrors.Forbidden("Only an administrator profile may edit personal-media Collection sources.");
            }

            if (result.WouldEmptyCollection)
            {
                return ApiErrors.Conflict(result.Error!);
            }

            if (!string.IsNullOrWhiteSpace(result.Error))
            {
                return ApiErrors.BadRequest(result.Error);
            }

            return Results.NoContent();
        })
        .WithName("RemoveCollectionPersonalMediaSource")
        .WithSummary("Removes a personal-media source reference without changing its Gallery or personal assets; refused while it is a manually curated collection's last member.")
        .Produces(StatusCodes.Status204NoContent)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status409Conflict)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.CollectionsWrite)
        .RequireCatalogueEntityAccess(ApplicationPermissionIds.CollectionsWrite, "Collection", "id");

        return group;
    }

    private static IResult ToWriteResult(Guid collectionId, CollectionPersonalMediaWriteResult result)
    {
        if (!result.Found)
        {
            return ApiErrors.NotFound($"Collection '{collectionId}' or its personal-media source was not found.");
        }

        if (!result.Allowed)
        {
            return ApiErrors.Forbidden("Only an administrator profile may edit personal-media sources on a Custom Collection.");
        }

        if (!string.IsNullOrWhiteSpace(result.Error))
        {
            return ApiErrors.BadRequest(result.Error);
        }

        return Results.Ok(result.Source);
    }

    private static IResult MissingTrustedProfile() => ApiErrors.Problem(
        StatusCodes.Status401Unauthorized,
        "Authentication required.",
        "A valid signed active-profile assertion is required for personal-media Collection access.");
}
