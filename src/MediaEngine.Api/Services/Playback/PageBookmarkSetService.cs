using MediaEngine.Api.Endpoints;
using MediaEngine.Api.Security;
using MediaEngine.Contracts.Playback;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Services;
using MediaEngine.Storage.Playback;

namespace MediaEngine.Api.Services.Playback;

/// <summary>
/// The editor's bookmark files for comics and PDFs (PDF and comic reader spec D14, Global bookmarks stage 3): the same compare and export as for
/// films and books, with pages instead of times. Pages are only comparable inside the same file, so a bookmark file made on another copy of the
/// title is shown but its pages are not placed. Closeness comes from the <c>page</c> block of <c>config/bookmarks.json</c>.
/// </summary>
public sealed class PageBookmarkSetService(
    PlayerCatalogueScope scope,
    BookmarkRecognitionRepository recognition,
    IVaultLibraryRepository vault,
    BookmarkClosenessProvider closeness,
    IMediaAssetRepository assets,
    TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <summary>True when the file opens in the page reader: a comic or a PDF.</summary>
    public async Task<bool> IsPageFileAsync(Guid assetId, CancellationToken ct)
    {
        var asset = await assets.FindByIdAsync(assetId, ct);
        return asset is not null && ReadEndpoints.ReaderFormatOf(asset.FilePathRoot) is "comic" or "pdf";
    }

    /// <summary>Compares a bookmark file with the title's Everyone's page bookmarks. Changes nothing. The caller has checked for an administrator.</summary>
    public async Task<BookmarkSetCompareDto> CompareWithEveryoneAsync(
        Guid assetId, BookmarkSetRequestDto request, IReadOnlyList<EditorBookmarkRow> everyone, CancellationToken ct = default)
    {
        if (await vault.IsInVaultAsync(assetId, ct))
        {
            throw new PlayerResourceDeniedException();
        }

        var content = BookmarkPackArchive.Read(Decode(request.ContentBase64));
        if (content.Bookmarks.Any(bookmark => PageLocator.TryParse(bookmark.Locator) is null))
        {
            throw new BookmarkPackInvalidException("This file is not for a comic or a PDF.");
        }

        var local = await recognition.GetRecognitionAsync(assetId, ct) ?? throw new PlayerResourceDeniedException();
        var ids = BookmarkPackMatcher.Compare(
            new BookmarkPackTitle(content.Title.Ids, null, null), new BookmarkPackTitle(local.Ids, null, null));
        var sameFile = string.IsNullOrWhiteSpace(content.Title.Edition) || string.IsNullOrWhiteSpace(local.ContentHash)
            || string.Equals(content.Title.Edition, EditionKey(local.ContentHash), StringComparison.OrdinalIgnoreCase);
        var kind = ids.Kind == BookmarkPackMatchKind.WrongTitle ? BookmarkPackMatchKind.WrongTitle
            : sameFile ? BookmarkPackMatchKind.Match : BookmarkPackMatchKind.DifferentCut;
        var reason = kind switch
        {
            BookmarkPackMatchKind.Match => "This file is for this title.",
            BookmarkPackMatchKind.DifferentCut => "This file was made on another copy of this title, so its pages may not line up. Nothing is placed.",
            _ => ids.Reason,
        };

        var isAdmin = await scope.IsServerAdministratorAsync(ct);
        var existing = everyone.Where(row => row.PositionKind == BookmarkPooling.PageKind).ToList();
        var placed = kind == BookmarkPackMatchKind.Match ? content.Bookmarks : [];
        var limits = closeness.Current.For(BookmarkPositionKind.Page);
        var rows = new List<BookmarkSetCompareRowDto>(placed.Count);
        for (var index = 0; index < placed.Count; index++)
        {
            var incoming = placed[index];
            var page = PageLocator.TryParse(incoming.Locator)!.Value;
            var best = existing
                .Select(row => (Row: row, Gap: (int)Math.Abs(row.PositionSeconds - page)))
                .Select(item => (item.Row, item.Gap, Closeness: BookmarkClosenessRules.Classify(limits, item.Gap)))
                .OrderBy(item => item.Closeness).ThenBy(item => item.Gap)
                .Cast<(EditorBookmarkRow Row, int Gap, BookmarkCloseness Closeness)?>().FirstOrDefault();
            var label = best?.Closeness ?? BookmarkCloseness.New;
            rows.Add(new BookmarkSetCompareRowDto
            {
                Index = index,
                Incoming = new BookmarkSetItemDto
                {
                    PositionSeconds = page,
                    Locator = PageLocator.Format(page),
                    Note = incoming.Note,
                    Description = incoming.Description,
                },
                Yours = label == BookmarkCloseness.New || best is null ? null : new BookmarkSetExistingDto
                {
                    Id = best.Value.Row.Id,
                    Name = best.Value.Row.Name,
                    IsGlobal = best.Value.Row.IsGlobal,
                    PositionSeconds = best.Value.Row.PositionSeconds,
                    Locator = PageLocator.Format((int)best.Value.Row.PositionSeconds),
                },
                GapPages = label == BookmarkCloseness.New ? null : best?.Gap,
                Closeness = ToLabel(label),
                DefaultChoice = ToChoice(BookmarkClosenessRules.DefaultChoice(label)),
            });
        }

        return new BookmarkSetCompareDto
        {
            Name = content.Name,
            Author = content.Author,
            BookmarkCount = content.Bookmarks.Count,
            Match = kind switch
            {
                BookmarkPackMatchKind.Match => BookmarkPackMatchResults.Match,
                BookmarkPackMatchKind.DifferentCut => BookmarkPackMatchResults.DifferentCut,
                _ => BookmarkPackMatchResults.WrongTitle,
            },
            Reason = reason,
            CanAlignByChapter = false,
            RowsReady = kind == BookmarkPackMatchKind.Match,
            SkippedCount = kind == BookmarkPackMatchKind.Match ? 0 : content.Bookmarks.Count,
            AppliesToGlobal = isAdmin,
            Rows = rows,
            GlobalCount = existing.Count(row => row.IsGlobal),
        };
    }

    /// <summary>The title's Everyone's page bookmarks as one bookmark file (pages and names, never anyone's personal bookmarks).</summary>
    public async Task<BookmarkPackFileDto> ExportEveryoneAsync(Guid assetId, IReadOnlyList<EditorBookmarkRow> everyone, CancellationToken ct = default)
    {
        if (await vault.IsInVaultAsync(assetId, ct))
        {
            throw new PlayerResourceDeniedException();
        }

        var chosen = everyone.Where(row => row.PositionKind == BookmarkPooling.PageKind)
            .OrderBy(row => row.PositionSeconds).ThenBy(row => row.CreatedAt)
            .Take(BookmarkPackLimits.MaximumBookmarks).ToList();
        if (chosen.Count == 0)
        {
            throw new BookmarkPackInvalidException("This title has no Everyone's bookmarks to export yet.");
        }

        var local = await recognition.GetRecognitionAsync(assetId, ct);
        if (local is null || local.Ids.Count == 0)
        {
            throw new BookmarkPackInvalidException("This title has no ID that other libraries can recognise it by yet, so a file cannot be made for it.");
        }

        var items = chosen.Select(row =>
            new BookmarkPackBookmark(row.PositionSeconds, null, null, null, string.IsNullOrWhiteSpace(row.Name) ? null : row.Name, null, PageLocator.Format((int)row.PositionSeconds)))
            .ToList();
        var content = new BookmarkPackContent(
            BookmarkSetService.ExportedSetName, null,
            new BookmarkPackTitle(local.Ids, null, EditionKey(local.ContentHash)),
            items, BookmarkPackArchive.Fingerprint(BookmarkSetService.ExportedSetName, null, items));
        return new BookmarkPackFileDto
        {
            FileName = BookmarkSetService.ExportedFileName,
            ContentBase64 = Convert.ToBase64String(BookmarkPackArchive.Write(content, _time.GetUtcNow())),
            BookmarkCount = items.Count,
        };
    }

    private static string? EditionKey(string? contentHash) => string.IsNullOrWhiteSpace(contentHash) ? null : "file:" + contentHash.Trim().ToLowerInvariant();

    private static string ToLabel(BookmarkCloseness closeness) => closeness switch
    {
        BookmarkCloseness.SameSpot => BookmarkClosenessLabels.SameSpot,
        BookmarkCloseness.Close => BookmarkClosenessLabels.Close,
        BookmarkCloseness.Far => BookmarkClosenessLabels.Far,
        _ => BookmarkClosenessLabels.New,
    };

    private static string ToChoice(BookmarkRowChoice choice) => choice switch
    {
        BookmarkRowChoice.KeepYours => BookmarkRowChoices.Yours,
        BookmarkRowChoice.Skip => BookmarkRowChoices.Skip,
        _ => BookmarkRowChoices.Incoming,
    };

    private static byte[] Decode(string? contentBase64)
    {
        if (string.IsNullOrWhiteSpace(contentBase64) || contentBase64.Length > BookmarkPackLimits.MaximumFileBytes * 4L / 3 + 8)
        {
            throw new BookmarkPackInvalidException("This is not a bookmark file, or it is too large.");
        }

        try
        {
            return Convert.FromBase64String(contentBase64);
        }
        catch (FormatException)
        {
            throw new BookmarkPackInvalidException("This is not a bookmark file.");
        }
    }
}
