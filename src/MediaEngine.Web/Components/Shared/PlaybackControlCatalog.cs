using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Formatting;
using MediaEngine.Web.Services.Playback;
using MediaEngine.Web.Services.Ui;

namespace MediaEngine.Web.Components.Shared;

public enum PlaybackControlKey
{
    PlayPause,
    Timeline,
    PreviousItem,
    NextItem,
    SkipBack,
    SkipForward,
    Volume,
    Mute,
    Queue,
    History,
    Lyrics,
    Chapters,
    Bookmarks,
    SleepTimer,
    Speed,
    Cast,
    More,
    Captions,
    AudioTrack,
    Quality,
    Fullscreen,
    PictureInPicture,
    Shuffle,
    Repeat,
    Favorite,
    Close,
    Expand,
    Resume,
    SkipIntro,
    SkipCredits,
}

public enum PlaybackControlSurface
{
    /// <summary>Layout-owned compact audio dock.</summary>
    Bottom,
    NowPlaying,
    PrimaryVideo,
    PictureInPicture,
    SidePanel,
    Popup,
    Fullscreen,
    Expanded,
    Phone,
}

public enum PlaybackControlPlacement
{
    Transport,
    Timeline,
    Utility,
    ToolStrip,
}

public sealed record PlaybackControlState(
    string? ActiveSheet = null,
    double PlaybackRate = 1d,
    bool IsPlaying = false,
    bool IsMuted = false,
    bool HasChapters = false,
    bool HasQueue = true,
    bool HasLyrics = false,
    bool HasCaptions = false,
    bool HasAudioTracks = false,
    bool HasQualityOptions = false,
    bool CanPrevious = false,
    bool CanNext = false,
    bool CanCast = false,
    bool CanPictureInPicture = false,
    bool CanFullscreen = false,
    bool CanFindCaptions = false,
    bool CanFindLyrics = false,
    bool IsTvEpisode = false,
    bool IsShuffleEnabled = false,
    bool IsRepeatEnabled = false,
    bool IsSleepTimerActive = false,
    string? SleepTimerValueText = null,
    int SkipBackSeconds = 15,
    int SkipForwardSeconds = 15,
    int HistoryCount = 0,
    string? RepeatMode = null);

public sealed record PlaybackControlDefinition(
    PlaybackControlKey Key,
    string Label,
    string AriaLabel,
    string Icon,
    PlaybackControlPlacement Placement,
    string Command,
    string? Sheet = null,
    string? ValueText = null,
    string? BadgeText = null,
    bool IsActive = false,
    bool IsDisabled = false,
    bool IsSelected = false);

