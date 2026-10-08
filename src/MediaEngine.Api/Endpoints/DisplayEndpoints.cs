using System.Security.Claims;
using MediaEngine.Api.Http;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services;
using MediaEngine.Api.Services.Display;
using MediaEngine.Api.Services.ReadServices;
using MediaEngine.Contracts.Artwork;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Contracts.Collections;
using MediaEngine.Contracts.Display;
using MediaEngine.Contracts.Paging;
using MediaEngine.Domain.Authorization;
using MediaEngine.Storage;

namespace MediaEngine.Api.Endpoints;

public static class DisplayEndpoints
{
    public static IEndpointRouteBuilder MapDisplayEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/display")
            .WithTags("Display");

        group.MapGet("/home", async (bool? includeCatalog, ClaimsPrincipal user, DisplayComposerService display, CancellationToken ct) =>
            Results.Ok(await display.BuildHomeAsync(includeCatalog ?? true, ProfileId(user), ct)))
            .WithName("GetDisplayHome")
            .WithSummary("Returns the cross-platform consumer Home display model.")
            .Produces<DisplayPageDto>(StatusCodes.Status200OK)
            .RequireClientScope(ClientApiScopes.LibraryRead);

        group.MapGet("/browse", async (
            string? lane,
            string? mediaType,
            string? grouping,
            string? search,
            string? genres,
            string? creator,
            string? status,
            string? year,
            string? sort,
            int? offset,
            int? limit,
            bool? includeCatalog,
            ClaimsPrincipal user,
            DisplayComposerService display,
            CancellationToken ct) =>
        {
            var paged = PagedRequest.From(offset, limit, defaultLimit: 48);
            return Results.Ok(await display.BuildBrowseAsync(
                lane,
                mediaType,
                grouping,
                search,
                paged.Offset,
                paged.Limit,
                includeCatalog ?? true,
                ProfileId(user),
                ct,
                genres,
                creator,
                status,
                year,
                sort));
        })
            .WithName("GetDisplayBrowse")
            .WithSummary("Returns cross-platform display cards for a media lane or browse query.")
            .Produces<DisplayPageDto>(StatusCodes.Status200OK)
            .RequireClientScope(ClientApiScopes.LibraryRead);

        group.MapGet("/continue", async (
            ClaimsPrincipal user,
            string? lane,
            string? mediaType,
            int? limit,
            bool? includeCatalog,
            DisplayComposerService display,
            CancellationToken ct) =>
        {
            var paged = PagedRequest.From(null, limit, defaultLimit: 24);
            return Results.Ok(await display.BuildContinueAsync(lane, paged.Limit, includeCatalog ?? true, ct, mediaType, ProfileId(user)));
        })
            .WithName("GetDisplayContinue")
            .WithSummary("Returns cross-platform continue cards with progress.")
            .Produces<DisplayPageDto>(StatusCodes.Status200OK)
            .RequireClientScope(ClientApiScopes.ProgressRead);

        group.MapGet("/recent", async (string? type, string? cursor, int? limit, ClaimsPrincipal user, DisplayRecentComposerService recent, CancellationToken ct) =>
        {
            if (ProfileId(user) is not { } profileId)
            {
                return Results.Unauthorized();
            }
            var paged = PagedRequest.From(null, limit, defaultLimit: 18);
            try { return Results.Ok(await recent.LoadAsync(type, cursor, paged.Limit, profileId, ct)); }
            catch (ArgumentException exception) { return ApiErrors.BadRequest(exception.Message); }
        })
            .WithName("GetDisplayRecent")
            .WithSummary("Returns permitted catalogue and Mine View additions with a stable recent cursor.")
            .Produces<DisplayRecentPageDto>(StatusCodes.Status200OK)
            .RequireClientScope(ClientApiScopes.LibraryRead);

        group.MapGet("/contributor-shelves", async (ContributorShelfReadService shelves, CancellationToken ct) =>
            Results.Ok(await shelves.LoadAsync(ct)))
            .WithName("GetDisplayContributorShelves")
            .WithSummary("Returns multi-work Collections shelves grouped by canonical primary contributors.")
            .Produces<IReadOnlyList<ContributorShelfDto>>(StatusCodes.Status200OK)
            .RequireClientScope(ClientApiScopes.LibraryRead);

