using MediaEngine.Api.Http;
using MediaEngine.Api.Security;
using MediaEngine.Contracts.Metadata;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Enums;

namespace MediaEngine.Api.Endpoints;

public static partial class MetadataEndpoints
{
    private static void MapAssetRenditionEndpoints(RouteGroupBuilder group)
    {
        group.MapGet("/assets/{assetId:guid}/renditions", async (
            Guid assetId, HttpContext http, CatalogueResourceAuthorizationService resources,
            IMediaAssetRepository assets, CancellationToken ct) =>
        {
            if (await resources.EvaluateAssetAsync(http, assetId,
                    ApplicationPermissionIds.MetadataRead, ct) != CatalogueResourceAccess.Allowed)
                return ApiErrors.NotFound("Owned file not found.");
            var selected = await assets.FindByIdAsync(assetId, ct);
            if (selected is null)
                return ApiErrors.NotFound("Owned file not found.");
            var siblings = await assets.ListByEditionAsync(selected.EditionId, ct);
            var visible = new List<MediaAssetRenditionDto>(siblings.Count);
            foreach (var sibling in siblings)
                if (await resources.EvaluateAssetAsync(http, sibling.Id,
                        ApplicationPermissionIds.MetadataRead, ct) == CatalogueResourceAccess.Allowed)
                    visible.Add(ToRenditionDto(sibling));
            return Results.Ok(visible);
        })
        .WithName("GetEditionRenditions")
        .WithSummary("Lists authorized alternate playback assets belonging to the same Edition.")
        .Produces<IReadOnlyList<MediaAssetRenditionDto>>()
        .RequireAdministratorOrApplication(ApplicationPermissionIds.MetadataRead)
        .RequireAnyCatalogueEntityAccess(ApplicationPermissionIds.MetadataRead);

        group.MapPut("/assets/{assetId:guid}/rendition", async (
            Guid assetId, UpdateMediaAssetRenditionRequestDto request, HttpContext http,
            CatalogueResourceAuthorizationService resources, IMediaAssetRepository assets,
            CancellationToken ct) =>
        {
            if (await resources.EvaluateAssetAsync(http, assetId,
                    ApplicationPermissionIds.MetadataWrite, ct) != CatalogueResourceAccess.Allowed)
                return ApiErrors.NotFound("Owned file not found.");
            if (!Enum.TryParse<RenditionPurpose>(request.Purpose, true, out var purpose)
                || !Enum.IsDefined(purpose))
                return ApiErrors.BadRequest("Purpose must be Original, Mobile, Offline, Compatibility, or Other.");
            if (request.Width is <= 0 || request.Height is <= 0 || request.BitrateBitsPerSecond is <= 0)
                return ApiErrors.BadRequest("Rendition dimensions and bitrate must be positive when supplied.");
            if (request.DerivedFromAssetId == assetId)
                return ApiErrors.BadRequest("A rendition cannot derive from itself.");
            if (purpose == RenditionPurpose.Original && request.DerivedFromAssetId is not null)
                return ApiErrors.BadRequest("An Original rendition cannot have a derived-from asset.");
            if (!ValidRenditionText(request))
                return ApiErrors.BadRequest("Rendition text fields must be 256 characters or fewer.");
            if (request.DerivedFromAssetId is Guid sourceId
                && await resources.EvaluateAssetAsync(http, sourceId,
                    ApplicationPermissionIds.MetadataWrite, ct) != CatalogueResourceAccess.Allowed)
                return ApiErrors.NotFound("Source owned file not found.");

            var asset = await assets.FindByIdAsync(assetId, ct);
            if (asset is null)
                return ApiErrors.NotFound("Owned file not found.");
            asset.RenditionPurpose = purpose;
            asset.DerivedFromAssetId = request.DerivedFromAssetId;
            asset.EncoderProfileVersion = NormalizeRenditionText(request.EncoderProfileVersion);
            asset.Width = request.Width;
            asset.Height = request.Height;
            asset.BitrateBitsPerSecond = request.BitrateBitsPerSecond;
            asset.VideoCodec = NormalizeRenditionText(request.VideoCodec);
            asset.AudioCodec = NormalizeRenditionText(request.AudioCodec);
            asset.DynamicRange = NormalizeRenditionText(request.DynamicRange);
            asset.AudioLayout = NormalizeRenditionText(request.AudioLayout);
            asset.RenditionGeneratedAt = request.GeneratedAt;
            asset.SourceFingerprint = NormalizeRenditionText(request.SourceFingerprint);
            if (!await assets.UpdateRenditionAsync(asset, ct))
                return ApiErrors.Conflict("The rendition source must exist and cannot form a cycle.");
            return Results.Ok(ToRenditionDto(asset));
        })
        .WithName("UpdateAssetRendition")
        .WithSummary("Updates delivery metadata while preserving Work and Edition identity.")
        .Produces<MediaAssetRenditionDto>()
        .ProducesProblem(StatusCodes.Status409Conflict)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.MetadataWrite)
        .RequireAnyCatalogueEntityAccess(ApplicationPermissionIds.MetadataWrite);
    }

    private static MediaAssetRenditionDto ToRenditionDto(Domain.Aggregates.MediaAsset asset) => new(
        asset.Id, asset.EditionId, asset.RenditionPurpose.ToString(), asset.DerivedFromAssetId,
        asset.EncoderProfileVersion, asset.Width, asset.Height, asset.BitrateBitsPerSecond,
        asset.VideoCodec, asset.AudioCodec, asset.DynamicRange, asset.AudioLayout,
        asset.RenditionGeneratedAt, asset.SourceFingerprint);

    private static bool ValidRenditionText(UpdateMediaAssetRenditionRequestDto request) =>
        new[] { request.EncoderProfileVersion, request.VideoCodec, request.AudioCodec,
            request.DynamicRange, request.AudioLayout, request.SourceFingerprint }
        .All(value => value is null || value.Trim().Length <= 256);

    private static string? NormalizeRenditionText(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