public static class PlaybackControlCatalog
{
    public static IReadOnlyList<PlaybackControlDefinition> Build(
        PlaybackExperience experience,
        PlaybackControlSurface surface,
        PlaybackControlState state)
    {
        var controls = new List<PlaybackControlDefinition>
        {
            new(PlaybackControlKey.PlayPause, state.IsPlaying ? "Pause" : "Play", state.IsPlaying ? "Pause" : "Play", state.IsPlaying ? AppMaterialIcons.Filled.Pause : AppMaterialIcons.Filled.PlayArrow, PlaybackControlPlacement.Transport, "toggle-play"),
            new(PlaybackControlKey.Timeline, "Playback position", "Playback position", AppMaterialIcons.Outlined.Timeline, PlaybackControlPlacement.Timeline, "seek"),
        };

        if (state.CanPrevious)
        {
            controls.Add(PreviousNext(experience, isNext: false));
        }
        if (state.CanNext)
        {
            controls.Add(Next(experience));
        }

        if (experience is PlaybackExperience.Audiobook or PlaybackExperience.Video)
        {
            var back = Math.Clamp(state.SkipBackSeconds, 1, 999);
            var forward = Math.Clamp(state.SkipForwardSeconds, 1, 999);
            controls.Add(new(PlaybackControlKey.SkipBack, $"Back {back}", $"Skip back {back} seconds", AppMaterialIcons.Outlined.Replay, PlaybackControlPlacement.Transport, "skip-back", ValueText: back.ToString()));
            controls.Add(new(PlaybackControlKey.SkipForward, $"Forward {forward}", $"Skip forward {forward} seconds", AppMaterialIcons.Outlined.Forward, PlaybackControlPlacement.Transport, "skip-forward", ValueText: forward.ToString()));
        }

        if (surface == PlaybackControlSurface.PictureInPicture)
        {
            if (experience == PlaybackExperience.Video)
            {
                controls.Add(new(PlaybackControlKey.Resume, "Restore", "Restore video", string.Empty, PlaybackControlPlacement.Utility, "restore-video"));
            }
            return controls.Where(control => control.Key is PlaybackControlKey.PlayPause or PlaybackControlKey.Timeline or PlaybackControlKey.SkipBack or PlaybackControlKey.SkipForward or PlaybackControlKey.Resume).ToList();
        }

        controls.Add(new(PlaybackControlKey.Mute, state.IsMuted ? "Unmute" : "Mute", state.IsMuted ? "Unmute" : "Mute", string.Empty, PlaybackControlPlacement.Utility, "toggle-mute", IsActive: state.IsMuted));
        controls.Add(new(PlaybackControlKey.Volume, "Volume", "Volume", string.Empty, PlaybackControlPlacement.Utility, "set-volume"));
        if (state.CanCast)
        {
            controls.Add(new(PlaybackControlKey.Cast, "Device", "Playback device", string.Empty, PlaybackControlPlacement.Utility, "cast"));
        }

        AddExperienceControls(controls, experience, surface, state);
        return controls;
    }

    public static IReadOnlyList<PlaybackControlDefinition> BuildToolStrip(
        PlaybackExperience experience,
        PlaybackControlSurface surface,
        PlaybackControlState state)
    {
        return Build(experience, surface, state)
            .Where(control => control.Placement == PlaybackControlPlacement.ToolStrip)
            .ToList();
    }

    /// <summary>Returns the high priority tool order for the requested player surface.</summary>
    public static IReadOnlyList<PlaybackControlDefinition> BuildPrimaryToolStrip(
        PlaybackExperience experience,
        PlaybackControlSurface surface,
        PlaybackControlState state)
    {
        var tools = BuildToolStrip(experience, surface, state);
        var order = experience switch
        {
            PlaybackExperience.Music when surface is PlaybackControlSurface.Bottom =>
                new[] { PlaybackControlKey.Queue, PlaybackControlKey.History, PlaybackControlKey.Lyrics, PlaybackControlKey.Shuffle, PlaybackControlKey.Repeat },
            PlaybackExperience.Music =>
                new[] { PlaybackControlKey.Queue, PlaybackControlKey.Lyrics, PlaybackControlKey.Shuffle, PlaybackControlKey.Repeat },
            PlaybackExperience.Video =>
                new[] { PlaybackControlKey.Captions, PlaybackControlKey.AudioTrack, PlaybackControlKey.Speed, PlaybackControlKey.Queue },
            _ => tools.Select(control => control.Key).ToArray(),
        };

        return OrderByKeys(tools.Where(control => order.Contains(control.Key)), order);
    }

    /// <summary>Returns the direct audiobook dock tools in their visual order.</summary>
    public static IReadOnlyList<PlaybackControlDefinition> BuildAudiobookDockToolStrip(PlaybackControlState state)
    {
        var tools = BuildToolStrip(PlaybackExperience.Audiobook, PlaybackControlSurface.NowPlaying, state).ToList();
        if (tools.All(control => control.Key != PlaybackControlKey.Chapters))
        {
            tools.Add(Tool(
                PlaybackControlKey.Chapters,
                "Chapters",
                "chapters",
                state,
                IsDisabled: true) with
            { AriaLabel = "Chapters unavailable; no timed chapters were found" });
        }

        var order = new[]
        {
            PlaybackControlKey.Chapters,
            PlaybackControlKey.Bookmarks,
            PlaybackControlKey.Speed,
            PlaybackControlKey.SleepTimer,
            PlaybackControlKey.History,
        };
        return OrderByKeys(tools, order);
    }

