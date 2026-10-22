using System.Text;
using MediaEngine.Api.Security;
using MediaEngine.Contracts.Playback;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Services;
using MediaEngine.Storage.Playback;

namespace MediaEngine.Api.Services.Playback;

/// <summary>
/// Exporting and importing bookmark packs (Videos spec D46). A pack carries only how to recognise a title and, per bookmark, its
/// time, optional end, chapter name, note and description. Imported packs are read-only, belong to the profile that imported them,
/// and follow the bookmark rules: a file the caller cannot see is not found, and a Vault title can never be exported.
/// </summary>
public sealed class BookmarkPackService
{
    private readonly PlayerCatalogueScope _scope;
    private readonly AudiobookBookmarkRepository _bookmarks;
    private readonly BookmarkPackRepository _packs;
    private readonly IVaultLibraryRepository _vault;
    private readonly TimeProvider _time;

    public BookmarkPackService(
        PlayerCatalogueScope scope,
        AudiobookBookmarkRepository bookmarks,
        BookmarkPackRepository packs,
        IVaultLibraryRepository vault,
        TimeProvider? time = null)
    {
        _scope = scope;
        _bookmarks = bookmarks;
        _packs = packs;
        _vault = vault;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Builds a pack from some of the profile's own bookmarks on the title.</summary>
    public async Task<BookmarkPackFileDto> ExportAsync(
        Guid? profileId, Guid workId, BookmarkPackExportRequestDto request, CancellationToken ct = default)
    {
        var profile = await _scope.RequireProfileAsync(profileId ?? request.ProfileId, ct);
        await _scope.RequireAssetAsync(request.AssetId, ct, workId);
        // A Vault title is never exportable, even in an unlocked session.
        if (await _vault.IsInVaultAsync(request.AssetId, ct))
        {
            throw new PlayerResourceDeniedException();
        }

        var name = request.Name?.Trim();
        var author = string.IsNullOrWhiteSpace(request.Author) ? null : request.Author.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > BookmarkPackLimits.MaximumNameLength)
        {
            throw new BookmarkPackInvalidException($"Give the pack a name of up to {BookmarkPackLimits.MaximumNameLength} characters.");
        }

        if (author is { Length: > BookmarkPackLimits.MaximumAuthorLength })
        {
            throw new BookmarkPackInvalidException($"The author can be up to {BookmarkPackLimits.MaximumAuthorLength} characters.");
        }

        var wanted = request.BookmarkIds.ToHashSet();
        if (wanted.Count == 0 || wanted.Count > BookmarkPackLimits.MaximumBookmarks)
        {
            throw new BookmarkPackInvalidException($"Pick between 1 and {BookmarkPackLimits.MaximumBookmarks} bookmarks to export.");
        }

        var saved = await _bookmarks.GetByWorkAsync(profile, workId, ct, await _scope.AssetIdsAsync(ct));
        // Only the profile's own, personal bookmarks on this file: never a shared one, and never anyone else's.
        var chosen = saved
            .Where(bookmark => wanted.Contains(bookmark.Id) && !bookmark.IsGlobal && bookmark.ProfileId == profile
                && bookmark.AssetId == request.AssetId)
            .OrderBy(bookmark => bookmark.PositionSeconds)
            .ToList();
        if (chosen.Count == 0)
        {
            throw new BookmarkPackInvalidException("None of the chosen bookmarks can be exported.");
        }

        var recognition = await _packs.GetRecognitionAsync(request.AssetId, ct);
        if (recognition is null || recognition.Ids.Count == 0)
        {
            throw new BookmarkPackInvalidException("This title has no ID that other libraries can recognise it by yet, so a pack cannot be made for it.");
        }

        var runningTime = request.RunningTimeSeconds is double reported && double.IsFinite(reported) && reported > 0
            ? reported
            : chosen.Select(bookmark => bookmark.DurationSeconds).Where(value => value is > 0).Max();
        var chapters = ToChapters(request.Chapters);
        var items = chosen.Select(bookmark => new BookmarkPackBookmark(
            bookmark.PositionSeconds,
            bookmark.EndSeconds,
            string.IsNullOrWhiteSpace(bookmark.ChapterTitle) ? null : bookmark.ChapterTitle.Trim(),
            ChapterStartFor(bookmark, chapters),
            bookmark.Note,
            bookmark.Description)).ToList();

        var content = new BookmarkPackContent(
            name,
            author,
            new BookmarkPackTitle(recognition.Ids, runningTime, recognition.EditionLabel),
            items,
            BookmarkPackArchive.Fingerprint(name, author, items));
        var bytes = BookmarkPackArchive.Write(content, _time.GetUtcNow());
        return new BookmarkPackFileDto
        {
            FileName = FileNameFor(name),
            ContentBase64 = Convert.ToBase64String(bytes),
            BookmarkCount = items.Count,
        };
    }

    /// <summary>Reads a pack and says whether it is for this title, without adding anything.</summary>
    public async Task<BookmarkPackPreviewDto> InspectAsync(
        Guid? profileId, Guid workId, BookmarkPackImportRequestDto request, CancellationToken ct = default)
    {
        var (profile, content, match, _) = await ReadForImportAsync(profileId, workId, request, ct);
        return new BookmarkPackPreviewDto
        {
            Name = content.Name,
            Author = content.Author,
            BookmarkCount = content.Bookmarks.Count,
            Match = ToMatchResult(match.Kind),
            Reason = match.Reason,
            CanAlignByChapter = match.Kind == BookmarkPackMatchKind.DifferentCut
                && request.Chapters.Count > 0
                && content.Bookmarks.Any(bookmark => !string.IsNullOrWhiteSpace(bookmark.Chapter)),
            AlreadyImported = await _packs.ExistsAsync(profile, workId, content.ContentHash, ct),
        };
    }

