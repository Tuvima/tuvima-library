using MediaEngine.Api.Security;
using MediaEngine.Contracts.Playback;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Services;
using MediaEngine.Storage.Playback;

namespace MediaEngine.Api.Services.Playback;

/// <summary>
/// The editor's bookmark files (universal editor v5 D37): reads a bookmark file, compares each incoming bookmark with the nearest one in the
/// title's Everyone's list, and builds a file of that list. This is the one place that decides how close two bookmarks are, using
/// <c>config/bookmarks.json</c>. Nothing is written here: the editor stages what the administrator takes and the Engine writes it on Save
/// changes. A Vault title never exports or imports, and a file the caller cannot see is not found.
/// </summary>
public sealed class BookmarkSetService
{
    /// <summary>The name written into an exported file. A set's name is not shown anywhere once it is applied (Global bookmarks spec D10).</summary>
    public const string ExportedSetName = "Everyone's bookmarks";

    /// <summary>The file name offered for an exported bookmark file.</summary>
    public const string ExportedFileName = "everyone-bookmarks.zip";

    private readonly PlayerCatalogueScope _scope;
    private readonly BookmarkRecognitionRepository _recognition;
    private readonly IVaultLibraryRepository _vault;
    private readonly BookmarkClosenessProvider _closeness;
    private readonly TimeProvider _time;
    private readonly BookmarkFrameService? _frames;