    /// <summary>Returns real available tools that belong in a secondary or overflow surface.</summary>
    public static IReadOnlyList<PlaybackControlDefinition> BuildSecondaryToolStrip(
        PlaybackExperience experience,
        PlaybackControlSurface surface,
        PlaybackControlState state)
    {
        var tools = BuildToolStrip(experience, surface, state);
        var primaryKeys = BuildPrimaryToolStrip(experience, surface, state).Select(control => control.Key).ToHashSet();
        return tools.Where(control => !primaryKeys.Contains(control.Key)).ToList();
    }

    /// <summary>Returns controls outside the primary tool strip, keeping video Fullscreen last.</summary>
    public static IReadOnlyList<PlaybackControlDefinition> BuildUtilityControls(
        PlaybackExperience experience,
        PlaybackControlSurface surface,
        PlaybackControlState state)
    {
        var controls = Build(experience, surface, state)
            .Where(control => control.Placement == PlaybackControlPlacement.Utility)
            .ToList();
        return controls
            .OrderBy(control => experience == PlaybackExperience.Video && control.Key == PlaybackControlKey.Fullscreen ? 1 : 0)
            .ToList();
    }

    private static IReadOnlyList<PlaybackControlDefinition> OrderByKeys(
        IEnumerable<PlaybackControlDefinition> controls,
        IReadOnlyList<PlaybackControlKey> keys)
    {
        var byKey = controls.ToDictionary(control => control.Key);
        return keys.Where(byKey.ContainsKey).Select(key => byKey[key]).ToList();
    }

    private static PlaybackControlDefinition PreviousNext(PlaybackExperience experience, bool isNext)
    {
        var label = experience switch
        {
            PlaybackExperience.Audiobook => isNext ? "Next chapter" : "Previous chapter",
            PlaybackExperience.Video => isNext ? "Next" : "Previous",
            _ => isNext ? "Next track" : "Previous track",
        };

        var key = isNext ? PlaybackControlKey.NextItem : PlaybackControlKey.PreviousItem;
        var icon = isNext ? AppMaterialIcons.Filled.SkipNext : AppMaterialIcons.Filled.SkipPrevious;
        var command = isNext
            ? experience == PlaybackExperience.Audiobook ? "play-next-chapter" : "play-next"
            : experience == PlaybackExperience.Audiobook ? "play-previous-chapter" : "play-previous";

        return new(key, label, label, icon, PlaybackControlPlacement.Transport, command);
    }

    private static PlaybackControlDefinition Next(PlaybackExperience experience) => PreviousNext(experience, isNext: true);

