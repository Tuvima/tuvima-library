using MediaEngine.Web.Services.Formatting;
using MediaEngine.Web.Services.Playback;
using MudBlazor;

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
    int HistoryCount = 0,
    int BookmarkCount = 0);

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
            new(PlaybackControlKey.PlayPause, state.IsPlaying ? "Pause" : "Play", "Play or pause", state.IsPlaying ? Icons.Material.Filled.Pause : Icons.Material.Filled.PlayArrow, PlaybackControlPlacement.Transport, "toggle-play"),
            new(PlaybackControlKey.Timeline, "Playback position", "Playback position", Icons.Material.Outlined.Timeline, PlaybackControlPlacement.Timeline, "seek"),
        };

        if (state.CanPrevious) controls.Add(PreviousNext(experience, isNext: false));
        if (state.CanNext) controls.Add(Next(experience));

        if (experience is PlaybackExperience.Audiobook or PlaybackExperience.Video)
        {
            controls.Add(new(PlaybackControlKey.SkipBack, "Back", "Skip back", Icons.Material.Outlined.Replay10, PlaybackControlPlacement.Transport, "skip-back"));
            controls.Add(new(PlaybackControlKey.SkipForward, "Forward", "Skip forward", Icons.Material.Outlined.Forward10, PlaybackControlPlacement.Transport, "skip-forward"));
        }

        controls.Add(new(PlaybackControlKey.Mute, state.IsMuted ? "Unmute" : "Mute", "Mute or unmute", state.IsMuted ? Icons.Material.Outlined.VolumeOff : Icons.Material.Outlined.VolumeUp, PlaybackControlPlacement.Utility, "toggle-mute", IsActive: state.IsMuted));
        controls.Add(new(PlaybackControlKey.Volume, "Volume", "Volume", Icons.Material.Outlined.VolumeUp, PlaybackControlPlacement.Utility, "set-volume"));
        if (state.CanCast)
        {
            controls.Add(new(PlaybackControlKey.Cast, "Device", "Playback device", Icons.Material.Outlined.Cast, PlaybackControlPlacement.Utility, "cast"));
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

    private static PlaybackControlDefinition PreviousNext(PlaybackExperience experience, bool isNext)
    {
        var label = experience switch
        {
            PlaybackExperience.Audiobook => isNext ? "Next chapter" : "Previous chapter",
            PlaybackExperience.Video => isNext ? "Next" : "Previous",
            _ => isNext ? "Next track" : "Previous track",
        };

        var key = isNext ? PlaybackControlKey.NextItem : PlaybackControlKey.PreviousItem;
        var icon = isNext ? Icons.Material.Filled.SkipNext : Icons.Material.Filled.SkipPrevious;
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
                if (state.HasQueue) controls.Add(Tool(PlaybackControlKey.Queue, "Queue", Icons.Material.Outlined.QueueMusic, "queue", state));
                controls.Add(Tool(PlaybackControlKey.History, "History", Icons.Material.Outlined.History, "history", state));
                if (state.HasLyrics || state.CanFindLyrics) controls.Add(Tool(PlaybackControlKey.Lyrics, "Lyrics", Icons.Material.Outlined.Lyrics, "lyrics", state));
                controls.Add(new(PlaybackControlKey.Shuffle, "Shuffle", "Shuffle", Icons.Material.Outlined.Shuffle, PlaybackControlPlacement.ToolStrip, "shuffle", IsActive: state.IsShuffleEnabled));
                controls.Add(new(PlaybackControlKey.Repeat, "Repeat", "Repeat", Icons.Material.Outlined.Repeat, PlaybackControlPlacement.ToolStrip, "repeat", IsActive: state.IsRepeatEnabled));
                break;
            case PlaybackExperience.Audiobook:
                controls.Add(Tool(PlaybackControlKey.Speed, "Speed", Icons.Material.Outlined.Speed, "speed", state, ValueText: DisplayFormat.FormatSpeedControl(state.PlaybackRate)));
                if (state.HasChapters) controls.Add(Tool(PlaybackControlKey.Chapters, "Chapters", Icons.Material.Outlined.FormatListBulleted, "chapters", state));
                controls.Add(Tool(PlaybackControlKey.History, "History", Icons.Material.Outlined.History, "history", state));
                controls.Add(Tool(PlaybackControlKey.Bookmarks, "Bookmark", Icons.Material.Outlined.BookmarkBorder, "bookmarks", state));
                controls.Add(Tool(PlaybackControlKey.SleepTimer, "Sleep", Icons.Material.Outlined.Timer, "sleep", state, BadgeText: state.SleepTimerValueText, IsActive: state.IsSleepTimerActive));
                break;
            case PlaybackExperience.Video:
                controls.Add(Tool(PlaybackControlKey.Speed, "Speed", Icons.Material.Outlined.Speed, "speed", state, ValueText: DisplayFormat.FormatSpeedControl(state.PlaybackRate)));
                if (state.HasChapters) controls.Add(Tool(PlaybackControlKey.Chapters, "Chapters", Icons.Material.Outlined.FormatListBulleted, "chapters", state));
                if (state.IsTvEpisode && state.HasQueue) controls.Add(Tool(PlaybackControlKey.Queue, "Next Up", Icons.Material.Outlined.QueuePlayNext, "queue", state));
                controls.Add(Tool(PlaybackControlKey.History, "History", Icons.Material.Outlined.History, "history", state));
                if (state.HasCaptions || state.CanFindCaptions) controls.Add(Tool(PlaybackControlKey.Captions, "Captions", Icons.Material.Outlined.ClosedCaption, "captions", state));
                if (state.HasAudioTracks) controls.Add(Tool(PlaybackControlKey.AudioTrack, "Audio", Icons.Material.Outlined.RecordVoiceOver, "audio-track", state));
                if (state.HasQualityOptions) controls.Add(Tool(PlaybackControlKey.Quality, "Quality", Icons.Material.Outlined.HighQuality, "quality", state));
                if (state.CanFullscreen) controls.Add(new(PlaybackControlKey.Fullscreen, "Fullscreen", "Fullscreen", Icons.Material.Outlined.Fullscreen, PlaybackControlPlacement.Utility, "fullscreen"));
                if (state.CanPictureInPicture) controls.Add(new(PlaybackControlKey.PictureInPicture, "PiP", "Picture in picture", Icons.Material.Outlined.PictureInPictureAlt, PlaybackControlPlacement.Utility, "picture-in-picture"));
                if (surface == PlaybackControlSurface.PictureInPicture)
                {
                    controls.Add(new(PlaybackControlKey.Resume, "Restore", "Restore video", Icons.Material.Outlined.PlayCircle, PlaybackControlPlacement.Utility, "restore-video"));
                }
                break;
        }
    }

    private static PlaybackControlDefinition Tool(
        PlaybackControlKey key,
        string label,
        string icon,
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
            icon,
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
