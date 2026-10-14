using System.Security.Claims;
using MediaEngine.Api.Http;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services.View;
using MediaEngine.Contracts.LocalAssets;
using MediaEngine.Contracts.Paging;
using MediaEngine.Contracts.Playback;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.PersonalMedia;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Api.Endpoints;

/// <summary>
/// Read-only Photos for paired native apps under <c>/api/v1/view</c>.
/// <para>
/// A paired app is one person on one device. Every route resolves who is asking from the device token's active
/// profile (<see cref="IViewRequestProfileContext"/>) and asks the View authorization service for
/// <see cref="ViewScopeRequest.Mine"/>: there is no scope or profile parameter, so an app can never name someone else's
/// space. A person's own photos and videos are all it ever shows.
/// </para>
/// <para>
/// This surface is photos and videos only. A document is answered exactly like an id that does not exist (404, the
/// same body), on every route and for every profile, so a profile without Documents cannot learn that one exists
/// through the timeline, Galleries, people, places, counts, items or any file route. The Dashboard's
/// <c>/view</c> routes and their Documents rules are unchanged.
/// </para>
/// </summary>
public static class ViewClientEndpoints
{
    private static readonly string[] PhotoKinds = [LocalAssetMediaKinds.Image, LocalAssetMediaKinds.Video];

    private const string BasePath = "/api/v1/view";

    public static IEndpointRouteBuilder MapViewClientEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup(BasePath).WithTags("View (native clients)")
            .RequireAuthorization(AuthPolicies.Authenticated);

        group.MapGet("/timeline", async (string? cursor, int? limit,
            IViewRequestProfileContext identity, IViewQueryOrchestrator queries, CancellationToken ct) =>
        {
            var (authority, refusal) = await ResolvePairedAppAsync(identity, ct);
            if (refusal is not null)
            {
                return refusal;
            }

            try
            {
                var result = await queries.QueryAsync(new ViewAssetQueryRequest(
                    ViewScopeRequest.Mine, PagedRequest.From(0, limit, 100, 200).Limit, cursor,
                    MediaKinds: PhotoKinds), ct);
                return result.Outcome == ViewAccessOutcome.Allowed && result.Page is { } page
                    ? Results.Ok(ToTimeline(page, authority!.ActiveProfileId!.Value)) : Refuse(result.Outcome);
            }
            catch (ArgumentException exception) { return ApiErrors.BadRequest(exception.Message); }
        })
            .WithName("GetViewClientTimeline")
            .WithSummary("The paired person's own photos and videos, newest first, grouped by month.")
            .Produces<ViewClientTimelinePageDto>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .RequireClientScope(ApplicationPermissionIds.ViewPersonalRead.Value);

        group.MapGet("/galleries", async (IViewRequestProfileContext identity,
            IViewResourceAuthorizationService authorization, IViewGalleryRepository galleries,
            ILocalAssetRepository assets, CancellationToken ct) =>
        {
            var (authority, refusal) = await ResolvePairedAppAsync(identity, ct);
            if (refusal is not null)
            {
                return refusal;
            }

            var access = await authorization.AuthorizeAsync(authority!,
                new ViewResourceRequest(ViewScopeRequest.Mine, ViewResourceKind.Gallery, null), ct);
            if (!access.IsAllowed)
            {
                return Refuse(access.Outcome);
            }

            // Documents are never part of this surface: counts leave them out and a saved Documents view is not listed.
            var profileId = authority!.ActiveProfileId!.Value;
            return Results.Ok(new ViewGalleryListResponse(
                (await galleries.GetOwnedAsync(profileId, false, ct))
                    .Where(gallery => !ViewEndpoints.IsDocumentView(gallery))
                    .Select(gallery => ToClient(gallery, profileId, assets)).ToList(),
                (await galleries.GetSharedWithAsync(profileId, false, ct))
                    .Where(gallery => !ViewEndpoints.IsDocumentView(gallery))
                    .Select(gallery => ToClient(gallery, profileId, assets)).ToList()));
        })
            .WithName("GetViewClientGalleries")
            .WithSummary("The paired person's Galleries, and Galleries shared with them, without document counts.")
            .Produces<ViewGalleryListResponse>()
            .RequireClientScope(ApplicationPermissionIds.ViewGalleriesRead.Value);

