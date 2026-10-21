using MediaEngine.Api.Http;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services.Vault;
using MediaEngine.Contracts.Vault;
using Microsoft.AspNetCore.Mvc;

namespace MediaEngine.Api.Endpoints;

/// <summary>
/// The Vault's images and galleries (Videos spec D44), built on View's photo and gallery system with the Vault scope on: folders appear as
/// galleries, hand-made and rule galleries, a timeline-ordered list, search, tags, favourites, thumbnails and previews, and the recycle bin.
/// Every route goes through the Vault browsing gate (the same one the video routes use): a locked session, an account without the Watch
/// feature, a service app or a paired app gets "not found", never "forbidden", and every answer, found or not, is private and uncacheable.
/// Pictures, tags, titles and search words are asked for by id or in a body; none is ever put in an address or a log line. Finding new
/// images, deleting them and emptying the bin are for an administrator in person; a person who is not one gets "not found" there too.
/// </summary>
public static class VaultImageEndpoints
{
    public static IEndpointRouteBuilder MapVaultImageEndpoints(this IEndpointRouteBuilder app)
    {
        var images = app.MapGroup("/api/v1/vault/images").WithTags("Vault images").RequireAuthorization(AuthPolicies.Authenticated);
        images.AddEndpointFilter(VaultBrowseEndpoints.GateAsync);

        images.MapGet("", async (
            [FromServices] VaultImageService service,
            [FromQuery] Guid? gallery, [FromQuery] Guid? tag, [FromQuery] bool? favorite,
            [FromQuery] string? cursor, [FromQuery] int? take, CancellationToken ct) =>
        {
            try
            {
                var page = await service.ListAsync(new VaultImageListRequest(
                    gallery, tag, favorite == true, Cursor: cursor, Take: take ?? VaultImageService.DefaultTake), ct);
                return page is null ? ApiErrors.NotFound("Not found.") : Results.Ok(page);
            }
            catch (ArgumentException)
            {
                return ApiErrors.BadRequest("That page could not be read.");
            }
        })
        .WithName("ListVaultImages")
        .WithSummary("Vault images, newest picture first: all of them, or those of one gallery, one tag or the favourites.")
        .Produces<VaultImagePageDto>()
        .ProducesProblem(StatusCodes.Status404NotFound);

        // POST, because the words typed travel in the body and are never put in an address or a log line.
        images.MapPost("/search", async ([FromServices] VaultImageService service, VaultImageSearchRequest request, CancellationToken ct) =>
        {
            if ((request.Text?.Trim().Length ?? 0) < VaultImageService.MinimumSearchLength)
            {
                return ApiErrors.BadRequest("Type at least two characters.");
            }

            try
            {
                var page = await service.SearchAsync(request, ct);
                return page is null ? ApiErrors.NotFound("Not found.") : Results.Ok(page);
            }
            catch (ArgumentException)
            {
                return ApiErrors.BadRequest("That page could not be read.");
            }
        })
        .WithName("SearchVaultImages")
        .WithSummary("Searches only the Vault's images: file-derived title, file name, date and the Vault's own tags.")
        .Produces<VaultImagePageDto>()
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status404NotFound);

        images.MapGet("/galleries", async ([FromServices] VaultImageService service, CancellationToken ct) =>
            Results.Ok(await service.ListGalleriesAsync(ct)))
        .WithName("ListVaultImageGalleries")
        .WithSummary("The Vault's image galleries: a gallery for every folder of images, then the hand-made and rule galleries.")
        .Produces<IReadOnlyList<VaultImageGalleryDto>>()
        .ProducesProblem(StatusCodes.Status404NotFound);

        images.MapGet("/galleries/{galleryId:guid}", async ([FromServices] VaultImageService service, Guid galleryId, CancellationToken ct) =>
            await service.GetGalleryAsync(galleryId, ct) is { } gallery ? Results.Ok(gallery) : ApiErrors.NotFound("Not found."))
        .WithName("GetVaultImageGallery")
        .WithSummary("One Vault image gallery.")
        .Produces<VaultImageGalleryDto>()
        .ProducesProblem(StatusCodes.Status404NotFound);

