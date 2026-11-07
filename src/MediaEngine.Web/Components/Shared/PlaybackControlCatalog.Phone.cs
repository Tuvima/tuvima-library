using MediaEngine.Web.Services.Formatting;
using MediaEngine.Web.Services.Playback;

namespace MediaEngine.Web.Components.Shared;

public static partial class PlaybackControlCatalog
{
    /// <summary>The most tools the phone's one tool row holds (unified player v2 D26).</summary>
    public const int PhoneToolRowLimit = 4;

    /// <summary>
    /// The transport of the phone's expanded player (unified player v2 D26): the bar's transport in the same order, except that music's Shuffle and Repeat
    /// leave it for the tool row, so a song has Previous, Play/pause and Next with a large Play.
    /// </summary>
    public static IReadOnlyList<PlaybackControlDefinition> BuildPhoneTransport(PlaybackBarMedia media, PlaybackBarState state) =>
        BuildBarTransport(media, state)
            .Where(control => control.Key is not (PlaybackControlKey.Shuffle or PlaybackControlKey.Repeat))
            .ToList();

    /// <summary>
    /// The one row of at most four tools under the phone's transport (unified player v2 D26; mockup screen 15). Music: Shuffle, Lyrics, Queue, Repeat.
    /// Audiobooks: Chapters (when the book has them), Speed, Sleep timer, Bookmarks. Video keeps its overlay and its row is Audio &amp; subtitles, Episodes
    /// (Lessons for a folder video; only when there is a list), Bookmarks and Fullscreen. There is no Volume (the phone's own buttons) and nothing else:
    /// music Bookmarks, Playback settings and the item actions live in the "..." menu at the top. A tool that cannot act is left out, never a dead button.
    /// </summary>
    public static IReadOnlyList<PlaybackControlDefinition> BuildPhoneTools(PlaybackBarMedia media, PlaybackBarState state)
    {
        var tools = new List<PlaybackControlDefinition>();
        switch (media)
        {
            case PlaybackBarMedia.Music:
                tools.Add(BuildBarTransport(media, state).Single(control => control.Key == PlaybackControlKey.Shuffle));
                tools.Add(BarTool(PlaybackControlKey.Lyrics, "Lyrics", "lyrics", state));
                if (state.HasQueue)
                {
                    tools.Add(BarTool(PlaybackControlKey.Queue, "Queue", "queue", state));
                }
                tools.Add(BuildBarTransport(media, state).Single(control => control.Key == PlaybackControlKey.Repeat));
                break;
            case PlaybackBarMedia.Audiobook:
                if (state.HasChapters)
                {
                    tools.Add(BarTool(PlaybackControlKey.Chapters, "Chapters", "chapters", state));
                }
                tools.Add(BarTool(PlaybackControlKey.Speed, "Playback speed", "speed", state,
                    valueText: DisplayFormat.FormatSpeedControl(state.PlaybackRate),
                    isActive: Math.Abs(state.PlaybackRate - 1d) > 0.001));
                tools.Add(BarTool(PlaybackControlKey.SleepTimer, "Sleep timer", "sleep-timer", state, isActive: state.IsSleepTimerActive));
                if (state.CanBookmark)
                {
                    tools.Add(BarTool(PlaybackControlKey.Bookmarks, "Bookmarks", "bookmarks", state));
                }
                break;
            default:
                tools.Add(BarTool(PlaybackControlKey.AudioSubtitles, "Audio & subtitles", "audio-subtitles", state));
                if (state.HasEpisodes)
                {
                    tools.Add(BarTool(PlaybackControlKey.Episodes, state.IsFolderVideo ? "Lessons" : "Episodes", "episodes", state));
                }
                if (state.CanBookmark)
                {
                    tools.Add(BarTool(PlaybackControlKey.Bookmarks, "Bookmarks", "bookmarks", state));
                }
                if (state.CanFullscreen)
                {
                    var label = state.IsFullscreen ? "Exit fullscreen" : "Fullscreen";
                    tools.Add(new(PlaybackControlKey.Fullscreen, label, label, string.Empty, PlaybackControlPlacement.Utility, "fullscreen", IsActive: state.IsFullscreen));
                }
                break;
        }

        return tools.Take(PhoneToolRowLimit).ToList();
    }
}
