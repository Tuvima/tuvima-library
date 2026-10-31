using MediaEngine.Api.Http;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services.Extras;
using MediaEngine.Api.Services.View;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Contracts.Details;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Models;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Api.Endpoints;

/// <summary>
/// Opens the Extras of a title (Extras spec WP2, WP3): the files behind a card, one file's bytes, and a small picture for a card. An
/// Extra has no Work of its own, so access follows its title: a viewer may open an Extra only when they may open at least one of the
/// title's own files (hidden libraries, Kids levels and the Vault are applied by that check).
/// </summary>
public static class ExtrasEndpoints
{
    private const int MaxFilesToCheck = 12;

    public static IEndpointRouteBuilder MapExtrasEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/extras").WithTags("Extras");

        group.MapGet("/{id:guid}", async (
            Guid id,
            HttpContext context,
            IItemExtraRepository extras,
            IMediaAssetRepository assets,
            CatalogueResourceAuthorizationService authorization,
            CancellationToken ct) =>
        {
            var extra = await extras.GetAsync(id, ct);
            if (extra is null)
            {
                return ApiErrors.NotFound($"No Extra found for '{id}'.");
            }

            var access = await AuthorizeAsync(context, extra, assets, authorization, ApplicationPermissionIds.LibraryRead, ct);
            if (access != CatalogueResourceAccess.Allowed)
            {
                return access == CatalogueResourceAccess.NotFound ? ApiErrors.NotFound($"No Extra found for '{id}'.") : Results.Forbid();
            }

            var siblings = extra.Kind == ItemExtraKinds.Picture
                ? (await extras.ListAllInItemFolderAsync(extra.ItemFolder, ct))
                    .Where(row => row.Origin != ItemExtraOrigins.UserEpisode
                        && row.Kind == ItemExtraKinds.Picture
                        && string.Equals(row.GroupKey ?? string.Empty, extra.GroupKey ?? string.Empty, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(row => row.SortOrder)
                    .ToList()
                : [extra];

            return Results.Ok(new ExtraViewDto
            {
                Id = extra.Id.ToString("D"),
                Kind = extra.Kind == ItemExtraKinds.Picture ? DetailExtraCardKinds.Pictures
                    : extra.Kind == ItemExtraKinds.Clip ? DetailExtraCardKinds.Clip
                    : DetailExtraCardKinds.Document,
                Title = extra.Kind == ItemExtraKinds.Picture ? ExtraTitleOfGroup(extra) : extra.Title,
                Subtitle = null,
                Items = [.. siblings.Select(row => new ExtraViewItemDto
                {
                    Id = row.Id.ToString("D"),
                    Title = row.Title,
                    FileName = Path.GetFileName(row.FilePath),
                    MimeType = ExtraCardBuilder.MimeTypeOf(row.FilePath),
                    IsMissing = !File.Exists(row.FilePath),
                })],
            });
        })
        .WithName("GetExtra")
        .WithSummary("Returns the files behind one Extras card.")
        .Produces<ExtraViewDto>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .RequireClientScope(ClientApiScopes.LibraryRead);

        group.MapMethods("/{id:guid}/file", [HttpMethods.Get, HttpMethods.Head], async (
            Guid id,
            HttpContext context,
            IItemExtraRepository extras,
            IMediaAssetRepository assets,
            CatalogueResourceAuthorizationService authorization,
            CancellationToken ct) =>
        {
            var extra = await extras.GetAsync(id, ct);
            if (extra is null || !File.Exists(extra.FilePath))
            {
                return ApiErrors.NotFound($"No Extra found for '{id}'.");
            }

            var access = await AuthorizeAsync(context, extra, assets, authorization, ApplicationPermissionIds.PlaybackRead, ct);
            if (access != CatalogueResourceAccess.Allowed)
            {
                return access == CatalogueResourceAccess.NotFound ? ApiErrors.NotFound($"No Extra found for '{id}'.") : Results.Forbid();
            }

            // Read in place: the original is only ever read, never moved or changed (Extras spec D7).
            return Results.File(extra.FilePath, ExtraCardBuilder.MimeTypeOf(extra.FilePath), enableRangeProcessing: true);
        })
        .WithName("GetExtraFile")
        .WithSummary("Streams one Extra with HTTP byte-range support.")
        .Produces(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status206PartialContent)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .RequireClientScope(ClientApiScopes.PlaybackRead)
        .RequireRateLimiting("streaming");

        group.MapGet("/{id:guid}/thumbnail", async (
            Guid id,
            HttpContext context,
            IItemExtraRepository extras,
            IMediaAssetRepository assets,
            CatalogueResourceAuthorizationService authorization,
            ViewThumbnailService thumbnails,
            CancellationToken ct) =>
        {
            var extra = await extras.GetAsync(id, ct);
            if (extra is null || !File.Exists(extra.FilePath))
            {
                return StreamEndpoints.CreateArtworkPlaceholderResult();
            }

            var access = await AuthorizeAsync(context, extra, assets, authorization, ApplicationPermissionIds.ArtworkRead, ct);
            if (access != CatalogueResourceAccess.Allowed)
            {
                return access == CatalogueResourceAccess.NotFound ? ApiErrors.NotFound($"No Extra found for '{id}'.") : Results.Forbid();
            }

            var location = new LocalAssetContentLocation(
                extra.Id, extra.Id, Guid.Empty, null, null, null,
                extra.FilePath, ExtraCardBuilder.MimeTypeOf(extra.FilePath), extra.SizeBytes, string.Empty, "Original", null);
            var thumbnail = extra.Kind == ItemExtraKinds.Document && !extra.FilePath.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)
                ? null
                : await thumbnails.GetOrCreateAsync(extra.Id, location, ct);
            return thumbnail is null
                ? StreamEndpoints.CreateArtworkPlaceholderResult()
                : Results.File(thumbnail, "image/jpeg");
        })
        .WithName("GetExtraThumbnail")
        .WithSummary("Returns a small picture for an Extras card: a PDF's first page, a clip's still, or the picture itself.")
        .Produces(StatusCodes.Status200OK)
        .RequireClientScope(ClientApiScopes.ArtworkRead);

        return app;
    }

    private static string ExtraTitleOfGroup(ItemExtra extra)
    {
        var name = (extra.GroupKey ?? string.Empty).Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        return string.IsNullOrWhiteSpace(name) ? "Pictures" : name.Replace('_', ' ').Trim();
    }

    /// <summary>Allowed when the viewer may open at least one of the title's own files.</summary>
    private static async Task<CatalogueResourceAccess> AuthorizeAsync(
        HttpContext context,
        ItemExtra extra,
        IMediaAssetRepository assets,
        CatalogueResourceAuthorizationService authorization,
        ApplicationPermissionId permission,
        CancellationToken ct)
    {
        var files = await assets.ListFilesUnderAsync(extra.ItemFolder, ct);
        var sawDenied = false;
        foreach (var file in files.Take(MaxFilesToCheck))
        {
            var access = await authorization.EvaluateAssetAsync(context, file.AssetId, permission, ct);
            if (access == CatalogueResourceAccess.Allowed)
            {
                return CatalogueResourceAccess.Allowed;
            }

            sawDenied |= access == CatalogueResourceAccess.Denied;
        }

        return sawDenied ? CatalogueResourceAccess.Denied : CatalogueResourceAccess.NotFound;
    }
}