        group.MapGet("/search", async (
            string? q,
            int? limit,
            IUniversalSearchReadService search,
            CancellationToken ct) =>
        {
            var paged = PagedRequest.From(null, limit, defaultLimit: 48);
            return Results.Ok(await search.SearchAsync(q, paged.Limit, ct));
        })
            .WithName("GetDisplaySearch")
            .WithSummary("Returns ranked local media, people, series, collections, and playlists for universal search.")
            .Produces<UniversalSearchResponseDto>(StatusCodes.Status200OK)
            .RequireClientScope(ClientApiScopes.LibraryRead);

        group.MapGet("/artwork", async (
            string? entityKind,
            string? artworkType,
            string? search,
            string? browseAs,
            string? mediaType,
            string? artworkState,
            string? sort,
            int? offset,
            int? limit,
            ArtworkLibraryReadService artwork,
            CancellationToken ct) =>
        {
            var paged = PagedRequest.From(offset, limit, defaultLimit: 48);
            return Results.Ok(await artwork.BrowseAsync(
                entityKind,
                artworkType,
                search,
                browseAs,
                mediaType,
                artworkState,
                sort,
                paged.Offset,
                paged.Limit,
                ct));
        })
            .WithName("GetArtworkLibrary")
            .WithSummary("Returns the pageable virtual artwork galleries for owned media and library people.")
            .Produces<ArtworkBrowsePageDto>(StatusCodes.Status200OK)
            .RequireClientScope(ClientApiScopes.ArtworkRead);

        group.MapGet("/artwork/assets", async (
            string? search,
            string[]? role,
            string[]? aspect,
            string[]? mediaType,
            string[]? source,
            string[]? year,
            string[]? entityType,
            string? relatedEntityType,
            Guid? relatedEntityId,
            string? targetEntityType,
            Guid? targetEntityId,
            string? targetRole,
            string? targetSourceAssetType,
            ArtworkPickerScope? pickerScope,
            ArtworkUsageFilter? usage,
            ArtworkAssetSort? sort,
            int? minimumWidth,
            int? minimumHeight,
            int? offset,
            int? limit,
            ArtworkAssetService assets,
            CancellationToken ct) =>
        {
            var paged = PagedRequest.From(offset, limit, defaultLimit: 48);
            return Results.Ok(await assets.BrowseAsync(new ArtworkAssetQuery(
                Search: search,
                Roles: role,
                Aspects: aspect,
                MediaTypes: mediaType,
                SourceProviders: source,
                Years: year,
                EntityTypes: entityType,
                RelatedEntityType: relatedEntityType,
                RelatedEntityId: relatedEntityId,
                TargetEntityType: targetEntityType,
                TargetEntityId: targetEntityId,
                TargetRole: targetRole,
                TargetSourceAssetType: targetSourceAssetType,
                PickerScope: pickerScope ?? ArtworkPickerScope.All,
                Usage: usage ?? ArtworkUsageFilter.All,
                Sort: sort ?? ArtworkAssetSort.Relevance,
                MinimumWidth: minimumWidth,
                MinimumHeight: minimumHeight,
                Offset: paged.Offset,
                Limit: paged.Limit), ct));
        })
            .WithName("GetArtworkAssets")
            .WithSummary("Searches the bounded canonical artwork asset library for galleries and pickers.")
            .Produces<ArtworkAssetPageDto>(StatusCodes.Status200OK)
            .RequireClientScope(ClientApiScopes.ArtworkRead);

        group.MapGet("/artwork/universes/{collectionId:guid}/entities", (
            Guid collectionId,
            ArtworkLibraryReadService artwork,
            CancellationToken ct) =>
            Results.Ok(artwork.LoadUniverseHierarchy(collectionId, ct)))
            .WithName("GetUniverseArtworkHierarchy")
            .WithSummary("Returns the canonical characters, places, organizations, events, and objects for one Universe artwork workspace.")
            .Produces<IReadOnlyList<ArtworkLibraryItemDto>>(StatusCodes.Status200OK)
            .RequireClientScope(ClientApiScopes.ArtworkRead);

        group.MapGet("/artwork/entities/{entityType}/{entityId:guid}", async (
            string entityType,
            Guid entityId,
            string? mediaType,
            string? groupKind,
            string[]? assetType,
            ArtworkAssetService assets,
            CancellationToken ct) =>
            Results.Ok(await assets.GetEntityAsync(entityType, entityId, mediaType, groupKind, assetType, ct)))
            .WithName("GetEntityArtworkWorkspace")
            .WithSummary("Returns lazy-loaded artwork variants for one entity workspace.")
            .Produces<ArtworkEntityWorkspaceDto>(StatusCodes.Status200OK)
            .RequireClientScope(ClientApiScopes.ArtworkRead);

