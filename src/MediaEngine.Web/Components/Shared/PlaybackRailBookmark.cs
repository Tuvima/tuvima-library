using MediaEngine.Contracts.Playback;

namespace MediaEngine.Web.Components.Shared;

/// <summary>
/// One bookmark as the progress rail shows it: a tick at its position and a quick-look card with the short note and,
/// under "More", the longer description (Videos spec D43). A pack bookmark (D46) carries the label of the pack it came from
/// (its name and author) and gets its own tick shade.
/// </summary>
public sealed record PlaybackRailBookmark(Guid Id, double Seconds, bool IsGlobal, string? Note, string? Description, string? PackLabel = null)
{
    /// <summary>True for a read-only bookmark that came from an imported pack.</summary>
    public bool IsPack => PackLabel is not null;

    /// <summary>The ticks for a title's saved bookmarks, in time order. A bookmark outside the file's length is left out.</summary>
    public static IReadOnlyList<PlaybackRailBookmark> From(IEnumerable<AudiobookBookmarkDto> saved, double duration) => saved
        .Where(bookmark => double.IsFinite(bookmark.PositionSeconds) && bookmark.PositionSeconds >= 0
            && (duration <= 0 || bookmark.PositionSeconds <= duration))
        .OrderBy(bookmark => bookmark.PositionSeconds)
        .Select(bookmark => new PlaybackRailBookmark(bookmark.Id, bookmark.PositionSeconds, bookmark.IsGlobal, bookmark.Note, bookmark.Description))
        .ToList();

    /// <summary>The ticks for the title's imported packs. A pack that is switched off, and a bookmark outside the file's length, are left out.</summary>
    public static IReadOnlyList<PlaybackRailBookmark> FromPacks(IEnumerable<BookmarkPackDto> packs, double duration) => packs
        .Where(pack => pack.IsEnabled)
        .SelectMany(pack => pack.Bookmarks.Select(bookmark => (Pack: pack, Bookmark: bookmark)))
        .Where(item => double.IsFinite(item.Bookmark.PositionSeconds) && item.Bookmark.PositionSeconds >= 0
            && (duration <= 0 || item.Bookmark.PositionSeconds <= duration))
        .OrderBy(item => item.Bookmark.PositionSeconds)
        .Select(item => new PlaybackRailBookmark(
            item.Bookmark.Id, item.Bookmark.PositionSeconds, false, item.Bookmark.Note, item.Bookmark.Description,
            string.IsNullOrWhiteSpace(item.Pack.Author) ? item.Pack.Name : $"{item.Pack.Name} by {item.Pack.Author}"))
        .ToList();
}
