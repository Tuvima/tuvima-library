using System.Security.Claims;
using MediaEngine.Api.Http;
using MediaEngine.Api.Security;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Contracts.Reading;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;

namespace MediaEngine.Api.Endpoints;

/// <summary>
/// EPUB reader content endpoints — serves chapters, embedded resources, TOC,
/// search results, and metadata from EPUB files in the library.
///
/// All resource URLs in chapter HTML are rewritten to point at the
/// <c>/read/{assetId}/resource/{path}</c> endpoint so images, CSS,
/// and fonts render correctly in the reader iframe.
/// </summary>
public static class ReadEndpoints
{
    public static IEndpointRouteBuilder MapReadEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/read")
                       .WithTags("Reader");

        // ── Book metadata ────────────────────────────────────────────────

        group.MapGet("/{assetId:guid}/metadata", async (
            Guid assetId,
            IMediaAssetRepository assetRepo,
            IEpubContentService epubService,
            CancellationToken ct) =>
        {
            var asset = await assetRepo.FindByIdAsync(assetId, ct);
            if (asset is null)
            {
                return ApiErrors.NotFound($"Asset '{assetId}' not found.");
            }

            if (!File.Exists(asset.FilePathRoot))
            {
                return Results.Problem(
                    detail: "File not found on disk.",
                    statusCode: StatusCodes.Status500InternalServerError);
            }

            var metadata = await epubService.GetBookMetadataAsync(asset.FilePathRoot, ct);
            return Results.Ok(new EpubBookMetadataDto(
                metadata.Title,
                metadata.Author,
                metadata.ChapterCount,
                metadata.WordCount,
                metadata.Language,
                metadata.HasCoverImage)
            { ChapterWordCounts = metadata.ChapterWordCounts });
        })
        .WithName("GetBookMetadata")
        .WithSummary("Returns EPUB book metadata (title, author, chapter count, word count).")
        .Produces<EpubBookMetadataDto>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .RequireClientScope(ClientApiScopes.LibraryRead)
        .RequireCatalogueAssetAccess(ApplicationPermissionIds.LibraryRead);

        // ── Table of Contents ────────────────────────────────────────────

        group.MapGet("/{assetId:guid}/toc", async (
            Guid assetId,
            IMediaAssetRepository assetRepo,
            IEpubContentService epubService,
            CancellationToken ct) =>
        {
            var asset = await assetRepo.FindByIdAsync(assetId, ct);
            if (asset is null)
            {
                return ApiErrors.NotFound($"Asset '{assetId}' not found.");
            }

            if (!File.Exists(asset.FilePathRoot))
            {
                return Results.Problem(
                    detail: "File not found on disk.",
                    statusCode: StatusCodes.Status500InternalServerError);
            }

            var toc = await epubService.GetTableOfContentsAsync(asset.FilePathRoot, ct);
            return Results.Ok(toc.Select(MapTocEntry).ToList());
        })
        .WithName("GetTableOfContents")
        .WithSummary("Returns the EPUB Table of Contents as a hierarchical tree.")
        .Produces<List<EpubTocEntryDto>>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .RequireClientScope(ClientApiScopes.LibraryRead)
        .RequireCatalogueAssetAccess(ApplicationPermissionIds.LibraryRead);

        // ── Chapter content ──────────────────────────────────────────────

        group.MapGet("/{assetId:guid}/chapter/{index:int}", async (
            Guid assetId,
            int index,
            HttpContext ctx,
            IMediaAssetRepository assetRepo,
            IEpubContentService epubService,
            CancellationToken ct) =>
        {
            var asset = await assetRepo.FindByIdAsync(assetId, ct);
            if (asset is null)
            {
                return ApiErrors.NotFound($"Asset '{assetId}' not found.");
            }

            if (!File.Exists(asset.FilePathRoot))
            {
                return Results.Problem(
                    detail: "File not found on disk.",
                    statusCode: StatusCodes.Status500InternalServerError);
            }

            // Build resource base URL relative to this request.
            var scheme = ctx.Request.Scheme;
            var host = ctx.Request.Host;
            var resourceBaseUrl = $"{scheme}://{host}/read/{assetId}/resource/";

            var chapter = await epubService.GetChapterContentAsync(
                asset.FilePathRoot, index, resourceBaseUrl, ct);

            if (chapter is null)
            {
                return ApiErrors.NotFound($"Chapter {index} not found.");
            }

            return Results.Ok(new EpubChapterContentDto(
                chapter.Index,
                chapter.Title,
                chapter.HtmlContent,
                chapter.WordCount));
        })
        .WithName("GetChapterContent")
        .WithSummary("Returns chapter HTML with resource URLs rewritten for the reader.")
        .Produces<EpubChapterContentDto>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .RequireClientScope(ClientApiScopes.LibraryRead)
        .RequireCatalogueAssetAccess(ApplicationPermissionIds.LibraryRead);

