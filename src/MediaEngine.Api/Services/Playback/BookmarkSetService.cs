using MediaEngine.Api.Security;
using MediaEngine.Contracts.Playback;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Services;
using MediaEngine.Storage.Playback;

namespace MediaEngine.Api.Services.Playback;

/// <summary>
/// Find bookmarks (Global bookmarks spec D6 to D10): reads a bookmark set (today a bookmark file), compares each incoming bookmark with
/// the nearest one the person already has, applies the person's choices in one write, and exports a title's Global bookmarks. This is
/// the one place that decides how close two bookmarks are, using <c>config/bookmarks.json</c>. A set becomes ordinary bookmarks:
/// Global when an administrator applies it, the person's own otherwise. A Vault title never exports or imports, and a file the
/// caller cannot see is not found.
/// </summary>
public sealed class BookmarkSetService
{
    /// <summary>The name written into an exported file. A set's name is not shown anywhere once it is applied (Global bookmarks spec D10).</summary>
    public const string ExportedSetName = "Global bookmarks";

    private readonly PlayerCatalogueScope _scope;
    private readonly AudiobookBookmarkRepository _bookmarks;
    private readonly BookmarkRecognitionRepository _recognition;
    private readonly IVaultLibraryRepository _vault;
    private readonly BookmarkClosenessProvider _closeness;
    private readonly TimeProvider _time;

