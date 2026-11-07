using MediaEngine.Contracts.Playback;
using MediaEngine.Web.Services.Formatting;
using MediaEngine.Web.Services.Playback;

namespace MediaEngine.Web.Components.Shared;

/// <summary>
/// Everything the catalog needs to decide which transport buttons and tools the bar shows and in what state.
/// Defaults are the unified player v2 values: skip back 10 s and forward 30 s for every media that has them (D3).
/// </summary>
public sealed record PlaybackBarState(
    bool IsPlaying = false,
    bool NeedsUserGestureToStart = false,
    bool CanPrevious = false,
    bool CanNext = false,
    int SkipBackSeconds = 10,
    int SkipForwardSeconds = 30,
    bool ShuffleEnabled = false,
    string RepeatMode = PlayerRepeatModes.Off,
    double PlaybackRate = 1d,
    bool HasChapters = false,
    bool HasEpisodes = false,
    bool CanBookmark = false,
    bool CanFullscreen = true,
    bool IsFullscreen = false,
    bool IsSleepTimerActive = false,
    bool HasQueue = true,
    bool Disabled = false,
    string? ActiveSheet = null);

public static partial class PlaybackControlCatalog
{
    /// <summary>
    /// The transport of the persistent bar (unified player v2 D3). Audiobooks, TV and movies: Previous · Skip back · Play/pause · Skip forward · Next.
    /// Music: Shuffle · Previous · Play/pause · Next · Repeat, with no skip-by-seconds buttons. Previous and Next that cannot act are returned
    /// disabled, in place, never left out (A13).
    /// </summary>
    public static IReadOnlyList<PlaybackControlDefinition> BuildBarTransport(PlaybackBarMedia media, PlaybackBarState state)
    {
        var playing = state.IsPlaying && !state.NeedsUserGestureToStart;
        var play = new PlaybackControlDefinition(
            PlaybackControlKey.PlayPause,
            playing ? "Pause" : "Play",
            playing ? "Pause" : "Play",
            string.Empty,
            PlaybackControlPlacement.Transport,
            "toggle-play",
            IsDisabled: state.Disabled);
        var previous = BarStep(media, isNext: false, enabled: state.CanPrevious);
        var next = BarStep(media, isNext: true, enabled: state.CanNext);

        if (media == PlaybackBarMedia.Music)
        {
            var repeat = BarRepeat(state.RepeatMode);
            return
            [
                new(PlaybackControlKey.Shuffle, "Shuffle", state.ShuffleEnabled ? "Shuffle on" : "Shuffle off", string.Empty,
                    PlaybackControlPlacement.Transport, "shuffle", IsActive: state.ShuffleEnabled, IsSelected: state.ShuffleEnabled),
                previous,
                play,
                next,
                repeat,
            ];
        }

        var back = Math.Clamp(state.SkipBackSeconds, 1, 999);
        var forward = Math.Clamp(state.SkipForwardSeconds, 1, 999);
        return
        [
            previous,
            new(PlaybackControlKey.SkipBack, $"Skip back {back} s", $"Skip back {back} seconds", string.Empty,
                PlaybackControlPlacement.Transport, "skip-back", ValueText: back.ToString(System.Globalization.CultureInfo.InvariantCulture), IsDisabled: state.Disabled),
            play,
            new(PlaybackControlKey.SkipForward, $"Skip forward {forward} s", $"Skip forward {forward} seconds", string.Empty,
                PlaybackControlPlacement.Transport, "skip-forward", ValueText: forward.ToString(System.Globalization.CultureInfo.InvariantCulture), IsDisabled: state.Disabled),
            next,
        ];
    }