        // ── Embedded resources (images, CSS, fonts) ──────────────────────

        group.MapGet("/{assetId:guid}/resource/{**path}", async (
            Guid assetId,
            string path,
            IMediaAssetRepository assetRepo,
            IEpubContentService epubService,
            CancellationToken ct) =>
        {
            var asset = await assetRepo.FindByIdAsync(assetId, ct);
            if (asset is null)
            {
                return ApiErrors.NotFound($"Asset '{assetId}' not found.");
            }

            if (!File.Exists(asset.FilePathRoot))
            {
                return Results.Problem(
                    detail: "File not found on disk.",
                    statusCode: StatusCodes.Status500InternalServerError);
            }

            var resource = await epubService.GetResourceAsync(asset.FilePathRoot, path, ct);
            if (resource is null)
            {
                return ApiErrors.NotFound($"Resource '{path}' not found in EPUB.");
            }

            return Results.File(
                resource.Data,
                resource.ContentType,
                resource.FileName,
                enableRangeProcessing: false);
        })
        .WithName("GetEpubResource")
        .WithSummary("Serves an embedded EPUB resource (image, CSS, font).")
        .Produces(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .RequireClientScope(ClientApiScopes.LibraryRead)
        .RequireCatalogueAssetAccess(ApplicationPermissionIds.LibraryRead)
        .RequireRateLimiting("streaming");

        // ── Full-text search ─────────────────────────────────────────────

        group.MapGet("/{assetId:guid}/search", async (
            Guid assetId,
            string? q,
            IMediaAssetRepository assetRepo,
            IEpubContentService epubService,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(q) || q.Length < 2)
            {
                return ApiErrors.BadRequest("Search query must be at least 2 characters.");
            }

            var asset = await assetRepo.FindByIdAsync(assetId, ct);
            if (asset is null)
            {
                return ApiErrors.NotFound($"Asset '{assetId}' not found.");
            }

            if (!File.Exists(asset.FilePathRoot))
            {
                return Results.Problem(
                    detail: "File not found on disk.",
                    statusCode: StatusCodes.Status500InternalServerError);
            }

            var hits = await epubService.SearchAsync(asset.FilePathRoot, q, ct);
            return Results.Ok(hits.Select(hit => new EpubSearchHitDto(
                hit.ChapterIndex,
                hit.ChapterTitle,
                hit.ContextSnippet,
                hit.MatchOffset)).ToList());
        })
        .WithName("SearchEpub")
        .WithSummary("Full-text search across all chapters (case-insensitive, min 2 chars).")
        .Produces<List<EpubSearchHitDto>>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .RequireClientScope(ClientApiScopes.LibraryRead)
        .RequireCatalogueAssetAccess(ApplicationPermissionIds.LibraryRead);

        // ── Resolve Work ID to Asset ID ──────────────────────────────────

        group.MapGet("/resolve/{workId:guid}", async (
            Guid workId,
            ClaimsPrincipal user,
            HttpContext context,
            CatalogueResourceAuthorizationService authorization,
            CancellationToken ct) =>
        {
            Guid? profileId = Guid.TryParse(user.FindFirstValue(TuvimaClaimTypes.ActiveProfileId), out var profile) ? profile : null;
            var assetId = await authorization.FindAuthorizedAssetForWorkAsync(
                context, workId, profileId, ApplicationPermissionIds.LibraryRead, ct);
            if (assetId is null)
            {
                return ApiErrors.NotFound($"No readable asset found for Work '{workId}'.");
            }

            return Results.Ok(new ResolveWorkToAssetResponse(assetId: assetId.Value));
        })
        .WithName("ResolveWorkToAsset")
        .WithSummary("Resolves a Work ID to its primary MediaAsset ID for reading.")
        .Produces<ResolveWorkToAssetResponse>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .RequireClientScope(ClientApiScopes.LibraryRead);

        return app;
    }

    private static EpubTocEntryDto MapTocEntry(MediaEngine.Domain.Models.EpubTocEntry entry) => new()
    {
        Title = entry.Title,
        ChapterIndex = entry.ChapterIndex,
        FragmentId = entry.FragmentId,
        Children = entry.Children.Select(MapTocEntry).ToList(),
    };
}
