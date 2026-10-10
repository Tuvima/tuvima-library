using MediaEngine.Api.Http;
using MediaEngine.Api.Security;
using MediaEngine.Contracts.Reading;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;

namespace MediaEngine.Api.Endpoints;

/// <summary>
/// EPUB reader data endpoints — CRUD for bookmarks, highlights, and reading statistics.
/// Personal state is isolated by the authenticated account's active profile.
/// </summary>
public static class ReaderEndpoints
{
    public static IEndpointRouteBuilder MapReaderEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/reader")
                       .WithTags("Reader Data");

        // ── Bookmarks ───────────────────────────────────────────────────────

        group.MapGet("/{assetId:guid}/bookmarks", async (
            Guid assetId,
            HttpContext context,
            IReaderBookmarkRepository repo,
            CancellationToken ct) =>
        {
            var bookmarks = await repo.ListByAssetAsync(ProfileUserId(context), assetId, ct);
            return Results.Ok(bookmarks.Select(MapBookmark).ToList());
        })
        .WithName("ListBookmarks")
        .WithSummary("Lists all bookmarks for the given asset.")
        .Produces<IReadOnlyList<ReaderBookmarkDto>>(StatusCodes.Status200OK)
        .RequireProfileOperation(ApplicationPermissionIds.ProgressRead)
        .RequireCatalogueAssetAccess(ApplicationPermissionIds.ProgressRead);

        group.MapPost("/{assetId:guid}/bookmarks", async (
            Guid assetId,
            HttpContext context,
            CreateReaderBookmarkRequestDto request,
            IReaderBookmarkRepository repo,
            CancellationToken ct) =>
        {
            if (request.CfiPosition?.Length > MaxCfiLength || request.Label?.Length > 500)
            {
                return ApiErrors.BadRequest("The bookmark position or label is too long.");
            }

            var bookmark = new ReaderBookmark
            {
                Id = Guid.NewGuid(),
                UserId = ProfileUserId(context),
                AssetId = assetId,
                ChapterIndex = request.ChapterIndex,
                CfiPosition = request.CfiPosition,
                Label = request.Label,
                CreatedAt = DateTime.UtcNow
            };

            await repo.InsertAsync(bookmark, ct);
            return Results.Created($"/reader/bookmarks/{bookmark.Id}", MapBookmark(bookmark));
        })
        .WithName("CreateBookmark")
        .WithSummary("Creates a bookmark at the specified chapter position.")
        .Produces<ReaderBookmarkDto>(StatusCodes.Status201Created)
        .RequireProfileOperation(ApplicationPermissionIds.ProgressWrite)
        .RequireCatalogueAssetAccess(ApplicationPermissionIds.ProgressWrite);

        group.MapDelete("/bookmarks/{id:guid}", async (
            Guid id,
            HttpContext context,
            IReaderBookmarkRepository repo,
            CatalogueResourceAuthorizationService authorization,
            CancellationToken ct) =>
        {
            var existing = await repo.FindByIdAsync(id, ct);
            if (existing is null)
            {
                return ApiErrors.NotFound($"Bookmark '{id}' not found.");
            }
            if (!string.Equals(existing.UserId, ProfileUserId(context), StringComparison.OrdinalIgnoreCase))
            {
                return ApiErrors.NotFound("Bookmark not found for the active profile.");
            }

            if (await authorization.EvaluateAssetAsync(
                    context, existing.AssetId, ApplicationPermissionIds.ProgressWrite, ct) != CatalogueResourceAccess.Allowed)
            {
                return Results.Forbid();
            }

            await repo.DeleteAsync(id, ct);
            return Results.NoContent();
        })
        .WithName("DeleteBookmark")
        .WithSummary("Deletes a bookmark by ID.")
        .Produces(StatusCodes.Status204NoContent)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .RequireProfileOperation(ApplicationPermissionIds.ProgressWrite);

        // ── Highlights ──────────────────────────────────────────────────────

        group.MapGet("/{assetId:guid}/highlights", async (
            Guid assetId,
            HttpContext context,
            IReaderHighlightRepository repo,
            CancellationToken ct) =>
        {
            var highlights = await repo.ListByAssetAsync(ProfileUserId(context), assetId, ct);
            return Results.Ok(highlights.Select(MapHighlight).ToList());
        })
        .WithName("ListHighlights")
        .WithSummary("Lists all highlights for the given asset.")
        .Produces<IReadOnlyList<ReaderHighlightDto>>(StatusCodes.Status200OK)
        .RequireProfileOperation(ApplicationPermissionIds.ProgressRead)
        .RequireCatalogueAssetAccess(ApplicationPermissionIds.ProgressRead);