    public BookmarkSetService(
        PlayerCatalogueScope scope,
        AudiobookBookmarkRepository bookmarks,
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

    /// <summary>Compares a bookmark set with the bookmarks the person already has on the file, without changing anything.</summary>
    public async Task<BookmarkSetCompareDto> CompareAsync(
        Guid? profileId, Guid workId, BookmarkSetRequestDto request, CancellationToken ct = default)
    {
        var prepared = await PrepareAsync(profileId, workId, request, ct);
        return prepared.Compare;
    }

    /// <summary>Applies the person's choices for every row. Nothing changes until this is called.</summary>
    public async Task<BookmarkSetApplyResultDto> ApplyAsync(
        Guid? profileId, Guid workId, BookmarkSetApplyRequestDto request, CancellationToken ct = default)
    {
        var prepared = await PrepareAsync(profileId, workId, request.Set, ct);
        if (prepared.Match.Kind == BookmarkPackMatchKind.WrongTitle)
        {
            throw new BookmarkPackInvalidException(prepared.Match.Reason);
        }

        if (!prepared.Compare.RowsReady)
        {
            throw new BookmarkPackInvalidException("This set is for a different cut. Align it by chapter, or skip it.");
        }

        var isAdmin = prepared.Compare.AppliesToGlobal;
        var replaceWhole = request.ReplaceGlobalSet && isAdmin;
        var choices = request.Choices.GroupBy(choice => choice.Index).ToDictionary(group => group.Key, group => group.Last().Choice);
        var adds = new List<AudiobookBookmarkRepository.SetBookmark>();
        var deletes = new HashSet<Guid>();
        var kept = 0;
        var skipped = 0;
        foreach (var row in prepared.Compare.Rows)
        {
            var choice = choices.TryGetValue(row.Index, out var chosen) && IsKnownChoice(chosen) ? chosen : row.DefaultChoice;
            if (replaceWhole && choice != BookmarkRowChoices.Skip)
            {
                // Replacing the whole Global set means every incoming bookmark goes in, whatever the row said.
                choice = BookmarkRowChoices.Incoming;
            }

            if (choice == BookmarkRowChoices.Skip)
            {
                skipped++;
                continue;
            }

            if (choice == BookmarkRowChoices.Yours && row.Yours is not null)
            {
                kept++;
                continue;
            }

            if (choice == BookmarkRowChoices.Yours)
            {
                // A New row has no bookmark of yours to keep: leaving it out is the same as skipping it.
                skipped++;
                continue;
            }

            adds.Add(ToSetBookmark(row.Incoming));
            // The nearest bookmark is replaced by the incoming one, when the person is allowed to replace it: their own always, and a
            // Global one only for an administrator. Anyone else keeps the Global one and gets the incoming bookmark as their own.
            if (row.Yours is { } yours && (!yours.IsGlobal || isAdmin))
            {
                deletes.Add(yours.Id);
            }
        }

        var duration = request.Set.RunningTimeSeconds is double total && double.IsFinite(total) && total > 0 ? total : (double?)null;
        var outcome = await _bookmarks.ApplySetAsync(
            prepared.Profile, workId, request.Set.AssetId, duration, adds, deletes,
            addAsGlobal: isAdmin, replaceGlobalSet: replaceWhole, _closeness.SameSpotSeconds, ct);
        return new BookmarkSetApplyResultDto
        {
            Added = outcome.Added,
            Replaced = outcome.Replaced,
            Kept = kept,
            Skipped = skipped,
            AddedToGlobal = isAdmin,
        };
    }

    /// <summary>Builds a file of this title's Global bookmarks only, never anyone's personal ones. Administrators only; never a Vault title.</summary>
    public async Task<BookmarkPackFileDto> ExportGlobalAsync(
        Guid? profileId, Guid workId, ExportGlobalBookmarksRequestDto request, CancellationToken ct = default)
    {
        var profile = await _scope.RequireProfileAsync(profileId ?? request.ProfileId, ct);
        await _scope.RequireAssetAsync(request.AssetId, ct, workId);
        if (await _vault.IsInVaultAsync(request.AssetId, ct))
        {
            throw new PlayerResourceDeniedException();
        }

        if (!await _scope.IsServerAdministratorAsync(ct))
        {
            throw new PlayerBookmarkAdminDeniedException();
        }

        var saved = await _bookmarks.GetByWorkAsync(profile, workId, ct, await _scope.AssetIdsAsync(ct));
        var chosen = saved
            .Where(bookmark => bookmark.IsGlobal && bookmark.AssetId == request.AssetId)
            .OrderBy(bookmark => bookmark.PositionSeconds)
            .Take(BookmarkPackLimits.MaximumBookmarks)
            .ToList();
        if (chosen.Count == 0)
        {
            throw new BookmarkPackInvalidException("This title has no Global bookmarks to export yet.");
        }

        var recognition = await _recognition.GetRecognitionAsync(request.AssetId, ct);
        if (recognition is null || recognition.Ids.Count == 0)
        {
            throw new BookmarkPackInvalidException("This title has no ID that other libraries can recognise it by yet, so a file cannot be made for it.");
        }

        var runningTime = request.RunningTimeSeconds is double reported && double.IsFinite(reported) && reported > 0
            ? reported
            : chosen.Select(bookmark => bookmark.DurationSeconds).Where(value => value is > 0).DefaultIfEmpty(null).Max();
        var chapters = ToChapters(request.Chapters);
        var items = chosen.Select(bookmark => new BookmarkPackBookmark(
            bookmark.PositionSeconds,
            bookmark.EndSeconds,
            string.IsNullOrWhiteSpace(bookmark.ChapterTitle) ? null : bookmark.ChapterTitle.Trim(),
            ChapterStartFor(bookmark, chapters),
            string.IsNullOrWhiteSpace(bookmark.Label) ? bookmark.Note : bookmark.Label,
            bookmark.Description)).ToList();

        var content = new BookmarkPackContent(
            ExportedSetName,
            null,
            new BookmarkPackTitle(recognition.Ids, runningTime, recognition.EditionLabel),
            items,
            BookmarkPackArchive.Fingerprint(ExportedSetName, null, items));
        var bytes = BookmarkPackArchive.Write(content, _time.GetUtcNow());
        return new BookmarkPackFileDto
        {
            FileName = "global-bookmarks.zip",
            ContentBase64 = Convert.ToBase64String(bytes),
            BookmarkCount = items.Count,
        };
    }

    private sealed record Prepared(Guid Profile, BookmarkPackMatch Match, BookmarkSetCompareDto Compare);

    private async Task<Prepared> PrepareAsync(
        Guid? profileId, Guid workId, BookmarkSetRequestDto request, CancellationToken ct)
    {
        var profile = await _scope.RequireProfileAsync(profileId ?? request.ProfileId, ct);
        var assets = await _scope.AssetIdsAsync(ct);
        await _scope.RequireAssetAsync(request.AssetId, ct, workId);
        // A Vault title never takes bookmarks from a file, and never gives them to one.
        if (await _vault.IsInVaultAsync(request.AssetId, ct))
        {
            throw new PlayerResourceDeniedException();
        }

        var content = BookmarkPackArchive.Read(Decode(request.ContentBase64));
        var local = await _recognition.GetRecognitionAsync(request.AssetId, ct) ?? throw new PlayerResourceDeniedException();
        var runningTime = request.RunningTimeSeconds is double reported && double.IsFinite(reported) && reported > 0 ? reported : (double?)null;
        var match = BookmarkPackMatcher.Compare(content.Title, new BookmarkPackTitle(local.Ids, runningTime, local.EditionLabel));
        var isAdmin = await _scope.IsServerAdministratorAsync(ct);
        var existing = (await _bookmarks.GetByWorkAsync(profile, workId, ct, assets))
            .Where(bookmark => bookmark.AssetId == request.AssetId).ToList();
        var globalCount = existing.Count(bookmark => bookmark.IsGlobal);
        var canAlign = match.Kind == BookmarkPackMatchKind.DifferentCut
            && request.Chapters.Count > 0
            && content.Bookmarks.Any(bookmark => !string.IsNullOrWhiteSpace(bookmark.Chapter));

        IReadOnlyList<BookmarkPackBookmark> placed = [];
        var skipped = 0;
        var ready = false;
        if (match.Kind == BookmarkPackMatchKind.Match)
        {
            placed = content.Bookmarks;
            ready = true;
        }
        else if (match.Kind == BookmarkPackMatchKind.DifferentCut && request.Mode == BookmarkPackImportModes.AlignByChapter)
        {
            var alignment = BookmarkPackMatcher.AlignByChapter(content.Bookmarks, ToChapters(request.Chapters), runningTime);
            placed = alignment.Aligned;
            skipped = alignment.Skipped;
            ready = true;
        }

        if (ready && runningTime is double total)
        {
            var inside = placed.Where(bookmark => bookmark.Time <= total).ToList();
            skipped += placed.Count - inside.Count;
            placed = inside;
        }

        var limits = _closeness.Current.For(BookmarkPositionKind.Time);
        var rows = new List<BookmarkSetCompareRowDto>(placed.Count);
        for (var index = 0; index < placed.Count; index++)
        {
            var incoming = placed[index];
            var nearest = existing.OrderBy(bookmark => Math.Abs(bookmark.PositionSeconds - incoming.Time)).FirstOrDefault();
            var gap = nearest is null ? (double?)null : Math.Abs(nearest.PositionSeconds - incoming.Time);
            var closeness = gap is double distance ? BookmarkClosenessRules.Classify(limits, distance) : BookmarkCloseness.New;
            rows.Add(new BookmarkSetCompareRowDto
            {
                Index = index,
                Incoming = new BookmarkSetItemDto
                {
                    PositionSeconds = incoming.Time,
                    EndSeconds = incoming.EndTime,
                    ChapterTitle = incoming.Chapter,
                    Note = incoming.Note,
                    Description = incoming.Description,
                },
                Yours = closeness == BookmarkCloseness.New || nearest is null ? null : new BookmarkSetExistingDto
                {
                    Id = nearest.Id,
                    PositionSeconds = nearest.PositionSeconds,
                    Name = nearest.Label,
                    Note = nearest.Note,
                    IsGlobal = nearest.IsGlobal,
                },
                GapSeconds = closeness == BookmarkCloseness.New ? null : gap,
                Closeness = ToLabel(closeness),
                DefaultChoice = ToChoice(BookmarkClosenessRules.DefaultChoice(closeness)),
            });
        }

        var compare = new BookmarkSetCompareDto
        {
            Name = content.Name,
            Author = content.Author,
            BookmarkCount = content.Bookmarks.Count,
            Match = ToMatchResult(match.Kind),
            Reason = match.Reason,
            CanAlignByChapter = canAlign,
            RowsReady = ready,
            SkippedCount = skipped,
            AppliesToGlobal = isAdmin,
            Rows = rows,
            GlobalCount = globalCount,
        };
        return new Prepared(profile, match, compare);
    }

    private static AudiobookBookmarkRepository.SetBookmark ToSetBookmark(BookmarkSetItemDto item)
    {
        var note = item.Note is { Length: > AudiobookBookmarkNotePolicy.MaximumLength } longNote
            ? longNote[..AudiobookBookmarkNotePolicy.MaximumLength]
            : item.Note;
        var description = item.Description is { Length: > AudiobookBookmarkNotePolicy.MaximumDescriptionLength } longText
            ? longText[..AudiobookBookmarkNotePolicy.MaximumDescriptionLength]
            : item.Description;
        return new AudiobookBookmarkRepository.SetBookmark(item.PositionSeconds, item.EndSeconds, item.ChapterTitle, note, description);
    }

    private static bool IsKnownChoice(string? choice) =>
        choice is BookmarkRowChoices.Incoming or BookmarkRowChoices.Yours or BookmarkRowChoices.Skip;

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

    private static IReadOnlyList<BookmarkPackChapter> ToChapters(IReadOnlyList<BookmarkPackChapterDto> chapters) => chapters
        .Where(chapter => !string.IsNullOrWhiteSpace(chapter.Title) && double.IsFinite(chapter.StartSeconds) && chapter.StartSeconds >= 0)
        .Select(chapter => new BookmarkPackChapter(chapter.Title.Trim(), chapter.StartSeconds))
        .ToList();

    /// <summary>How far into its chapter the bookmark is needs the chapter's start; the nearest chapter of that name at or before the time.</summary>
    private static double? ChapterStartFor(AudiobookBookmarkDto bookmark, IReadOnlyList<BookmarkPackChapter> chapters)
    {
        if (string.IsNullOrWhiteSpace(bookmark.ChapterTitle))
        {
            return null;
        }

        var title = bookmark.ChapterTitle.Trim();
        return chapters
            .Where(chapter => string.Equals(chapter.Title, title, StringComparison.OrdinalIgnoreCase)
                && chapter.StartSeconds <= bookmark.PositionSeconds)
            .OrderByDescending(chapter => chapter.StartSeconds)
            .Select(chapter => (double?)chapter.StartSeconds)
            .FirstOrDefault();
    }

    private static string ToMatchResult(BookmarkPackMatchKind kind) => kind switch
    {
        BookmarkPackMatchKind.Match => BookmarkPackMatchResults.Match,
        BookmarkPackMatchKind.DifferentCut => BookmarkPackMatchResults.DifferentCut,
        _ => BookmarkPackMatchResults.WrongTitle,
    };
}