    private static void AddExperienceControls(
        ICollection<PlaybackControlDefinition> controls,
        PlaybackExperience experience,
        PlaybackControlSurface surface,
        PlaybackControlState state)
    {
        switch (experience)
        {
            case PlaybackExperience.Music:
                if (state.HasQueue)
                {
                    controls.Add(Tool(PlaybackControlKey.Queue, "Queue", "queue", state));
                }
                controls.Add(Tool(PlaybackControlKey.History, "History", "history", state));
                if (state.HasLyrics || state.CanFindLyrics)
                {
                    controls.Add(Tool(PlaybackControlKey.Lyrics, "Lyrics", "lyrics", state));
                }
                controls.Add(new(PlaybackControlKey.Shuffle, "Shuffle", state.IsShuffleEnabled ? "Shuffle on" : "Shuffle off", string.Empty, PlaybackControlPlacement.ToolStrip, "shuffle", IsActive: state.IsShuffleEnabled));
                controls.Add(new(PlaybackControlKey.Repeat, "Repeat", state.RepeatMode == "one" ? "Repeat one" : state.IsRepeatEnabled ? "Repeat all" : "Repeat off", string.Empty, PlaybackControlPlacement.ToolStrip, "repeat", IsActive: state.IsRepeatEnabled));
                break;
            case PlaybackExperience.Audiobook:
                controls.Add(Tool(PlaybackControlKey.Speed, "Speed", "speed", state, ValueText: DisplayFormat.FormatSpeedControl(state.PlaybackRate)));
                if (state.HasChapters)
                {
                    controls.Add(Tool(PlaybackControlKey.Chapters, "Chapters", "chapters", state));
                }
                controls.Add(Tool(PlaybackControlKey.History, "History", "history", state));
                controls.Add(Tool(PlaybackControlKey.Bookmarks, "Bookmark", "bookmark-dialog", state));
                var sleepTimer = new PlaybackControlDefinition(
                    PlaybackControlKey.SleepTimer,
                    "Sleep",
                    "Sleep timer",
                    string.Empty,
                    PlaybackControlPlacement.ToolStrip,
                    "sleep-timer",
                    ValueText: state.SleepTimerValueText,
                    BadgeText: state.SleepTimerValueText,
                    IsActive: state.IsSleepTimerActive);
                if (state.IsSleepTimerActive)
                {
                    var status = state.SleepTimerValueText switch
                    {
                        "Ch" => "ends at the current chapter boundary",
                        "NEXT" => "ends at the next chapter boundary",
                        _ => $"{state.SleepTimerValueText ?? "active"} remaining",
                    };
                    sleepTimer = sleepTimer with { AriaLabel = $"Sleep timer active, {status}" };
                }
                controls.Add(sleepTimer);
                break;
            case PlaybackExperience.Video:
                controls.Add(Tool(PlaybackControlKey.Speed, "Speed", "speed", state, ValueText: DisplayFormat.FormatSpeedControl(state.PlaybackRate)));
                if (state.HasChapters)
                {
                    controls.Add(Tool(PlaybackControlKey.Chapters, "Chapters", "chapters", state));
                }
                if (state.IsTvEpisode && state.HasQueue)
                {
                    controls.Add(Tool(PlaybackControlKey.Queue, "Next Up", "queue", state));
                }
                if (state.HasCaptions || state.CanFindCaptions)
                {
                    controls.Add(Tool(PlaybackControlKey.Captions, "Captions", "captions", state));
                }
                if (state.HasAudioTracks)
                {
                    controls.Add(Tool(PlaybackControlKey.AudioTrack, "Audio", "audio-track", state));
                }
                if (state.HasQualityOptions)
                {
                    controls.Add(Tool(PlaybackControlKey.Quality, "Quality", "quality", state));
                }
                if (state.CanFullscreen)
                {
                    controls.Add(new(PlaybackControlKey.Fullscreen, "Fullscreen", "Fullscreen", string.Empty, PlaybackControlPlacement.Utility, "fullscreen"));
                }
                if (state.CanPictureInPicture)
                {
                    controls.Add(new(PlaybackControlKey.PictureInPicture, "PiP", "Picture in picture", string.Empty, PlaybackControlPlacement.Utility, "picture-in-picture"));
                }
                break;
        }
    }

    private static PlaybackControlDefinition Tool(
        PlaybackControlKey key,
        string label,
        string sheet,
        PlaybackControlState state,
        string? ValueText = null,
        string? BadgeText = null,
        bool IsActive = false,
        bool IsDisabled = false)
    {
        return new(
            key,
            label,
            ValueText is null ? label : $"{label} {ValueText}",
            string.Empty,
            PlaybackControlPlacement.ToolStrip,
            $"open-{sheet}",
            sheet,
            ValueText,
            BadgeText,
            IsActive,
            IsDisabled,
            string.Equals(state.ActiveSheet, sheet, StringComparison.Ordinal));
    }

}