        group.MapPost("/{assetId:guid}/highlights", async (
            Guid assetId,
            HttpContext context,
            CreateReaderHighlightRequestDto request,
            IReaderHighlightRepository repo,
            CancellationToken ct) =>
        {
            if (request.CfiRange is { } cfiRange
                && (cfiRange.Length is 0 or > MaxCfiLength || !cfiRange.StartsWith("epubcfi(", StringComparison.Ordinal)
                    || !cfiRange.EndsWith(')')))
            {
                return ApiErrors.BadRequest("CfiRange must be an epubcfi(...) value of at most 4096 characters.");
            }
            if (request.Color is { } requestedColor && !HighlightColor.All.Contains(requestedColor))
            {
                return ApiErrors.BadRequest("Color must be one of the supported highlight colours.");
            }
            if (string.IsNullOrEmpty(request.SelectedText) || request.SelectedText.Length > MaxSelectedTextLength || request.NoteText?.Length > MaxNoteLength)
            {
                return ApiErrors.BadRequest("The selected text is required, and the text and note must not be too long.");
            }

            var highlight = new ReaderHighlight
            {
                Id = Guid.NewGuid(),
                UserId = ProfileUserId(context),
                AssetId = assetId,
                ChapterIndex = request.ChapterIndex,
                StartOffset = request.StartOffset,
                EndOffset = request.EndOffset,
                CfiRange = request.CfiRange,
                SelectedText = request.SelectedText,
                Color = request.Color ?? HighlightColor.Yellow,
                NoteText = request.NoteText,
                CreatedAt = DateTime.UtcNow
            };

            await repo.InsertAsync(highlight, ct);
            return Results.Created($"/reader/highlights/{highlight.Id}", MapHighlight(highlight));
        })
        .WithName("CreateHighlight")
        .WithSummary("Creates a text highlight with optional note and colour.")
        .Produces<ReaderHighlightDto>(StatusCodes.Status201Created)
        .RequireProfileOperation(ApplicationPermissionIds.ProgressWrite)
        .RequireCatalogueAssetAccess(ApplicationPermissionIds.ProgressWrite);

        group.MapPut("/highlights/{id:guid}", async (
            Guid id,
            HttpContext context,
            UpdateReaderHighlightRequestDto request,
            IReaderHighlightRepository repo,
            CatalogueResourceAuthorizationService authorization,
            CancellationToken ct) =>
        {
            if (request.Color is { } requestedColor && !HighlightColor.All.Contains(requestedColor))
            {
                return ApiErrors.BadRequest("Color must be one of the supported highlight colours.");
            }
            if (request.NoteText?.Length > MaxNoteLength)
            {
                return ApiErrors.BadRequest("The note is too long.");
            }

            var existing = await repo.FindByIdAsync(id, ct);
            if (existing is null)
            {
                return ApiErrors.NotFound($"Highlight '{id}' not found.");
            }
            if (!string.Equals(existing.UserId, ProfileUserId(context), StringComparison.OrdinalIgnoreCase))
            {
                return ApiErrors.NotFound("Highlight not found for the active profile.");
            }

            if (await authorization.EvaluateAssetAsync(
                    context, existing.AssetId, ApplicationPermissionIds.ProgressWrite, ct) != CatalogueResourceAccess.Allowed)
            {
                return Results.Forbid();
            }

            await repo.UpdateAsync(id, request.Color, request.NoteText, ct);
            return Results.NoContent();
        })
        .WithName("UpdateHighlight")
        .WithSummary("Updates a highlight's colour and/or note text.")
        .Produces(StatusCodes.Status204NoContent)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .RequireProfileOperation(ApplicationPermissionIds.ProgressWrite);

        group.MapDelete("/highlights/{id:guid}", async (
            Guid id,
            HttpContext context,
            IReaderHighlightRepository repo,
            CatalogueResourceAuthorizationService authorization,
            CancellationToken ct) =>
        {
            var existing = await repo.FindByIdAsync(id, ct);
            if (existing is null)
            {
                return ApiErrors.NotFound($"Highlight '{id}' not found.");
            }
            if (!string.Equals(existing.UserId, ProfileUserId(context), StringComparison.OrdinalIgnoreCase))
            {
                return ApiErrors.NotFound("Highlight not found for the active profile.");
            }

            if (await authorization.EvaluateAssetAsync(
                    context, existing.AssetId, ApplicationPermissionIds.ProgressWrite, ct) != CatalogueResourceAccess.Allowed)
            {
                return Results.Forbid();
            }

            await repo.DeleteAsync(id, ct);
            return Results.NoContent();
        })
        .WithName("DeleteHighlight")
        .WithSummary("Deletes a highlight by ID.")
        .Produces(StatusCodes.Status204NoContent)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .RequireProfileOperation(ApplicationPermissionIds.ProgressWrite);

        // ── Statistics ──────────────────────────────────────────────────────