        group.MapGet("/galleries/{id:guid}/items", async (Guid id, string? cursor, int? limit,
            IViewRequestProfileContext identity, IViewQueryOrchestrator queries, IViewGalleryRepository galleries,
            ILocalAssetRepository assets, CancellationToken ct) =>
        {
            var (authority, refusal) = await ResolvePairedAppAsync(identity, ct);
            if (refusal is not null)
            {
                return refusal;
            }

            // A saved Documents view reads as a Gallery that does not exist. This is decided before the cursor is read,
            // so a bad cursor on it answers 404 like it does for an id nobody has, never 400.
            if (await galleries.GetAsync(id, false, ct) is { } existing && ViewEndpoints.IsDocumentView(existing))
            {
                return Missing();
            }

            try
            {
                var result = await queries.QueryAsync(new ViewAssetQueryRequest(
                    ViewScopeRequest.Mine, PagedRequest.From(0, limit, 100, 200).Limit, cursor,
                    MediaKinds: PhotoKinds, GalleryId: id), ct);
                return result.Outcome == ViewAccessOutcome.Allowed && result.Page is { } page
                    ? Results.Ok(ToItems(page, authority!.ActiveProfileId!.Value)) : Refuse(result.Outcome);
            }
            catch (ArgumentException exception) { return ApiErrors.BadRequest(exception.Message); }
        })
            .WithName("GetViewClientGalleryItems")
            .WithSummary("The photos and videos of one Gallery, newest first.")
            .Produces<ViewClientItemPageDto>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .RequireClientScope(ApplicationPermissionIds.ViewGalleriesRead.Value);

        group.MapGet("/people", async (string? cursor, int? limit,
            IViewRequestProfileContext identity, ViewDiscoveryService discovery, CancellationToken ct) =>
        {
            if ((await ResolvePairedAppAsync(identity, ct)).Refusal is { } refusal)
            {
                return refusal;
            }

            try
            {
                var result = await discovery.GetPeopleAsync(new ViewDiscoveryRequest(
                    ViewScopeRequest.Mine, PagedRequest.From(null, limit, 100, 100).Limit, null, cursor), ct);
                return result.Outcome == ViewAccessOutcome.Allowed && result.Page is { } page
                    ? Results.Ok(page) : Refuse(result.Outcome);
            }
            catch (ArgumentException exception) { return ApiErrors.BadRequest(exception.Message); }
        })
            .WithName("GetViewClientPeople")
            .WithSummary("Named or reviewed people in the paired person's own photos and videos.")
            .Produces<ViewPeoplePageDto>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .RequireClientScope(ApplicationPermissionIds.ViewPersonalRead.Value);

        group.MapGet("/people/{key}/items", async (string key, string? cursor, int? limit,
            IViewRequestProfileContext identity, IViewQueryOrchestrator queries, CancellationToken ct) =>
        {
            var (authority, refusal) = await ResolvePairedAppAsync(identity, ct);
            if (refusal is not null)
            {
                return refusal;
            }

            if (string.IsNullOrWhiteSpace(key) || key.Length > 200)
            {
                return Missing();
            }

            try
            {
                var result = await queries.QueryAsync(new ViewAssetQueryRequest(
                    ViewScopeRequest.Mine, PagedRequest.From(0, limit, 100, 200).Limit, cursor,
                    MediaKinds: PhotoKinds, PersonKey: key), ct);
                return result.Outcome == ViewAccessOutcome.Allowed && result.Page is { } page
                    ? Results.Ok(ToItems(page, authority!.ActiveProfileId!.Value)) : Refuse(result.Outcome);
            }
            catch (ArgumentException exception) { return ApiErrors.BadRequest(exception.Message); }
        })
            .WithName("GetViewClientPersonItems")
            .WithSummary("The photos and videos that carry one person's name, newest first.")
            .Produces<ViewClientItemPageDto>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .RequireClientScope(ApplicationPermissionIds.ViewPersonalRead.Value);

