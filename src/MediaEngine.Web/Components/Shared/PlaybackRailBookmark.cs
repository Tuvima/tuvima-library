using MediaEngine.Contracts.Playback;

namespace MediaEngine.Web.Components.Shared;

/// <summary>
/// One bookmark as the progress rail shows it: a tick at its position and a quick-look card with the short note and,
/// under "More", the longer description (Videos spec D43).
/// </summary>
public sealed record PlaybackRailBookmark(Guid Id, double Seconds, bool IsGlobal, string? Note, string? Description)
{
    /// <summary>The ticks for a title's saved bookmarks, in time order. A bookmark outside the file's length is left out.</summary>
    public static IReadOnlyList<PlaybackRailBookmark> From(IEnumerable<AudiobookBookmarkDto> saved, double duration) => saved
        .Where(bookmark => double.IsFinite(bookmark.PositionSeconds) && bookmark.PositionSeconds >= 0
            && (duration <= 0 || bookmark.PositionSeconds <= duration))
        .OrderBy(bookmark => bookmark.PositionSeconds)
        .Select(bookmark => new PlaybackRailBookmark(bookmark.Id, bookmark.PositionSeconds, bookmark.IsGlobal, bookmark.Note, bookmark.Description))
        .ToList();
}