    public BookmarkSetService(
        PlayerCatalogueScope scope,
        BookmarkRecognitionRepository recognition,
        IVaultLibraryRepository vault,
        BookmarkClosenessProvider closeness,
        TimeProvider? time = null,
        BookmarkFrameService? frames = null)
    {
        _frames = frames;
        _scope = scope;
        _recognition = recognition;
        _vault = vault;
        _closeness = closeness;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>
    /// The editor's Import (universal editor v5 D37): a bookmark file compared with the title's Everyone's list instead of the person's own
    /// bookmarks. Changes nothing; the editor stages the rows the administrator takes and writes them on Save changes.
    /// </summary>
    public Task<BookmarkSetCompareDto> CompareWithEveryoneAsync(
        Guid? profileId, Guid workId, BookmarkSetRequestDto request, IReadOnlyList<AudiobookBookmarkDto> everyone, CancellationToken ct = default) =>
        PrepareAsync(profileId, workId, request, everyone, ct);

    /// <summary>
    /// One episode of a show file set beside the episode's Everyone's list (matched by season and episode number, so there is no title to
    /// recognise). The rows carry how close each bookmark is, as for a single title.
    /// </summary>
    public BookmarkSetCompareDto CompareEpisode(
        string name, IReadOnlyList<BookmarkPackBookmark> bookmarks, double? runningTimeSeconds, IReadOnlyList<AudiobookBookmarkDto> everyone)
    {
        var placed = bookmarks;
        var skipped = 0;
        if (runningTimeSeconds is double total && double.IsFinite(total) && total > 0)
        {
            var inside = bookmarks.Where(bookmark => bookmark.Time <= total).ToList();
            skipped = bookmarks.Count - inside.Count;
            placed = inside;
        }

        return new BookmarkSetCompareDto
        {
            Name = name,
            BookmarkCount = bookmarks.Count,
            Match = BookmarkPackMatchResults.Match,
            RowsReady = true,
            SkippedCount = skipped,
            AppliesToGlobal = true,
            Rows = BuildRows(placed, everyone),
            GlobalCount = everyone.Count,
        };
    }

    /// <summary>
    /// The editor's Export (universal editor v5 D37): the title's Everyone's list, definitive or pooled, as one bookmark file (times and names,
    /// never anyone's personal bookmarks; the pictures are made again by the Engine that imports it, Unified player v2 D21a). The caller has
    /// already checked that the person is an administrator and that the title is not in the Vault.
    /// </summary>
    public Task<BookmarkPackFileDto> ExportEveryoneAsync(
        Guid assetId, IReadOnlyList<AudiobookBookmarkDto> everyone, double? runningTimeSeconds, CancellationToken ct = default)
    {
        var chosen = everyone
            .Where(bookmark => bookmark.AssetId == assetId)
            .OrderBy(bookmark => bookmark.PositionSeconds)
            .Take(BookmarkPackLimits.MaximumBookmarks)
            .ToList();
        if (chosen.Count == 0)
        {
            throw new BookmarkPackInvalidException("This title has no Everyone's bookmarks to export yet.");
        }

        return BuildFileAsync(assetId, chosen, runningTimeSeconds, [], ct);
    }

    /// <summary>One title's bookmark pack: how to recognise the title, then each bookmark's time, end, chapter, name and description.</summary>
    internal async Task<(BookmarkPackContent Content, byte[] Bytes)> BuildPackAsync(
        Guid assetId, IReadOnlyList<AudiobookBookmarkDto> chosen, double? reportedRunningTime,
        IReadOnlyList<BookmarkPackChapterDto> chapterDtos, CancellationToken ct)
    {
        var recognition = await _recognition.GetRecognitionAsync(assetId, ct);
        if (recognition is null || recognition.Ids.Count == 0)
        {
            throw new BookmarkPackInvalidException("This title has no ID that other libraries can recognise it by yet, so a file cannot be made for it.");
        }

        var runningTime = reportedRunningTime is double reported && double.IsFinite(reported) && reported > 0
            ? reported
            : chosen.Select(bookmark => bookmark.DurationSeconds).Where(value => value is > 0).DefaultIfEmpty(null).Max();
        var chapters = ToChapters(chapterDtos);
        // A video bookmark carries the small picture of its moment (D37) while the file's picture budget lasts.
        var framesBytes = 0L;
        var items = new List<BookmarkPackBookmark>(chosen.Count);
        foreach (var bookmark in chosen)
        {
            byte[]? frame = null;
            if (_frames is not null && framesBytes < BookmarkPackLimits.MaximumFramesBytes
                && _frames.ReadFrameBytes(bookmark.AssetId, bookmark.Id) is { } bytes && framesBytes + bytes.Length <= BookmarkPackLimits.MaximumFramesBytes)
            {
                frame = bytes;
                framesBytes += bytes.Length;
            }

            items.Add(new BookmarkPackBookmark(
                bookmark.PositionSeconds,
                bookmark.EndSeconds,
                string.IsNullOrWhiteSpace(bookmark.ChapterTitle) ? null : bookmark.ChapterTitle.Trim(),
                ChapterStartFor(bookmark, chapters),
                string.IsNullOrWhiteSpace(bookmark.Label) ? bookmark.Note : bookmark.Label,
                bookmark.Description,
                Frame: frame));
        }

        var content = new BookmarkPackContent(
            ExportedSetName,
            null,
            new BookmarkPackTitle(recognition.Ids, runningTime, recognition.EditionLabel),
            items,
            BookmarkPackArchive.Fingerprint(ExportedSetName, null, items));
        return (content, BookmarkPackArchive.Write(content, _time.GetUtcNow()));
    }

    private async Task<BookmarkPackFileDto> BuildFileAsync(
        Guid assetId, IReadOnlyList<AudiobookBookmarkDto> chosen, double? reportedRunningTime,
        IReadOnlyList<BookmarkPackChapterDto> chapterDtos, CancellationToken ct)
    {
        var (content, bytes) = await BuildPackAsync(assetId, chosen, reportedRunningTime, chapterDtos, ct);
        return new BookmarkPackFileDto
        {
            FileName = ExportedFileName,
            ContentBase64 = Convert.ToBase64String(bytes),
            BookmarkCount = content.Bookmarks.Count,
        };
    }

    private async Task<BookmarkSetCompareDto> PrepareAsync(
        Guid? profileId, Guid workId, BookmarkSetRequestDto request, IReadOnlyList<AudiobookBookmarkDto> everyone, CancellationToken ct)
    {
        await _scope.RequireProfileAsync(profileId ?? request.ProfileId, ct);
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
        // The editor compares the file with the title's Everyone's list.
        var existing = everyone.Where(bookmark => bookmark.AssetId == request.AssetId).ToList();
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

        var rows = BuildRows(placed, existing);

        return new BookmarkSetCompareDto
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
    }

    /// <summary>Each incoming bookmark beside the nearest bookmark already there, with how close the two are (the Engine decides, never the Dashboard).</summary>
    private List<BookmarkSetCompareRowDto> BuildRows(IReadOnlyList<BookmarkPackBookmark> placed, IReadOnlyList<AudiobookBookmarkDto> existing)
    {
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
                    FrameBase64 = BookmarkFrameImage.ToBase64(incoming.Frame),
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

        return rows;
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
