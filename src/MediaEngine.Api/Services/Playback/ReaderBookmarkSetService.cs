using MediaEngine.Api.Security;
using MediaEngine.Contracts.Playback;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Services;
using MediaEngine.Storage.Playback;

namespace MediaEngine.Api.Services.Playback;

/// <summary>
/// Find bookmarks for books (Global bookmarks spec D6 to D10, D12, D14): the same compare, apply and export as for films and
/// audiobooks, with places in the text instead of times. Two places can only be compared inside the same file (same paragraph is the
/// same spot, same chapter is close, up to two chapters away is far); a bookmark file made on another edition of the book is placed by
/// chapter name instead and lands at the start of that chapter. The compare rows reuse the film wire shapes.
/// </summary>
public sealed class ReaderBookmarkSetService
{
    private readonly PlayerCatalogueScope _scope;
    private readonly IReaderBookmarkRepository _bookmarks;
    private readonly BookmarkRecognitionRepository _recognition;
    private readonly IVaultLibraryRepository _vault;
    private readonly BookmarkClosenessProvider _closeness;
    private readonly TimeProvider _time;

    public ReaderBookmarkSetService(
        PlayerCatalogueScope scope,
        IReaderBookmarkRepository bookmarks,
        BookmarkRecognitionRepository recognition,
        IVaultLibraryRepository vault,
        BookmarkClosenessProvider closeness,
        TimeProvider? time = null)
    {
        _scope = scope;
        _bookmarks = bookmarks;
        _recognition = recognition;
        _vault = vault;
        _closeness = closeness;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>The reader location of the start of a chapter, where a bookmark placed by chapter name lands.</summary>
    public static string ChapterStartLocator(int chapter) => $"epubcfi(/6/{(chapter + 1) * 2}!)";

    public async Task<BookmarkSetCompareDto> CompareAsync(Guid profileId, Guid assetId, BookmarkSetRequestDto request, CancellationToken ct = default) =>
        (await PrepareAsync(profileId, assetId, request, ct)).Compare;

    public async Task<BookmarkSetApplyResultDto> ApplyAsync(Guid profileId, Guid assetId, BookmarkSetApplyRequestDto request, CancellationToken ct = default)
    {
        var prepared = await PrepareAsync(profileId, assetId, request.Set, ct);
        if (prepared.Compare.Match == BookmarkPackMatchResults.WrongTitle)
        {
            throw new BookmarkPackInvalidException(prepared.Compare.Reason);
        }

        if (!prepared.Compare.RowsReady)
        {
            throw new BookmarkPackInvalidException("This file is for a different edition. Place it by chapter name, or skip it.");
        }

        var isAdmin = prepared.Compare.AppliesToGlobal;
        var replaceWhole = request.ReplaceGlobalSet && isAdmin;
        var choices = request.Choices.GroupBy(choice => choice.Index).ToDictionary(group => group.Key, group => group.Last().Choice);
        var adds = new List<ReaderSetBookmark>();
        var deletes = new HashSet<Guid>();
        var kept = 0;
        var skipped = 0;
        foreach (var row in prepared.Compare.Rows)
        {
            var choice = choices.TryGetValue(row.Index, out var chosen) && chosen is BookmarkRowChoices.Incoming or BookmarkRowChoices.Yours or BookmarkRowChoices.Skip
                ? chosen
                : row.DefaultChoice;
            if (replaceWhole && choice != BookmarkRowChoices.Skip)
            {
                choice = BookmarkRowChoices.Incoming;
            }

            if (choice == BookmarkRowChoices.Skip || (choice == BookmarkRowChoices.Yours && row.Yours is null))
            {
                skipped++;
                continue;
            }

            if (choice == BookmarkRowChoices.Yours)
            {
                kept++;
                continue;
            }

            var spot = ReaderLocation.TryParse(row.Incoming.Locator);
            if (spot is null || row.Incoming.Locator is null)
            {
                skipped++;
                continue;
            }

            var label = row.Incoming.Note is { Length: > AudiobookBookmarkNotePolicy.MaximumLength } longNote
                ? longNote[..AudiobookBookmarkNotePolicy.MaximumLength]
                : row.Incoming.Note;
            adds.Add(new ReaderSetBookmark(row.Incoming.Locator, spot.Chapter, label));
            // The nearest bookmark is replaced when the person may replace it: their own, or a Global one for an administrator.
            if (row.Yours is { } yours && (!yours.IsGlobal || isAdmin))
            {
                deletes.Add(yours.Id);
            }
        }

        var outcome = await _bookmarks.ApplySetAsync(profileId, assetId, adds, deletes, addAsGlobal: isAdmin, replaceGlobalSet: replaceWhole, ct);
        return new BookmarkSetApplyResultDto
        {
            Added = outcome.Added,
            Replaced = outcome.Replaced,
            Kept = kept,
            Skipped = skipped,
            AddedToGlobal = isAdmin,
        };
    }

    /// <summary>A file of this book's Global bookmarks only. Administrators only; never a Vault book.</summary>
    public async Task<BookmarkPackFileDto> ExportGlobalAsync(Guid profileId, Guid assetId, ExportGlobalBookmarksRequestDto request, CancellationToken ct = default)
    {
        if (await _vault.IsInVaultAsync(assetId, ct))
        {
            throw new PlayerResourceDeniedException();
        }

        if (!await _scope.IsServerAdministratorAsync(ct))
        {
            throw new PlayerBookmarkAdminDeniedException();
        }

        var chosen = (await _bookmarks.ListByAssetAsync(profileId.ToString("D"), assetId, ct))
            .Where(bookmark => bookmark.IsGlobal && ReaderLocation.TryParse(bookmark.CfiPosition) is not null)
            .OrderBy(bookmark => bookmark.ChapterIndex)
            .ThenBy(bookmark => bookmark.CreatedAt)
            .Take(BookmarkPackLimits.MaximumBookmarks)
            .ToList();
        if (chosen.Count == 0)
        {
            throw new BookmarkPackInvalidException("This book has no Global bookmarks to export yet.");
        }

        var recognition = await _recognition.GetRecognitionAsync(assetId, ct);
        if (recognition is null || recognition.Ids.Count == 0)
        {
            throw new BookmarkPackInvalidException("This book has no ID that other libraries can recognise it by yet, so a file cannot be made for it.");
        }

        var titles = request.Chapters.Where(chapter => !string.IsNullOrWhiteSpace(chapter.Title))
            .GroupBy(chapter => (int)chapter.StartSeconds).ToDictionary(group => group.Key, group => group.First().Title.Trim());
        var items = chosen.Select(bookmark =>
        {
            var chapter = ReaderLocation.TryParse(bookmark.CfiPosition)!.Chapter;
            return new BookmarkPackBookmark(
                chapter, null,
                titles.TryGetValue(chapter, out var title) ? title : null,
                chapter,
                bookmark.Label, null, bookmark.CfiPosition);
        }).ToList();

        var content = new BookmarkPackContent(
            BookmarkSetService.ExportedSetName, null,
            new BookmarkPackTitle(recognition.Ids, null, EditionKey(recognition.ContentHash)),
            items, BookmarkPackArchive.Fingerprint(BookmarkSetService.ExportedSetName, null, items));
        return new BookmarkPackFileDto
        {
            FileName = "global-bookmarks.zip",
            ContentBase64 = Convert.ToBase64String(BookmarkPackArchive.Write(content, _time.GetUtcNow())),
            BookmarkCount = items.Count,
        };
    }

    private sealed record Prepared(BookmarkSetCompareDto Compare);

    private async Task<Prepared> PrepareAsync(Guid profileId, Guid assetId, BookmarkSetRequestDto request, CancellationToken ct)
    {
        if (await _vault.IsInVaultAsync(assetId, ct))
        {
            throw new PlayerResourceDeniedException();
        }

        var content = BookmarkPackArchive.Read(Decode(request.ContentBase64));
        if (content.Bookmarks.Any(bookmark => bookmark.Locator is null))
        {
            throw new BookmarkPackInvalidException("This file is for a film or an audiobook, not a book.");
        }

        var local = await _recognition.GetRecognitionAsync(assetId, ct) ?? throw new PlayerResourceDeniedException();
        var ids = BookmarkPackMatcher.Compare(
            new BookmarkPackTitle(content.Title.Ids, null, null), new BookmarkPackTitle(local.Ids, null, null));
        var sameFile = string.IsNullOrWhiteSpace(content.Title.Edition) || string.IsNullOrWhiteSpace(local.ContentHash)
            || string.Equals(content.Title.Edition, EditionKey(local.ContentHash), StringComparison.OrdinalIgnoreCase);
        var kind = ids.Kind == BookmarkPackMatchKind.WrongTitle ? BookmarkPackMatchKind.WrongTitle
            : sameFile ? BookmarkPackMatchKind.Match : BookmarkPackMatchKind.DifferentCut;
        var reason = kind switch
        {
            BookmarkPackMatchKind.Match => "This file is for this book.",
            BookmarkPackMatchKind.DifferentCut => "This file was made on another edition of the book, so its places cannot be used as they are. They can be placed by chapter name.",
            _ => ids.Reason,
        };

        var isAdmin = await _scope.IsServerAdministratorAsync(ct);
        var existing = (await _bookmarks.ListByAssetAsync(profileId.ToString("D"), assetId, ct))
            .Select(bookmark => (Bookmark: bookmark, Spot: ReaderLocation.TryParse(bookmark.CfiPosition)))
            .Where(item => item.Spot is not null)
            .ToList();
        var canAlign = kind == BookmarkPackMatchKind.DifferentCut
            && request.Chapters.Count > 0
            && content.Bookmarks.Any(bookmark => !string.IsNullOrWhiteSpace(bookmark.Chapter));

        var placed = new List<BookmarkPackBookmark>();
        var skipped = 0;
        var ready = false;
        if (kind == BookmarkPackMatchKind.Match)
        {
            placed.AddRange(content.Bookmarks.Where(bookmark => ReaderLocation.TryParse(bookmark.Locator) is not null));
            skipped = content.Bookmarks.Count - placed.Count;
            ready = true;
        }
        else if (kind == BookmarkPackMatchKind.DifferentCut && request.Mode == BookmarkPackImportModes.AlignByChapter)
        {
            var chapters = request.Chapters.Where(chapter => !string.IsNullOrWhiteSpace(chapter.Title) && chapter.StartSeconds >= 0)
                .Select(chapter => (Key: NormalizeTitle(chapter.Title), Index: (int)chapter.StartSeconds)).ToList();
            foreach (var bookmark in content.Bookmarks)
            {
                var key = NormalizeTitle(bookmark.Chapter);
                var target = key is null
                    ? ((string Key, int Index)?)null
                    : chapters.Where(chapter => chapter.Key == key).OrderBy(chapter => Math.Abs(chapter.Index - (int)(bookmark.ChapterStart ?? 0))).Cast<(string Key, int Index)?>().FirstOrDefault();
                if (target is null)
                {
                    skipped++;
                    continue;
                }

                placed.Add(bookmark with { Locator = ChapterStartLocator(target.Value.Index) });
            }

            ready = true;
        }

        var limits = _closeness.Current.For(BookmarkPositionKind.Text);
        var rows = new List<BookmarkSetCompareRowDto>(placed.Count);
        for (var index = 0; index < placed.Count; index++)
        {
            var incoming = placed[index];
            var spot = ReaderLocation.TryParse(incoming.Locator)!;
            var best = existing
                .Select(item => (item.Bookmark, Closeness: ReaderLocation.Classify(limits, spot, item.Spot!), Gap: Math.Abs(item.Spot!.Chapter - spot.Chapter)))
                .OrderBy(item => item.Closeness).ThenBy(item => item.Gap)
                .Cast<(ReaderBookmark Bookmark, BookmarkCloseness Closeness, int Gap)?>().FirstOrDefault();
            var closeness = best?.Closeness ?? BookmarkCloseness.New;
            rows.Add(new BookmarkSetCompareRowDto
            {
                Index = index,
                Incoming = new BookmarkSetItemDto
                {
                    PositionSeconds = spot.Chapter,
                    Locator = incoming.Locator,
                    ChapterTitle = incoming.Chapter,
                    Note = incoming.Note,
                    Description = incoming.Description,
                },
                Yours = closeness == BookmarkCloseness.New || best is null ? null : new BookmarkSetExistingDto
                {
                    Id = best.Value.Bookmark.Id,
                    Name = best.Value.Bookmark.Label,
                    IsGlobal = best.Value.Bookmark.IsGlobal,
                    PositionSeconds = best.Value.Bookmark.ChapterIndex,
                    Locator = best.Value.Bookmark.CfiPosition,
                },
                GapChapters = closeness == BookmarkCloseness.New ? null : best?.Gap,
                Closeness = ToLabel(closeness),
                DefaultChoice = ToChoice(BookmarkClosenessRules.DefaultChoice(closeness)),
            });
        }

        return new Prepared(new BookmarkSetCompareDto
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
            CanAlignByChapter = canAlign,
            RowsReady = ready,
            SkippedCount = skipped,
            AppliesToGlobal = isAdmin,
            Rows = rows,
            GlobalCount = existing.Count(item => item.Bookmark.IsGlobal),
        });
    }

    private static string? EditionKey(string? contentHash) => string.IsNullOrWhiteSpace(contentHash) ? null : "file:" + contentHash.Trim().ToLowerInvariant();

    private static string? NormalizeTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        return string.Join(' ', title.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();
    }

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
