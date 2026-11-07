namespace MediaEngine.Domain.Services;

/// <summary>
/// One saved bookmark as the pool sees it: where it is, who owns it and whether it is already part of Everyone's set. A film, episode or audiobook
/// bookmark is a time (<see cref="Position"/> in seconds); a book bookmark is a chapter (and, when the reader saved one, a place in it).
/// </summary>
public sealed record BookmarkPoolCandidate(
    Guid Id,
    Guid AssetId,
    string PositionKind,
    double Position,
    int? ChapterIndex,
    string? Locator,
    bool IsInSet,
    bool HasName,
    DateTimeOffset CreatedAt);

/// <summary>Bookmarks that are the same moment, with the one that stands for them in Everyone's list.</summary>
public sealed record BookmarkPoolGroup(BookmarkPoolCandidate Representative, IReadOnlyList<BookmarkPoolCandidate> Members);

/// <summary>
/// How Everyone's bookmarks are pooled from every profile's own (unified player v2 D21, universal editor v5 D37): bookmarks at the same spot
/// become one entry, so a moment two people marked is listed once. One place decides it, so the editor and the player (when it adopts the
/// pool) always agree. Pure: the gap that counts as "the same spot" is given by the caller (<c>config/bookmarks.json</c>).
/// </summary>
public static class BookmarkPooling
{
    public const string TimeKind = "time";
    public const string TextKind = "text";
    public const string PageKind = "page";

    /// <summary>True when two bookmarks on one file are the same moment: a time or page within <paramref name="sameSpot"/>, a book place in the same chapter.</summary>
    public static bool IsSameSpot(BookmarkPoolCandidate a, BookmarkPoolCandidate b, double sameSpot)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        if (a.AssetId != b.AssetId || !string.Equals(a.PositionKind, b.PositionKind, StringComparison.Ordinal))
        {
            return false;
        }

        if (string.Equals(a.PositionKind, TextKind, StringComparison.Ordinal))
        {
            return a.ChapterIndex == b.ChapterIndex && string.Equals(a.Locator, b.Locator, StringComparison.Ordinal);
        }

        return Math.Abs(a.Position - b.Position) <= sameSpot;
    }

    /// <summary>
    /// Groups the bookmarks by spot, in file and position order. A group starts at its first bookmark and takes every later one within the
    /// gap of that first one (so a long run of bookmarks a few seconds apart does not chain into one). The group is stood for by a bookmark
    /// already in Everyone's set, else a named one, else the earliest made.
    /// </summary>
    public static IReadOnlyList<BookmarkPoolGroup> Merge(IEnumerable<BookmarkPoolCandidate> candidates, Func<string, double> sameSpotFor)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(sameSpotFor);
        var ordered = candidates
            .OrderBy(candidate => candidate.AssetId)
            .ThenBy(candidate => candidate.PositionKind, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.ChapterIndex ?? -1)
            .ThenBy(candidate => candidate.Locator, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.Position)
            .ThenBy(candidate => candidate.CreatedAt)
            .ThenBy(candidate => candidate.Id)
            .ToList();

        var groups = new List<BookmarkPoolGroup>();
        List<BookmarkPoolCandidate>? members = null;
        foreach (var candidate in ordered)
        {
            if (members is not null && IsSameSpot(members[0], candidate, sameSpotFor(candidate.PositionKind)))
            {
                members.Add(candidate);
                continue;
            }

            if (members is not null)
            {
                groups.Add(Close(members));
            }

            members = [candidate];
        }

        if (members is not null)
        {
            groups.Add(Close(members));
        }

        return groups;
    }

    private static BookmarkPoolGroup Close(List<BookmarkPoolCandidate> members)
    {
        var representative = members
            .OrderByDescending(candidate => candidate.IsInSet)
            .ThenByDescending(candidate => candidate.HasName)
            .ThenBy(candidate => candidate.CreatedAt)
            .ThenBy(candidate => candidate.Id)
            .First();
        return new BookmarkPoolGroup(representative, members);
    }
}