    /// <summary>
    /// The tools of the persistent bar's right zone, in the order of unified player v2 D4. A tool shows only when the media and the app support
    /// it; nothing here is a dead button, and the space of a missing tool stays empty.
    /// </summary>
    public static IReadOnlyList<PlaybackControlDefinition> BuildBarTools(PlaybackBarMedia media, PlaybackBarState state)
    {
        var tools = new List<PlaybackControlDefinition>();
        switch (media)
        {
            case PlaybackBarMedia.Music:
                tools.Add(BarTool(PlaybackControlKey.Lyrics, "Lyrics", "lyrics", state));
                if (state.HasQueue)
                {
                    tools.Add(BarTool(PlaybackControlKey.Queue, "Queue", "queue", state));
                }
                if (state.CanBookmark)
                {
                    tools.Add(BarTool(PlaybackControlKey.Bookmarks, "Bookmarks", "bookmarks", state));
                }
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
                if (state.HasEpisodes)
                {
                    tools.Add(BarTool(PlaybackControlKey.Episodes, "Episodes", "episodes", state));
                }
                tools.Add(BarTool(PlaybackControlKey.AudioSubtitles, "Audio & subtitles", "audio-subtitles", state));
                if (state.CanBookmark)
                {
                    tools.Add(BarTool(PlaybackControlKey.Bookmarks, "Bookmarks", "bookmarks", state));
                }
                break;
        }

        tools.Add(new(PlaybackControlKey.Volume, "Volume", "Volume", string.Empty, PlaybackControlPlacement.Utility, "set-volume"));

        if (media is PlaybackBarMedia.Music or PlaybackBarMedia.Audiobook)
        {
            tools.Add(BarTool(PlaybackControlKey.NowPlaying, "Now playing", "now-playing", state));
        }
        else
        {
            tools.Add(BarTool(PlaybackControlKey.Settings, "Playback settings", "playback-settings", state));
            if (state.CanFullscreen)
            {
                var label = state.IsFullscreen ? "Exit fullscreen" : "Fullscreen";
                tools.Add(new(PlaybackControlKey.Fullscreen, label, label, string.Empty, PlaybackControlPlacement.Utility, "fullscreen", IsActive: state.IsFullscreen));
            }
        }

        tools.Add(new(PlaybackControlKey.Close, "Close", "Close", string.Empty, PlaybackControlPlacement.Utility, "close"));
        return tools;
    }

    private static PlaybackControlDefinition BarStep(PlaybackBarMedia media, bool isNext, bool enabled)
    {
        var label = media switch
        {
            PlaybackBarMedia.Audiobook => isNext ? "Next chapter" : "Previous chapter",
            PlaybackBarMedia.Tv => isNext ? "Next episode" : "Previous episode",
            PlaybackBarMedia.Movie => isNext ? "Next" : "Previous",
            _ => isNext ? "Next track" : "Previous track",
        };
        var command = isNext
            ? media == PlaybackBarMedia.Audiobook ? "play-next-chapter" : "play-next"
            : media == PlaybackBarMedia.Audiobook ? "play-previous-chapter" : "play-previous";
        return new(isNext ? PlaybackControlKey.NextItem : PlaybackControlKey.PreviousItem, label, label, string.Empty,
            PlaybackControlPlacement.Transport, command, IsDisabled: !enabled);
    }

    private static PlaybackControlDefinition BarRepeat(string? mode)
    {
        var (label, active, badge) = mode switch
        {
            PlayerRepeatModes.One => ("Repeat one", true, "1"),
            PlayerRepeatModes.All => ("Repeat all", true, null),
            _ => ("Repeat off", false, (string?)null),
        };
        return new(PlaybackControlKey.Repeat, "Repeat", label, string.Empty, PlaybackControlPlacement.Transport, "repeat",
            BadgeText: badge, IsActive: active, IsSelected: active);
    }

    private static PlaybackControlDefinition BarTool(
        PlaybackControlKey key,
        string label,
        string sheet,
        PlaybackBarState state,
        string? valueText = null,
        bool isActive = false)
    {
        return new(
            key,
            label,
            valueText is null ? label : $"{label} {valueText}",
            string.Empty,
            PlaybackControlPlacement.ToolStrip,
            $"open-{sheet}",
            sheet,
            valueText,
            IsActive: isActive,
            IsSelected: string.Equals(state.ActiveSheet, sheet, StringComparison.Ordinal));
    }
}