        group.MapGet("/places", async (string? cursor, int? limit,
            IViewRequestProfileContext identity, ViewDiscoveryService discovery, CancellationToken ct) =>
        {
            if ((await ResolvePairedAppAsync(identity, ct)).Refusal is { } refusal)
            {
                return refusal;
            }

            try
            {
                var result = await discovery.GetPlacesAsync(new ViewDiscoveryRequest(
                    ViewScopeRequest.Mine, PagedRequest.From(null, limit, 50, 100).Limit, null, cursor), ct);
                return result.Outcome == ViewAccessOutcome.Allowed && result.Page is { } page
                    ? Results.Ok(page) : Refuse(result.Outcome);
            }
            catch (ArgumentException exception) { return ApiErrors.BadRequest(exception.Message); }
        })
            .WithName("GetViewClientPlaces")
            .WithSummary("Real place clusters (with latitude and longitude) from the paired person's own photos and videos.")
            .Produces<ViewPlacesPageDto>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .RequireClientScope(ApplicationPermissionIds.ViewPersonalRead.Value);

        group.MapGet("/items/{id:guid}", async (Guid id, IViewRequestProfileContext identity,
            IViewResourceAuthorizationService authorization, ILocalAssetRepository assets, CancellationToken ct) =>
        {
            var (authority, refusal) = await ResolvePairedAppAsync(identity, ct);
            if (refusal is not null)
            {
                return refusal;
            }

            var decision = await authorization.AuthorizeAsync(authority!,
                new ViewResourceRequest(ViewScopeRequest.Mine, ViewResourceKind.Asset, id), ct);
            if (!decision.IsAllowed)
            {
                return Refuse(decision.Outcome);
            }

            return assets.Find(id, ct) is { } item && IsPhotoOrVideo(item.MediaKind)
                ? Results.Ok(ToClient(item, authority!.ActiveProfileId!.Value)) : Missing();
        })
            .WithName("GetViewClientItem")
            .WithSummary("Size, taken date, camera, location and kind (image or video) of one photo or video.")
            .Produces<LocalAssetDto>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .RequireClientScope(ApplicationPermissionIds.ViewPersonalRead.Value);