        group.MapGet("/artwork/works/{workId:guid}/effective", async (
            Guid workId,
            string role,
            string? sourceAssetType,
            ArtworkAssetService assets,
            CancellationToken ct) =>
        {
            var selection = await assets.GetEffectiveWorkArtworkAsync(workId, role, sourceAssetType, ct);
            return selection is null ? ApiErrors.NotFound("Work not found.") : Results.Ok(selection);
        })
            .WithName("GetEffectiveWorkArtwork")
            .WithSummary("Returns preferred artwork and its verified owner through a work's canonical parent chain.")
            .Produces<EffectiveArtworkSelection>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .RequireClientScope(ClientApiScopes.ArtworkRead);

        group.MapGet("/artwork/media-assets/{assetId:guid}/effective-cover", async (
            Guid assetId,
            HttpContext http,
            CatalogueResourceAuthorizationService resources,
            MediaEditorEditionArtworkRepository artwork,
            CancellationToken ct) =>
        {
            if (await resources.EvaluateAssetAsync(http, assetId,
                    ApplicationPermissionIds.ArtworkRead, ct) != CatalogueResourceAccess.Allowed)
            {
                return ApiErrors.NotFound("Owned file not found.");
            }
            var selection = await artwork.GetEffectiveAssetCoverAsync(assetId, ct);
            return selection is null
                ? ApiErrors.NotFound("Owned file cover not found.")
                : Results.Ok(selection);
        })
            .WithName("GetEffectiveOwnedAssetCover")
            .WithSummary("Returns the preferred Edition cover for an authorized owned file, falling back through its Work ancestry.")
            .Produces<EffectiveArtworkSelection>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .RequireClientScope(ClientApiScopes.ArtworkRead);

        group.MapGet("/artwork/assets/{assetId:guid}/content", (
            Guid assetId,
            string? size,
            ArtworkAssetService assets) =>
        {
            var path = assets.ResolveContentPath(assetId, size);
            if (path is null)
            {
                return ApiErrors.NotFound("Artwork image not found.");
            }

            var contentType = Path.GetExtension(path).Equals(".png", StringComparison.OrdinalIgnoreCase)
                ? "image/png"
                : Path.GetExtension(path).Equals(".webp", StringComparison.OrdinalIgnoreCase)
                    ? "image/webp"
                    : "image/jpeg";
            return Results.File(path, contentType, enableRangeProcessing: true);
        })
            .WithName("GetArtworkAssetContent")
            .WithSummary("Streams one canonical artwork asset rendition.")
            .Produces<byte[]>(StatusCodes.Status200OK, "image/jpeg")
            .RequireClientScope(ClientApiScopes.ArtworkRead);

        group.MapPost("/artwork/entities/{entityType}/{entityId:guid}/links", async (
            string entityType,
            Guid entityId,
            ArtworkLinkRequest request,
            ArtworkAssetService assets,
            CancellationToken ct) =>
        {
            await assets.LinkAsync(entityType, entityId, request, ct);
            return Results.Ok(await assets.GetEntityAsync(entityType, entityId, ct));
        })
            .WithName("LinkArtworkAsset")
            .WithSummary("Links an existing canonical image without copying bytes or renditions.")
            .Produces<ArtworkEntityWorkspaceDto>(StatusCodes.Status200OK)
            .RequireAdministratorOrApplication(ApplicationPermissionIds.MetadataWrite)
            .RequireAnyCatalogueEntityAccess(ApplicationPermissionIds.MetadataWrite);

        group.MapPost("/artwork/entities/{entityType}/{entityId:guid}/from-url", async (
            string entityType,
            Guid entityId,
            ArtworkFromUrlRequest request,
            ArtworkAssetService assets,
            CancellationToken ct) =>
        {
            try
            {
                return Results.Ok(await assets.AddFromUrlAsync(entityType, entityId, request, ct));
            }
            catch (InvalidOperationException ex)
            {
                return ApiErrors.BadRequest(ex.Message);
            }
        })
            .WithName("AddArtworkFromUrl")
            .WithSummary("Imports or reuses a canonical image from an explicit URL and links it to the entity.")
            .Produces<ArtworkAssetDto>(StatusCodes.Status200OK)
            .RequireAdministratorOrApplication(ApplicationPermissionIds.MetadataWrite)
            .RequireAnyCatalogueEntityAccess(ApplicationPermissionIds.MetadataWrite);

