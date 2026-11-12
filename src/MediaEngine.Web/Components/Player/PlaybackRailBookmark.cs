using MediaEngine.Contracts.Playback;

namespace MediaEngine.Web.Components.Player;

/// <summary>
/// One bookmark as the progress bar and the Bookmarks panel show it: a marker at its position and a quick-look card with its name, the short note
/// and, under "More", the longer description (player-controls-v3 D8a, D10). Global bookmarks are white, a person's own are the accent colour.
/// <see cref="IsYours"/> and <see cref="InEveryone"/> are decided by the Engine (unified player v2 D21): the panel's Mine and Everyone lists, and the
/// markers that follow the list shown, only read them and never compare profile ids. <see cref="FrameUrl"/> is the Engine's still from that moment.
/// </summary>
public sealed record PlaybackRailBookmark(Guid Id, double Seconds, bool IsGlobal, string? Note, string? Description, string? Label = null, string? OwnerName = null,
    bool IsYours = false, bool InEveryone = false, string? FrameUrl = null)
{
    /// <summary>The bookmark's name when it has one, otherwise its short note.</summary>
    public string? DisplayName => string.IsNullOrWhiteSpace(Label) ? Note : Label;

    /// <summary>The markers for a title's bookmarks, in time order. A bookmark outside the file's length is left out.</summary>
    public static IReadOnlyList<PlaybackRailBookmark> From(IEnumerable<AudiobookBookmarkDto> saved, double duration) => saved
        .Where(bookmark => double.IsFinite(bookmark.PositionSeconds) && bookmark.PositionSeconds >= 0
            && (duration <= 0 || bookmark.PositionSeconds <= duration))
        .OrderBy(bookmark => bookmark.PositionSeconds)
        .Select(bookmark => new PlaybackRailBookmark(bookmark.Id, bookmark.PositionSeconds, bookmark.IsGlobal, bookmark.Note,
            bookmark.Description, bookmark.Label, bookmark.OwnerName, bookmark.IsYours, bookmark.InEveryone, bookmark.FrameUrl))
        .ToList();

    /// <summary>The Mine list: bookmarks the Engine says the profile made.</summary>
    public static IReadOnlyList<PlaybackRailBookmark> MineOf(IEnumerable<PlaybackRailBookmark> all) => all.Where(bookmark => bookmark.IsYours).ToList();

    /// <summary>The Everyone list: the bookmarks the Engine says are in the title's Everyone set.</summary>
    public static IReadOnlyList<PlaybackRailBookmark> EveryoneOf(IEnumerable<PlaybackRailBookmark> all) => all.Where(bookmark => bookmark.InEveryone).ToList();

    /// <summary>True when the profile's remembered choice is Mine; anything else (the default, <c>All</c>) is Everyone.</summary>
    public static bool IsMineChoice(string? choice) => string.Equals(choice, PlaybackPreferenceValues.Mine, StringComparison.Ordinal);

    /// <summary>The markers for the list the panel shows: Mine or Everyone, as the profile chose (D21: the markers follow the choice shown).</summary>
    public static IReadOnlyList<PlaybackRailBookmark> ForChoice(IEnumerable<PlaybackRailBookmark> all, string? choice) =>
        IsMineChoice(choice) ? MineOf(all) : EveryoneOf(all);

    /// <summary>
    /// The dots the video bar draws now that they are on by default (video player follow-ups D4): the profile's own bookmarks, unless
    /// the profile itself picked another list. A profile that never chose is stored as <c>All</c>, and must not see anyone else's dots.
    /// </summary>
    public static IReadOnlyList<PlaybackRailBookmark> ForBar(IEnumerable<PlaybackRailBookmark> all, string? choice) =>
        string.IsNullOrEmpty(choice) || string.Equals(choice, PlaybackPreferenceValues.All, StringComparison.Ordinal) ? MineOf(all) : ForChoice(all, choice);
}