        group.MapGet("/items/{id:guid}/thumbnail", async (Guid id, int? size, HttpResponse response,
            IViewRequestProfileContext identity, IViewResourceAuthorizationService authorization,
            IViewResourceStore resources, ViewThumbnailService thumbnails, ILocalAssetRepository assets,
            CancellationToken ct) =>
        {
            var edge = size ?? ViewThumbnailService.ThumbnailEdges[^1];
            if (!ViewThumbnailService.ThumbnailEdges.Contains(edge))
            {
                return ApiErrors.BadRequest($"size must be one of {string.Join(", ", ViewThumbnailService.ThumbnailEdges)}.");
            }

            var (file, refusal) = await ResolveFileAsync(
                id, ViewResourceKind.Thumbnail, identity, authorization, resources, assets, ct);
            if (refusal is not null)
            {
                return refusal;
            }

            var derivative = await thumbnails.GetOrCreateSizedAsync(id, file!, edge, ct);
            response.Headers.CacheControl = "private, no-cache";
            return derivative is null ? StreamEndpoints.CreateArtworkPlaceholderResult() : Results.File(derivative, "image/jpeg");
        })
            .WithName("GetViewClientItemThumbnail")
            .WithSummary("A small JPEG of a photo or the first frame of a video; size is the longest edge (160, 320 or 640).")
            .Produces(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .RequireClientScope(ApplicationPermissionIds.ViewPersonalRead.Value)
            .RequireRateLimiting("view_images");

        group.MapGet("/items/{id:guid}/preview", async (Guid id, int? size, HttpResponse response,
            IViewRequestProfileContext identity, IViewResourceAuthorizationService authorization,
            IViewResourceStore resources, ViewThumbnailService thumbnails, ILocalAssetRepository assets,
            CancellationToken ct) =>
        {
            var edge = size ?? ViewThumbnailService.PreviewEdges[^1];
            if (!ViewThumbnailService.PreviewEdges.Contains(edge))
            {
                return ApiErrors.BadRequest($"size must be one of {string.Join(", ", ViewThumbnailService.PreviewEdges)}.");
            }

            var (file, refusal) = await ResolveFileAsync(
                id, ViewResourceKind.Thumbnail, identity, authorization, resources, assets, ct);
            if (refusal is not null)
            {
                return refusal;
            }

            var derivative = await thumbnails.GetOrCreateSizedAsync(id, file!, edge, ct);
            response.Headers.CacheControl = "private, no-cache";
            return derivative is null ? StreamEndpoints.CreateArtworkPlaceholderResult() : Results.File(derivative, "image/jpeg");
        })
            .WithName("GetViewClientItemPreview")
            .WithSummary("A large JPEG for the viewer; size is the longest edge (1280 or 2048).")
            .Produces(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .RequireClientScope(ApplicationPermissionIds.ViewPersonalRead.Value)
            .RequireRateLimiting("view_images");

        // The original file. It is only ever served to the device token that is allowed this item (view.originals.read
        // on the token and the application, then the same resource rules as every other View file), never through an
        // address that works without that token.
        group.MapGet("/items/{id:guid}/original", async (Guid id, HttpResponse response,
            IViewRequestProfileContext identity, IViewResourceAuthorizationService authorization,
            IViewResourceStore resources, ILocalAssetRepository assets, CancellationToken ct) =>
        {
            var (file, refusal) = await ResolveFileAsync(
                id, ViewResourceKind.Original, identity, authorization, resources, assets, ct);
            if (refusal is not null)
            {
                return refusal;
            }

            response.Headers.CacheControl = "private, no-cache";
            response.Headers.XContentTypeOptions = "nosniff";
            return Results.File(file!.FilePath, file.MimeType, enableRangeProcessing: true);
        })
            .WithName("GetViewClientItemOriginal")
            .WithSummary("The original photo or video file, with HTTP range support, for the device token that may read it.")
            .Produces(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status206PartialContent)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .RequireClientScope(ApplicationPermissionIds.ViewOriginalsRead.Value)
            .RequireRateLimiting("streaming");

        // A personal video has no catalogue asset, probe or adaptive package, so the existing catalogue playback is not
        // involved; this answers the same manifest shape with the one delivery a personal video has.
        group.MapGet("/items/{id:guid}/playback", async (Guid id, ClaimsPrincipal user,
            IViewRequestProfileContext identity, IViewResourceAuthorizationService authorization,
            IViewResourceStore resources, ILocalAssetRepository assets, CancellationToken ct) =>
        {
            var (file, refusal) = await ResolveFileAsync(
                id, ViewResourceKind.Original, identity, authorization, resources, assets, ct);
            if (refusal is not null)
            {
                return refusal;
            }

            return assets.Find(id, ct) is { MediaKind: LocalAssetMediaKinds.Video } video
                ? Results.Ok(BuildPersonalVideoManifest(
                    video, file!, user.FindFirstValue(TuvimaClaimTypes.ClientId)))
                : Missing();
        })
            .WithName("GetViewClientItemPlayback")
            .WithSummary("The playback manifest for a personal video: one direct stream of the original file.")
            .Produces<PlaybackManifestDto>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .RequireClientScope(ApplicationPermissionIds.ViewOriginalsRead.Value);

        return app;
    }

    /// <summary>
    /// Authorizes one item for <see cref="ViewScopeRequest.Mine"/>, then resolves the file that may be served. Every
    /// reason there is nothing to serve (unknown id, someone else's item, a document, a kind that is not a photo or
    /// video, a missing file) is the same 404, so the answer never confirms that an id exists.
    /// </summary>
    private static async Task<(LocalAssetContentLocation? File, IResult? Refusal)> ResolveFileAsync(
        Guid id, ViewResourceKind kind, IViewRequestProfileContext identity,
        IViewResourceAuthorizationService authorization, IViewResourceStore resources,
        ILocalAssetRepository assets, CancellationToken ct)
    {
        var (authority, refusal) = await ResolvePairedAppAsync(identity, ct);
        if (refusal is not null)
        {
            return (null, refusal);
        }

        var decision = await authorization.AuthorizeAsync(authority!,
            new ViewResourceRequest(ViewScopeRequest.Mine, kind, id), ct);
        if (!decision.IsAllowed)
        {
            return (null, Refuse(decision.Outcome));
        }

        if (decision.Scope is null || assets.IsDocument(id, ct))
        {
            return (null, Missing());
        }

        var file = await resources.ResolveContentAsync(id, LocalAssetFileRoles.Primary, decision.Scope, ct);
        return file is null || !File.Exists(file.FilePath) || !IsPhotoOrVideoMime(file.MimeType)
            ? (null, Missing())
            : (file, null);
    }

    /// <summary>Only a live paired app (a device token bound to a person) may use this surface.</summary>
    private static async Task<(RequestAuthority? Authority, IResult? Refusal)> ResolvePairedAppAsync(
        IViewRequestProfileContext identity, CancellationToken ct)
    {
        var authority = await identity.ResolveAuthorityAsync(ct);
        if (!authority.IsAuthenticated)
        {
            return (null, ApiErrors.Problem(401, "Authentication required.", "A paired app is required."));
        }

        return authority.PrincipalKind != PrincipalKind.DelegatedUserClient
            || authority.ActiveProfileId is not { } profileId || profileId == Guid.Empty
            || authority.DeviceId is not { } deviceId || deviceId == Guid.Empty
                ? (null, ApiErrors.Forbidden("Only a paired app can use this."))
                : (authority, null);
    }

    private static IResult Refuse(ViewAccessOutcome outcome) => outcome switch
    {
        ViewAccessOutcome.Unauthenticated => ApiErrors.Problem(401, "Authentication required.", "A paired app is required."),
        // Forbidden is decided from the token and the person alone (before any item is looked up), so it says nothing
        // about whether an item exists.
        ViewAccessOutcome.Forbidden => ApiErrors.Forbidden("View access is not permitted."),
        _ => Missing(),
    };

    private static IResult Missing() => ApiErrors.NotFound("The View resource was not found.");

    private static bool IsPhotoOrVideo(string mediaKind) =>
        mediaKind is LocalAssetMediaKinds.Image or LocalAssetMediaKinds.Video;

    private static bool IsPhotoOrVideoMime(string mimeType) =>
        mimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
        || mimeType.StartsWith("video/", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Points the item's addresses at this surface and drops anything only Documents use. An item that is not the
    /// caller's own (it reached them through a shared Gallery) loses everything that lives in the owner's space: the
    /// owner and space ids, the embedded location and date a photo carries after its owner removed or changed them,
    /// and the owner's people and tags.
    /// </summary>
    private static LocalAssetDto ToClient(LocalAssetDto item, Guid profileId)
    {
        var own = item.OwnerProfileId == profileId;
        var client = item with
        {
            ThumbnailUrl = $"{BasePath}/items/{item.Id:D}/thumbnail",
            ContentUrl = $"{BasePath}/items/{item.Id:D}/original",
            DocumentInfo = null,
            Snippet = null,
        };
        return own
            ? client
            : client with
            {
                OwnerProfileId = null,
                PersonalSpaceId = null,
                EmbeddedLatitude = null,
                EmbeddedLongitude = null,
                EmbeddedCapturedAt = null,
                LocationUserOverride = false,
                CapturedAtUserOverride = false,
                People = [],
                Tags = [],
            };
    }

    /// <summary>
    /// A Gallery someone else shared keeps its name, description and cover, but not the owner's space or the rule behind
    /// a Smart Gallery. The contract's ids are not nullable, so they read as empty.
    /// </summary>
    private static ViewGalleryDto ToClient(ViewGallery gallery, Guid profileId, ILocalAssetRepository assets)
    {
        var dto = ViewEndpoints.ToContract(gallery, false, assets);
        return gallery.OwnerProfileId == profileId
            ? dto
            : dto with { OwnerProfileId = Guid.Empty, PersonalSpaceId = Guid.Empty, SmartRuleJson = null };
    }

    private static ViewClientItemPageDto ToItems(ViewAssetTimelinePageDto page, Guid profileId) =>
        new(page.Items.Where(item => IsPhotoOrVideo(item.MediaKind)).Select(item => ToClient(item, profileId)).ToList(),
            page.NextCursor, page.HasMore);


    /// <summary>Groups a newest-first page into UTC calendar months of the date the timeline sorts by.</summary>
    private static ViewClientTimelinePageDto ToTimeline(ViewAssetTimelinePageDto page, Guid profileId)
    {
        var months = new List<ViewClientTimelineMonthDto>();
        var current = new List<LocalAssetDto>();
        (int Year, int Month)? currentKey = null;
        foreach (var item in page.Items.Where(item => IsPhotoOrVideo(item.MediaKind)))
        {
            var when = (item.CapturedAt ?? item.CreatedAt).UtcDateTime;
            var key = (when.Year, when.Month);
            if (currentKey is not null && currentKey != key)
            {
                months.Add(new ViewClientTimelineMonthDto(currentKey.Value.Year, currentKey.Value.Month, current));
                current = [];
            }

            currentKey = key;
            current.Add(ToClient(item, profileId));
        }

        if (currentKey is not null)
        {
            months.Add(new ViewClientTimelineMonthDto(currentKey.Value.Year, currentKey.Value.Month, current));
        }

        return new ViewClientTimelinePageDto(months, page.NextCursor, page.HasMore);
    }

    private static PlaybackManifestDto BuildPersonalVideoManifest(
        LocalAssetDto video, LocalAssetContentLocation file, string? client)
    {
        var extension = Path.GetExtension(file.FilePath).ToLowerInvariant();
        var directPlay = extension is ".mp4" or ".m4v" or ".mov";
        var warnings = new List<string> { "Personal videos are served as stored; adaptive delivery and conversion are not available." };
        return new PlaybackManifestDto
        {
            AssetId = video.Id,
            Client = string.IsNullOrWhiteSpace(client) ? "native" : client,
            MediaType = "Video",
            SourceExtension = extension,
            RecommendedDelivery = PlaybackDeliveryModes.DirectStream,
            DirectPlaySupported = directPlay,
            DirectStreamUrl = $"{BasePath}/items/{video.Id:D}/original",
            DurationSeconds = video.DurationSeconds,
            Technical = new PlaybackTechnicalInfoDto
            {
                Container = extension.TrimStart('.'),
                VideoCodec = video.VideoCodec,
                Width = video.Width,
                Height = video.Height,
                FrameRate = video.FrameRate,
            },
            SourceBitrateKbps = video.DurationSeconds is > 0 && file.ByteSize > 0
                ? (int)Math.Min(int.MaxValue, file.ByteSize * 8d / video.DurationSeconds.Value / 1000d)
                : null,
            ConversionReason = directPlay ? null : "This container is not usually playable on a phone and personal videos are not converted.",
            Warnings = warnings,
        };
    }
}
