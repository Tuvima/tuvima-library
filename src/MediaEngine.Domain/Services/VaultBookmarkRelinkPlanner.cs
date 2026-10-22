namespace MediaEngine.Domain.Services;

/// <summary>
/// How to recognise a Vault video: its canonical IDs (including the Stash-box record of a matched video), the running time and edition of the
/// copy, and its exact file fingerprints as a fallback.
/// </summary>
public sealed record VaultRelinkIdentity(
    IReadOnlyDictionary<string, string> Ids,
    double? RunningTimeSeconds,
    string? Edition,
    string? ContentHash,
    IReadOnlyList<string> Fingerprints);

/// <summary>A Vault video found by a rescan, as the re-link sees it.</summary>
public sealed record VaultRelinkCandidate(
    Guid AssetId,
    Guid WorkId,
    VaultRelinkIdentity Identity,
    IReadOnlyList<BookmarkPackChapter> Chapters);

/// <summary>One bookmark kept in a backup, waiting for its video.</summary>
public sealed record VaultRelinkBookmark(VaultRelinkIdentity Identity, BookmarkPackBookmark Bookmark);

public enum VaultRelinkKind
{
    /// <summary>The video is the same title and cut (or the very same file): the time is used as it is.</summary>
    Exact,

    /// <summary>The same title on a different cut; the bookmark was moved by chapter name.</summary>
    Aligned,

    /// <summary>This video is not the bookmark's video, or the time cannot be placed on it.</summary>
    NoMatch,
}

public sealed record VaultRelinkDecision(VaultRelinkKind Kind, BookmarkPackBookmark? Bookmark);

/// <summary>
/// Decides whether a bookmark kept in a backup belongs on a video found by a rescan. The bookmark is pinned to the canonical title first
/// (external IDs plus running time and edition, reusing <see cref="BookmarkPackMatcher"/>); when the bookmark or the video has no canonical
/// ID of the same kind, the exact file fingerprint decides; when neither agrees the bookmark cannot be re-linked to this video.
/// Pure, so the rules are tested without a data store.
/// </summary>
public static class VaultBookmarkRelinkPlanner
{
    private static readonly IReadOnlySet<string> ExtraIdKeys =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { BookmarkPackMatcher.VaultStashBoxIdKey };

    public static VaultRelinkDecision Decide(VaultRelinkBookmark saved, VaultRelinkCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(saved);
        ArgumentNullException.ThrowIfNull(candidate);
        var from = saved.Identity;
        var to = candidate.Identity;

        if (BookmarkPackMatcher.ShareAnIdKind(from.Ids, to.Ids, ExtraIdKeys))
        {
            var match = BookmarkPackMatcher.Compare(
                new BookmarkPackTitle(from.Ids, from.RunningTimeSeconds, from.Edition),
                new BookmarkPackTitle(to.Ids, to.RunningTimeSeconds, to.Edition),
                ExtraIdKeys);
            return match.Kind switch
            {
                BookmarkPackMatchKind.Match => Fit(saved.Bookmark, to.RunningTimeSeconds, VaultRelinkKind.Exact),
                BookmarkPackMatchKind.DifferentCut => AlignOnTheNewCut(saved.Bookmark, candidate),
                _ => new VaultRelinkDecision(VaultRelinkKind.NoMatch, null),
            };
        }

        // No canonical ID in common (nothing was known about the title, or the rescan has not matched it yet): the exact file decides.
        return SameFile(from, to)
            ? new VaultRelinkDecision(VaultRelinkKind.Exact, saved.Bookmark)
            : new VaultRelinkDecision(VaultRelinkKind.NoMatch, null);
    }

    private static bool SameFile(VaultRelinkIdentity first, VaultRelinkIdentity second)
    {
        if (!string.IsNullOrWhiteSpace(first.ContentHash)
            && string.Equals(first.ContentHash, second.ContentHash, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return first.Fingerprints.Any(print => !string.IsNullOrWhiteSpace(print)
            && second.Fingerprints.Contains(print, StringComparer.OrdinalIgnoreCase));
    }

    private static VaultRelinkDecision Fit(BookmarkPackBookmark bookmark, double? runningTime, VaultRelinkKind kind) =>
        runningTime is double total && total > 0 && bookmark.Time > total
            ? new VaultRelinkDecision(VaultRelinkKind.NoMatch, null)
            : new VaultRelinkDecision(kind, bookmark);

    private static VaultRelinkDecision AlignOnTheNewCut(BookmarkPackBookmark bookmark, VaultRelinkCandidate candidate)
    {
        if (candidate.Chapters.Count == 0)
        {
            return new VaultRelinkDecision(VaultRelinkKind.NoMatch, null);
        }

        var alignment = BookmarkPackMatcher.AlignByChapter([bookmark], candidate.Chapters, candidate.Identity.RunningTimeSeconds);
        return alignment.Aligned.Count == 1
            ? new VaultRelinkDecision(VaultRelinkKind.Aligned, alignment.Aligned[0])
            : new VaultRelinkDecision(VaultRelinkKind.NoMatch, null);
    }
}