        images.MapPost("/galleries", async (
            HttpContext http, [FromServices] IRequestAuthorityResolver resolver, [FromServices] VaultImageService service,
            VaultImageGalleryRequest request, CancellationToken ct) =>
        {
            var authority = await resolver.ResolveAsync(http, ct);
            return Answer(await service.CreateGalleryAsync(request, authority.ActiveProfileId, ct));
        })
        .WithName("CreateVaultImageGallery")
        .WithSummary("Makes a hand-made or a rule gallery. A rule uses View's Smart Gallery rule document, limited to what Vault images have.")
        .Produces<VaultImageGalleryDto>()
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status404NotFound);

        images.MapPut("/galleries/{galleryId:guid}", async (
            [FromServices] VaultImageService service, Guid galleryId, VaultImageGalleryRequest request, CancellationToken ct) =>
            Answer(await service.UpdateGalleryAsync(galleryId, request, ct)))
        .WithName("UpdateVaultImageGallery")
        .WithSummary("Renames a hand-made or rule gallery, changes its rule or its cover. A folder gallery cannot be changed.")
        .Produces<VaultImageGalleryDto>()
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status404NotFound);

        images.MapDelete("/galleries/{galleryId:guid}", async ([FromServices] VaultImageService service, Guid galleryId, CancellationToken ct) =>
            await service.DeleteGalleryAsync(galleryId, ct) ? Results.NoContent() : ApiErrors.NotFound("Not found."))
        .WithName("DeleteVaultImageGallery")
        .WithSummary("Deletes a hand-made or rule gallery. The images are kept.")
        .Produces(StatusCodes.Status204NoContent)
        .ProducesProblem(StatusCodes.Status404NotFound);

        images.MapPost("/galleries/{galleryId:guid}/items", async (
            [FromServices] VaultImageService service, Guid galleryId, VaultImageIdsRequest request, CancellationToken ct) =>
            Answer(await service.AddToGalleryAsync(galleryId, request.ImageIds, ct)))
        .WithName("AddVaultImagesToGallery")
        .WithSummary("Adds Vault images to a hand-made gallery. Anything that is not a Vault image is left out without a word about why.")
        .Produces<int>()
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status404NotFound);

        images.MapPost("/galleries/{galleryId:guid}/items/remove", async (
            [FromServices] VaultImageService service, Guid galleryId, VaultImageIdsRequest request, CancellationToken ct) =>
            Answer(await service.RemoveFromGalleryAsync(galleryId, request.ImageIds, ct)))
        .WithName("RemoveVaultImagesFromGallery")
        .WithSummary("Takes images out of a hand-made gallery. The images are kept.")
        .Produces<int>()
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status404NotFound);

        images.MapGet("/tags", async ([FromServices] VaultImageService service, CancellationToken ct) =>
            Results.Ok(await service.ListTagsAsync(ct)))
        .WithName("ListVaultImageTags")
        .WithSummary("The Vault's own image tags, most used first. They never join the shared tag list.")
        .Produces<IReadOnlyList<VaultImageTagDto>>()
        .ProducesProblem(StatusCodes.Status404NotFound);

        images.MapGet("/trash", async (
            [FromServices] VaultImageService service, [FromQuery] string? cursor, [FromQuery] int? take, CancellationToken ct) =>
        {
            try
            {
                return Results.Ok(await service.ListDeletedAsync(cursor, take ?? VaultImageService.DefaultTake, ct));
            }
            catch (ArgumentException)
            {
                return ApiErrors.BadRequest("That page could not be read.");
            }
        })
        .WithName("ListVaultImageTrash")
        .WithSummary("Deleted Vault images waiting in the recycle bin.")
        .Produces<VaultImagePageDto>()
        .ProducesProblem(StatusCodes.Status404NotFound)
        .AddEndpointFilter(AdministratorOnlyAsync);

        images.MapPost("/trash/empty", async ([FromServices] VaultImageService service, CancellationToken ct) =>
            Results.Ok(new VaultImageEmptiedDto { Removed = await service.EmptyBinAsync(ct) }))
        .WithName("EmptyVaultImageTrash")
        .WithSummary("Removes every deleted Vault image from the disk for good.")
        .Produces<VaultImageEmptiedDto>()
        .ProducesProblem(StatusCodes.Status404NotFound)
        .AddEndpointFilter(AdministratorOnlyAsync);

        images.MapPost("/scan", async ([FromServices] VaultImageService service, CancellationToken ct) =>
            Results.Ok(await service.ScanAsync(ct)))
        .WithName("ScanVaultImages")
        .WithSummary("Looks in the Vault library folders for new images and puts each in the gallery of its folder. Counts only are returned.")
        .Produces<VaultImageScanDto>()
        .ProducesProblem(StatusCodes.Status404NotFound)
        .AddEndpointFilter(AdministratorOnlyAsync);

        images.MapGet("/{imageId:guid}", async ([FromServices] VaultImageService service, Guid imageId, CancellationToken ct) =>
            await service.GetAsync(imageId, ct) is { } detail ? Results.Ok(detail) : ApiErrors.NotFound("Not found."))
        .WithName("GetVaultImage")
        .WithSummary("One Vault image with its tags, folder and how it was shot.")
        .Produces<VaultImageDetailDto>()
        .ProducesProblem(StatusCodes.Status404NotFound);

        images.MapGet("/{imageId:guid}/thumbnail", async (
            [FromServices] VaultImageService service, Guid imageId, [FromQuery] int? w, CancellationToken ct) =>
            await PictureAsync(service, imageId, w ?? 320, ct))
        .WithName("GetVaultImageThumbnail")
        .WithSummary("A Vault image as a thumbnail (160, 320 or 640 pixels on the long edge), made once and kept apart from View's.")
        .Produces<byte[]>(StatusCodes.Status200OK, "image/jpeg")
        .ProducesProblem(StatusCodes.Status404NotFound)
        .RequireRateLimiting("view_images");

        images.MapGet("/{imageId:guid}/preview", async (
            [FromServices] VaultImageService service, Guid imageId, [FromQuery] int? w, CancellationToken ct) =>
            await PictureAsync(service, imageId, w ?? 1280, ct))
        .WithName("GetVaultImagePreview")
        .WithSummary("A Vault image sized for the viewer (1280 or 2048 pixels on the long edge).")
        .Produces<byte[]>(StatusCodes.Status200OK, "image/jpeg")
        .ProducesProblem(StatusCodes.Status404NotFound)
        .RequireRateLimiting("view_images");

        images.MapPut("/{imageId:guid}/tags", async (
            [FromServices] VaultImageService service, Guid imageId, VaultImageTagsRequest request, CancellationToken ct) =>
            await service.SetTagsAsync(imageId, request.Tags, ct)
                ? Results.Ok(await service.GetAsync(imageId, ct))
                : ApiErrors.NotFound("Not found."))
        .WithName("SetVaultImageTags")
        .WithSummary("Replaces an image's tags by name. Names that are new join the Vault's own tags, never the shared list.")
        .Produces<VaultImageDetailDto>()
        .ProducesProblem(StatusCodes.Status404NotFound);

        images.MapPut("/{imageId:guid}/favorite", async (
            [FromServices] VaultImageService service, Guid imageId, VaultImageFavoriteRequest request, CancellationToken ct) =>
            await service.SetFavoriteAsync(imageId, request.Favorite, ct) ? Results.NoContent() : ApiErrors.NotFound("Not found."))
        .WithName("SetVaultImageFavorite")
        .WithSummary("Marks or unmarks an image as a favourite.")
        .Produces(StatusCodes.Status204NoContent)
        .ProducesProblem(StatusCodes.Status404NotFound);

        images.MapPost("/{imageId:guid}/delete", async ([FromServices] VaultImageService service, Guid imageId, CancellationToken ct) =>
            Answer(await service.DeleteAsync(imageId, ct)))
        .WithName("DeleteVaultImage")
        .WithSummary("Moves an image's files to the recycle bin. They leave the disk only when the bin is emptied.")
        .Produces<bool>()
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .AddEndpointFilter(AdministratorOnlyAsync);

        images.MapPost("/{imageId:guid}/restore", async ([FromServices] VaultImageService service, Guid imageId, CancellationToken ct) =>
            Answer(await service.RestoreAsync(imageId, ct)))
        .WithName("RestoreVaultImage")
        .WithSummary("Puts a deleted image back where it was. Nothing is overwritten.")
        .Produces<bool>()
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .AddEndpointFilter(AdministratorOnlyAsync);

        return app;
    }

    /// <summary>
    /// Deleting, restoring, emptying the bin and looking for new files are for an effective administrator signed in at the Dashboard. A
    /// session that is not one gets the same "not found" a locked one gets, so the route never says it exists. Runs after the Vault gate.
    /// </summary>
    internal static async ValueTask<object?> AdministratorOnlyAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var authority = await http.RequestServices.GetRequiredService<IRequestAuthorityResolver>().ResolveAsync(http, http.RequestAborted);
        return authority.PrincipalKind == MediaEngine.Domain.Authorization.PrincipalKind.Human && authority.IsEffectiveAdministrator
            ? await next(context)
            : ApiErrors.NotFound("Not found.");
    }

    private static async Task<IResult> PictureAsync(VaultImageService service, Guid imageId, int width, CancellationToken ct)
    {
        if (!VaultImageService.IsAllowedWidth(width) || await service.OpenAsync(imageId, width, ct) is not { } file)
        {
            return ApiErrors.NotFound("Not found.");
        }

        return Results.File(file.Path, file.ContentType, enableRangeProcessing: true);
    }

    private static IResult Answer<T>(VaultImageResult<T> result) => result.Outcome switch
    {
        VaultImageOutcome.Done => Results.Ok(result.Value),
        VaultImageOutcome.NotFound => ApiErrors.NotFound("Not found."),
        _ => ApiErrors.BadRequest(result.Message ?? "That could not be done."),
    };
}