    /// <summary>
    /// Adds a pack's bookmarks to the title as read-only pack bookmarks for the profile. A pack for another title is refused; a pack
    /// for a different cut needs the person to choose align-by-chapter (or skip, which simply does not call this).
    /// </summary>
    public async Task<BookmarkPackDto> ImportAsync(
        Guid? profileId, Guid workId, BookmarkPackImportRequestDto request, CancellationToken ct = default)
    {
        var (profile, content, match, _) = await ReadForImportAsync(profileId, workId, request, ct);
        if (match.Kind == BookmarkPackMatchKind.WrongTitle)
        {
            throw new BookmarkPackInvalidException(match.Reason);
        }

        IReadOnlyList<BookmarkPackBookmark> bookmarks = content.Bookmarks;
        var aligned = false;
        if (match.Kind == BookmarkPackMatchKind.DifferentCut)
        {
            if (request.Mode != BookmarkPackImportModes.AlignByChapter)
            {
                throw new BookmarkPackInvalidException("This pack is for a different cut. Align it by chapter, or skip it.");
            }

            var alignment = BookmarkPackMatcher.AlignByChapter(bookmarks, ToChapters(request.Chapters), request.RunningTimeSeconds);
            if (alignment.Aligned.Count == 0)
            {
                throw new BookmarkPackInvalidException("None of this pack's bookmarks could be placed on your copy by chapter.");
            }

            bookmarks = alignment.Aligned;
            aligned = true;
        }
        else if (request.RunningTimeSeconds is double total && total > 0)
        {
            bookmarks = bookmarks.Where(bookmark => bookmark.Time <= total).ToList();
            if (bookmarks.Count == 0)
            {
                throw new BookmarkPackInvalidException("None of this pack's bookmarks fall inside your copy.");
            }
        }

        var added = await _packs.AddAsync(
            profile, workId, request.AssetId, content.Name, content.Author, content.ContentHash,
            content.Title.RunningTimeSeconds, content.Title.Edition, aligned, bookmarks, ct);
        return added ?? throw new BookmarkPackInvalidException("This pack is already on this title.");
    }

    public async Task<IReadOnlyList<BookmarkPackDto>> GetAsync(Guid? profileId, Guid workId, CancellationToken ct = default)
    {
        var profile = await _scope.RequireProfileAsync(profileId, ct);
        return await _packs.GetByWorkAsync(profile, workId, ct, await _scope.AssetIdsAsync(ct));
    }

    public async Task<bool> SetEnabledAsync(Guid? profileId, Guid packId, bool isEnabled, CancellationToken ct = default)
    {
        var profile = await _scope.RequireProfileAsync(profileId, ct);
        return await _packs.SetEnabledAsync(profile, packId, isEnabled, ct, await _scope.AssetIdsAsync(ct));
    }

    public async Task<bool> DeleteAsync(Guid? profileId, Guid packId, CancellationToken ct = default)
    {
        var profile = await _scope.RequireProfileAsync(profileId, ct);
        return await _packs.DeleteAsync(profile, packId, ct, await _scope.AssetIdsAsync(ct));
    }

    private async Task<(Guid Profile, BookmarkPackContent Content, BookmarkPackMatch Match, BookmarkPackRecognition Local)> ReadForImportAsync(
        Guid? profileId, Guid workId, BookmarkPackImportRequestDto request, CancellationToken ct)
    {
        var profile = await _scope.RequireProfileAsync(profileId ?? request.ProfileId, ct);
        await _scope.RequireAssetAsync(request.AssetId, ct, workId);
        var content = BookmarkPackArchive.Read(Decode(request.ContentBase64));
        var local = await _packs.GetRecognitionAsync(request.AssetId, ct) ?? throw new PlayerResourceDeniedException();
        var runningTime = request.RunningTimeSeconds is double reported && double.IsFinite(reported) && reported > 0 ? reported : (double?)null;
        var match = BookmarkPackMatcher.Compare(content.Title, new BookmarkPackTitle(local.Ids, runningTime, local.EditionLabel));
        return (profile, content, match, local);
    }

    private static byte[] Decode(string? contentBase64)
    {
        if (string.IsNullOrWhiteSpace(contentBase64) || contentBase64.Length > BookmarkPackLimits.MaximumFileBytes * 4L / 3 + 8)
        {
            throw new BookmarkPackInvalidException("This is not a bookmark pack, or it is too large.");
        }

        try
        {
            return Convert.FromBase64String(contentBase64);
        }
        catch (FormatException)
        {
            throw new BookmarkPackInvalidException("This is not a bookmark pack.");
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

    private static string FileNameFor(string name)
    {
        var slug = new StringBuilder();
        foreach (var character in name.Trim().ToLowerInvariant())
        {
            if (character is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                slug.Append(character);
            }
            else if (slug.Length > 0 && slug[^1] != '-')
            {
                slug.Append('-');
            }
        }

        var text = slug.ToString().Trim('-');
        if (text.Length > 40)
        {
            text = text[..40].Trim('-');
        }

        return $"bookmarks-{(text.Length == 0 ? "pack" : text)}.zip";
    }
}