        group.MapPost("/artwork/entities/{entityType}/{entityId:guid}/upload", async (
            string entityType,
            Guid entityId,
            HttpRequest httpRequest,
            ArtworkAssetService assets,
            CancellationToken ct) =>
        {
            if (!httpRequest.HasFormContentType)
            {
                return ApiErrors.BadRequest("Expected multipart form data.");
            }
            var form = await httpRequest.ReadFormAsync(ct);
            var file = form.Files.GetFile("file") ?? form.Files.FirstOrDefault();
            var role = form["role"].FirstOrDefault() ?? "Primary";
            if (file is null || file.Length == 0)
            {
                return ApiErrors.BadRequest("No image was uploaded.");
            }
            if (file.Length > 10 * 1024 * 1024)
            {
                return ApiErrors.BadRequest("Artwork must be 10 MB or smaller.");
            }
            var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
            if (extension is not (".jpg" or ".jpeg" or ".png"))
            {
                return ApiErrors.BadRequest("Artwork must be a JPEG or PNG image.");
            }
            try
            {
                await using var stream = file.OpenReadStream();
                var linked = await assets.UploadAsync(stream, extension, entityType, entityId,
                    new ArtworkLinkRequest(
                        Guid.Empty,
                        role,
                        form["context"].FirstOrDefault(),
                        true,
                        form["entityLabel"].FirstOrDefault(),
                        form["mediaType"].FirstOrDefault(),
                        form["year"].FirstOrDefault(),
                        form["sourceAssetType"].FirstOrDefault()),
                    "user_upload", null, ct);
                return Results.Ok(linked);
            }
            catch (InvalidOperationException ex)
            {
                return ApiErrors.BadRequest(ex.Message);
            }
        })
            .WithName("UploadCanonicalArtwork")
            .WithSummary("Adds a user upload to the canonical image store and links it to the entity.")
            .Produces<ArtworkAssetDto>(StatusCodes.Status200OK)
            .DisableAntiforgery()
            .RequireAdministratorOrApplication(ApplicationPermissionIds.MetadataWrite)
            .RequireAnyCatalogueEntityAccess(ApplicationPermissionIds.MetadataWrite);

        group.MapDelete("/artwork/links/{linkId:guid}", async (
            Guid linkId,
            ArtworkAssetService assets,
            CancellationToken ct) =>
        {
            await assets.RemoveLinkAsync(linkId, ct);
            return Results.NoContent();
        })
            .WithName("RemoveArtworkLink")
            .WithSummary("Removes one entity usage without deleting the shared image.")
            .Produces(StatusCodes.Status204NoContent)
            .RequireAdministratorOrApplication(ApplicationPermissionIds.MetadataWrite)
            .RequireArtworkLinkAccess(ApplicationPermissionIds.MetadataWrite);

        group.MapGet("/shelves/{shelfKey}", async (
            string shelfKey,
            string? lane,
            string? mediaType,
            string? grouping,
            string? search,
            string? cursor,
            int? offset,
            int? limit,
            ClaimsPrincipal user,
            DisplayComposerService display,
            CancellationToken ct) =>
        {
            var paged = PagedRequest.From(offset, limit, defaultLimit: 24);
            var page = await display.BuildShelfPageAsync(
                shelfKey,
                lane,
                mediaType,
                grouping,
                search,
                cursor,
                paged.Offset,
                paged.Limit,
                ProfileId(user),
                ct);
            return page is null ? ApiErrors.NotFound($"No shelf found for key '{shelfKey}'.") : Results.Ok(page);
        })
            .WithName("GetDisplayShelf")
            .WithSummary("Returns one paged display shelf for native and TV clients.")
            .Produces<DisplayShelfPageDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .RequireClientScope(ClientApiScopes.LibraryRead);

        group.MapGet("/groups/{groupId:guid}", async (
            Guid groupId,
            bool? includeCatalog,
            ClaimsPrincipal user,
            DisplayComposerService display,
            CancellationToken ct) =>
        {
            var page = await display.BuildGroupAsync(groupId, includeCatalog ?? true, ProfileId(user), ct);
            return page is null ? ApiErrors.NotFound($"No display group found for '{groupId}'.") : Results.Ok(page);
        })
            .WithName("GetDisplayGroup")
            .WithSummary("Returns display cards for a consumer group or collection.")
            .Produces<DisplayPageDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .RequireClientScope(ClientApiScopes.LibraryRead);

        return app;
    }

    private static Guid? ProfileId(ClaimsPrincipal user) =>
        Guid.TryParse(user.FindFirstValue(TuvimaClaimTypes.ActiveProfileId), out var value) ? value : null;
}