        group.MapGet("/{assetId:guid}/statistics", async (
            Guid assetId,
            HttpContext context,
            IReaderStatisticsRepository repo,
            CancellationToken ct) =>
        {
            var stats = await repo.GetAsync(ProfileUserId(context), assetId, ct);
            if (stats is null)
            {
                return Results.Ok(new ReaderStatisticsDto
                {
                    Id = Guid.NewGuid(),
                    UserId = ProfileUserId(context),
                    AssetId = assetId
                });
            }

            return Results.Ok(MapStatistics(stats));
        })
        .WithName("GetReadingStatistics")
        .WithSummary("Returns reading statistics for the given asset (or defaults if none exist).")
        .Produces<ReaderStatisticsDto>(StatusCodes.Status200OK)
        .RequireProfileOperation(ApplicationPermissionIds.ProgressRead)
        .RequireCatalogueAssetAccess(ApplicationPermissionIds.ProgressRead);

        group.MapPut("/{assetId:guid}/statistics", async (
            Guid assetId,
            HttpContext context,
            UpdateReaderStatisticsRequestDto request,
            IReaderStatisticsRepository repo,
            CancellationToken ct) =>
        {
            var stats = await repo.GetAsync(ProfileUserId(context), assetId, ct) ?? new ReaderStatistics
            {
                Id = Guid.NewGuid(),
                UserId = ProfileUserId(context),
                AssetId = assetId
            };

            stats.ChaptersRead = request.ChaptersRead;
            stats.TotalReadingTimeSecs = request.TotalReadingTimeSecs;
            stats.WordsRead = request.WordsRead;
            stats.SessionsCount = request.SessionsCount;
            stats.AvgWordsPerMinute = request.AvgWordsPerMinute;
            stats.LastSessionAt = DateTime.UtcNow;

            await repo.UpsertAsync(stats, ct);
            return Results.NoContent();
        })
        .WithName("UpdateReadingStatistics")
        .WithSummary("Upserts reading statistics (auto-saved by the reader every 30 seconds).")
        .Produces(StatusCodes.Status204NoContent)
        .RequireProfileOperation(ApplicationPermissionIds.ProgressWrite)
        .RequireCatalogueAssetAccess(ApplicationPermissionIds.ProgressWrite);

        return app;
    }

    private const int MaxCfiLength = 4096;
    private const int MaxSelectedTextLength = 4000;
    private const int MaxNoteLength = 4000;

    private static string ProfileUserId(HttpContext context) =>
        context.Items.TryGetValue(ProfileOperationAccessFilter.ActiveProfileItemKey, out var value) && value is Guid profileId
            ? profileId.ToString("D")
            : throw new UnauthorizedAccessException("An authorized active profile is required.");

    private static ReaderBookmarkDto MapBookmark(ReaderBookmark bookmark) => new()
    {
        Id = bookmark.Id,
        UserId = bookmark.UserId,
        AssetId = bookmark.AssetId,
        ChapterIndex = bookmark.ChapterIndex,
        CfiPosition = bookmark.CfiPosition,
        Label = bookmark.Label,
        CreatedAt = bookmark.CreatedAt,
    };

    private static ReaderHighlightDto MapHighlight(ReaderHighlight highlight) => new()
    {
        Id = highlight.Id,
        UserId = highlight.UserId,
        AssetId = highlight.AssetId,
        ChapterIndex = highlight.ChapterIndex,
        StartOffset = highlight.StartOffset,
        EndOffset = highlight.EndOffset,
        CfiRange = highlight.CfiRange,
        SelectedText = highlight.SelectedText,
        Color = highlight.Color,
        NoteText = highlight.NoteText,
        CreatedAt = highlight.CreatedAt,
    };

    private static ReaderStatisticsDto MapStatistics(ReaderStatistics statistics) => new()
    {
        Id = statistics.Id,
        UserId = statistics.UserId,
        AssetId = statistics.AssetId,
        ChaptersRead = statistics.ChaptersRead,
        TotalReadingTimeSecs = statistics.TotalReadingTimeSecs,
        WordsRead = statistics.WordsRead,
        SessionsCount = statistics.SessionsCount,
        AvgWordsPerMinute = statistics.AvgWordsPerMinute,
        LastSessionAt = statistics.LastSessionAt,
    };

    private static AlignmentJobDto MapAlignmentJob(AlignmentJob job) => new()
    {
        Id = job.Id,
        EbookAssetId = job.EbookAssetId,
        AudiobookAssetId = job.AudiobookAssetId,
        Status = job.Status,
        AlignmentData = job.AlignmentData,
        ErrorMessage = job.ErrorMessage,
        CreatedAt = job.CreatedAt,
        CompletedAt = job.CompletedAt,
    };
}

