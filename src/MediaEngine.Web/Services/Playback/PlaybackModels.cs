using System.Text.Json.Serialization;
using MediaEngine.Contracts.Playback;
using MediaEngine.Domain.Services;
using MediaEngine.Web.Models.ViewDTOs;

namespace MediaEngine.Web.Services.Playback;

public enum PlaybackExperience
{
    Music,
    Audiobook,
    Video,
}

public enum PlaybackPresentationSurface
{
    Docked,
    NowPlaying,
    PrimaryVideo,
    PictureInPicture,
    RestorableVideo,
    Fullscreen,
}

public enum PlaybackPhase
{
    Idle,
    Loading,
    Ready,
    Playing,
    Paused,
    Ended,
    Error,
    NeedsGesture,
}

public enum PlaybackChangeKind
{
    State,
    Queue,
    TransportTick,
    TransportState,
    Ui,
    Audiobook,
    Video,
    View,
    Error,
}

public enum ViewPlaybackKind
{
    Video,
    Audio,
}

/// <summary>
/// A personal View asset is not a catalogue work. Its authorized stream URL stays with
/// the viewer transport and is never persisted in a catalogue playback queue.
/// </summary>
public sealed record ViewPlaybackSessionState(
    Guid AssetId,
    ViewPlaybackKind Kind,
    double PositionSeconds = 0,
    double DurationSeconds = 0,
    bool IsPlaying = false,
    double Volume = 1,
    bool IsMuted = false,
    double PlaybackRate = 1,
    PlaybackPresentationSurface PresentationSurface = PlaybackPresentationSurface.PrimaryVideo);

public enum PlaybackCommandKind
{
    TogglePlay,
    Pause,
    PlayNext,
    PlayPrevious,
    PlayNextChapter,
    PlayPreviousChapter,
    SkipRelative,
    Seek,
    SetVolume,
    SetSpeed,
    ToggleMute,
    TogglePanel,
    ClosePanel,
    SetActiveTab,
    ClearUpcoming,
    RemoveUpcoming,
    PlayIndex,
    PlayHistory,
    PlayQueueItem,
    PlayAudiobookChapter,
    PlayAudiobookHistory,
    SetPopupOpen,
    ClosePlayer,
    RestoreState,
    UpdateTransportState,
    MarkPlaybackStarted,
    MarkNeedsUserGestureToStart,
    ReportHeartbeat,
}

public sealed record PlaybackCommand(
    PlaybackCommandKind Kind,
    double? Value = null,
    int? Index = null,
    string? Text = null,
    bool? Flag = null,
    TimeSpan? Duration = null,
    Guid? Id = null,
    ListenQueueItem? Item = null,
    ListenPlaybackSnapshot? Snapshot = null,
    AudioTransportState? AudioState = null,
    AudiobookListenHistoryItemDto? AudiobookHistoryItem = null,
    CancellationToken CancellationToken = default)
{
    public static PlaybackCommand TogglePlay() => new(PlaybackCommandKind.TogglePlay);
    public static PlaybackCommand Pause() => new(PlaybackCommandKind.Pause);
    public static PlaybackCommand Seek(double seconds) => new(PlaybackCommandKind.Seek, Value: seconds);
    public static PlaybackCommand SkipRelative(double seconds) => new(PlaybackCommandKind.SkipRelative, Value: seconds);
    public static PlaybackCommand SetVolume(double volume) => new(PlaybackCommandKind.SetVolume, Value: volume);
    public static PlaybackCommand SetSpeed(double speed) => new(PlaybackCommandKind.SetSpeed, Value: speed);
}

public sealed record PlaybackTransportCommand(
    string Action,
    double? Value = null,
    string? StreamUrl = null,
    double? PositionSeconds = null,
    double? PlaybackRate = null,
    long? RequestId = null,
    string? AudiobookStartKind = null,
    Guid? ExpectedAssetId = null,
    long? ExpectedPlaybackRequestVersion = null);

public sealed record PlaybackClientContext(
    string DeviceId,
    string DeviceName,
    string Client,
    string AppVersion,
    string DeviceClass)
{
    public string ConnectionPath { get; init; } = PlaybackConnectionPaths.Local;
    public string? RemoteConnectivityProvider { get; init; }
    public double? EstimatedBandwidthMbps { get; init; }
    public int? LatencyMs { get; init; }
    public Guid? RoomId { get; init; }

    public static PlaybackClientContext WebDefault { get; } = new(
        DeviceId: "web",
        DeviceName: "Dashboard",
        Client: "web",
        AppVersion: "dashboard",
        DeviceClass: "web");

    public PlaybackClientContext Normalize() => this with
    {
        DeviceId = string.IsNullOrWhiteSpace(DeviceId) ? WebDefault.DeviceId : DeviceId.Trim(),
        DeviceName = string.IsNullOrWhiteSpace(DeviceName) ? WebDefault.DeviceName : DeviceName.Trim(),
        Client = string.IsNullOrWhiteSpace(Client) ? WebDefault.Client : Client.Trim().ToLowerInvariant(),
        AppVersion = string.IsNullOrWhiteSpace(AppVersion) ? WebDefault.AppVersion : AppVersion.Trim(),
        DeviceClass = string.IsNullOrWhiteSpace(DeviceClass) ? WebDefault.DeviceClass : DeviceClass.Trim().ToLowerInvariant(),
        ConnectionPath = PlaybackConnectionPaths.IsKnown(ConnectionPath) ? ConnectionPath : PlaybackConnectionPaths.Local,
        RemoteConnectivityProvider = string.IsNullOrWhiteSpace(RemoteConnectivityProvider) ? null : RemoteConnectivityProvider.Trim(),
        EstimatedBandwidthMbps = EstimatedBandwidthMbps is > 0 ? Math.Min(EstimatedBandwidthMbps.Value, 100_000) : null,
        LatencyMs = LatencyMs is >= 0 ? Math.Min(LatencyMs.Value, 120_000) : null,
    };
}

public sealed record AudioTransportState(
    double? CurrentTimeSeconds = null,
    double? DurationSeconds = null,
    bool? IsPlaying = null,
    double? Volume = null,
    bool? IsMuted = null,
    double? PlaybackRate = null,
    bool? NeedsUserGestureToStart = null);

public sealed record PlaybackSessionState
{
    public ViewPlaybackSessionState? ViewSession { get; init; }
    public IReadOnlyList<ListenQueueItem> Queue { get; init; } = [];
    public IReadOnlyList<ListenQueueItem> History { get; init; } = [];
    public IReadOnlyList<ListenQueueItem> UpcomingQueue { get; init; } = [];
    public IReadOnlyList<AudiobookListenHistoryItemDto> AudiobookHistory { get; init; } = [];
    public Guid? AudiobookBookSessionLeaseId { get; init; }
    public long AudiobookBookSessionGeneration { get; init; }
    public int CurrentIndex { get; init; } = -1;
    public string? SourceLabel { get; init; }
    public bool IsPanelOpen { get; init; }
    public string ActiveTab { get; init; } = ListenPlaybackTabs.Queue;
    public bool IsDismissed { get; init; }
    public double CurrentTimeSeconds { get; init; }
    public double DurationSeconds { get; init; }
    public double Volume { get; init; }
    public bool IsMuted { get; init; }
    public bool IsPlaying { get; init; }
    public double PlaybackRate { get; init; }
    public bool ShuffleEnabled { get; init; }
    public string RepeatMode { get; init; } = PlayerRepeatModes.Off;
    public long PlaybackStartVersion { get; init; }
    public PlaybackExperience Experience { get; init; } = PlaybackExperience.Music;
    public PlaybackPhase Phase { get; init; } = PlaybackPhase.Idle;
    public bool NeedsUserGestureToStart { get; init; }
    public bool IsPopupOpen { get; init; }
    public bool IsVideoExpanded { get; init; }
    public PlaybackPresentationSurface PresentationSurface { get; init; } = PlaybackPresentationSurface.Docked;
    public string? CurrentError { get; init; }
    public int SkipBackSeconds { get; init; }
    public int SkipForwardSeconds { get; init; }
    public int ResumeRewindSeconds { get; init; }
    public int AudiobookNearStartGuardSeconds { get; init; }
    public IReadOnlyList<int> SleepTimerOptionsMinutes { get; init; } = [];
    public AudiobookSleepTimerStateDto SleepTimerState { get; init; } = new();
    public AudiobookSleepTimerAvailabilityDto SleepTimerAvailability { get; init; } = new();
    public ListenQueueItem? CurrentItem { get; init; }
    public string? CurrentStreamUrl { get; init; }
    public string? CurrentBrowserStreamUrl { get; init; }
    public PlaybackChapterDto? CurrentChapter { get; init; }
    public PlaybackClientContext ClientContext { get; init; } = PlaybackClientContext.WebDefault;

    public bool HasQueue => Queue.Count > 0 && !IsDismissed;
    public bool IsAudiobookMode => Experience == PlaybackExperience.Audiobook;
    public bool IsMusicMode => Experience == PlaybackExperience.Music;
    public bool IsVideoMode => Experience == PlaybackExperience.Video;
    public string ExperienceMode => MediaKindClassifier.ToPlayerExperienceString(Experience);
}

public static class ListenPlaybackTabs
{
    public const string Queue = "queue";
    public const string History = "history";
    public const string Lyrics = "lyrics";
}

public sealed record AudiobookStartRequest(
    ListenQueueItem Item,
    string StartKind,
    double? PositionSeconds = null,
    int? ChapterIndex = null,
    string? SourceLabel = null,
    AudiobookStartIntent Intent = AudiobookStartIntent.Explicit);

public enum AudiobookStartIntent
{
    Explicit,
    Natural,
    CapturedPreview,
    BookmarkReplay,
}

public sealed record ListenQueueItem
{
    [JsonPropertyName("queue_entry_id")]
    public Guid QueueEntryId { get; init; } = Guid.NewGuid();
    [JsonPropertyName("persisted_queue_item_id")]
    public Guid? PersistedQueueItemId { get; init; }
    public Guid? AudiobookWorkId { get; init; }
    public Guid? AlbumWorkId { get; init; }
    public Guid? ArtistPersonId { get; init; }
    public Guid? PlaylistId { get; init; }
    public string? AuthorName { get; init; }
    public string? NarratorName { get; init; }
    public string? Synopsis { get; init; }
    public IReadOnlyList<PlaybackContributorIdentity> Authors { get; init; } = [];
    public IReadOnlyList<PlaybackContributorIdentity> Narrators { get; init; } = [];
    [JsonPropertyName("work_id")]
    public Guid WorkId { get; init; }

    [JsonPropertyName("collection_id")]
    public Guid? CollectionId { get; init; }

    [JsonPropertyName("media_type")]
    public string MediaType { get; init; } = string.Empty;

    [JsonIgnore]
    public PlaybackExperience PlaybackExperience => MediaKindClassifier.Classify(MediaType);

    [JsonPropertyName("title")]
    public string Title { get; init; } = string.Empty;

    [JsonPropertyName("subtitle")]
    public string? Subtitle { get; init; }

    [JsonPropertyName("album")]
    public string? Album { get; init; }

    [JsonPropertyName("year")]
    public string? Year { get; init; }

    [JsonPropertyName("content_rating")]
    public string? ContentRating { get; init; }

    [JsonPropertyName("season_number")]
    public string? SeasonNumber { get; init; }

    [JsonPropertyName("episode_number")]
    public string? EpisodeNumber { get; init; }

    [JsonPropertyName("episode_title")]
    public string? EpisodeTitle { get; init; }

    [JsonPropertyName("quality")]
    public string? Quality { get; init; }

    [JsonPropertyName("cover_url")]
    public string? CoverUrl { get; init; }

    [JsonPropertyName("duration")]
    public string? Duration { get; init; }

    [JsonPropertyName("asset_id")]
    public Guid? AssetId { get; init; }

    [JsonPropertyName("stream_url")]
    public string? StreamUrl { get; init; }

    [JsonPropertyName("initial_position_seconds")]
    public double? InitialPositionSeconds { get; init; }

    [JsonPropertyName("chapters")]
    public IReadOnlyList<PlaybackChapterDto> Chapters { get; init; } = [];

    [JsonPropertyName("manifest")]
    public PlaybackManifestDto? Manifest { get; init; }

    [JsonPropertyName("chapter_index")]
    public int? ChapterIndex { get; init; }

    [JsonPropertyName("start_at_exact_position")]
    public bool StartAtExactPosition { get; init; }

    [JsonPropertyName("audiobook_start_kind")]
    public string? AudiobookStartKind { get; init; }

    [JsonPropertyName("played_at")]
    public DateTimeOffset? PlayedAt { get; init; }
}

public sealed record PlaybackContributorIdentity(string Name, Guid? PersonId = null);

public sealed record ListenPlaybackSnapshot
{
    [JsonPropertyName("queue_revision")]
    public long QueueRevision { get; init; }
    public bool OutputSupported { get; init; }
    [JsonPropertyName("explicit_upcoming_order")]
    public bool ExplicitUpcomingOrder { get; init; }
    public long AudioOutputRevision { get; init; }
    public bool SoftwareVolumeSupported { get; init; } = true;

    [JsonPropertyName("lyrics_selection")]
    public PlaybackLyricsSelectionProjection? LyricsSelection { get; init; }

    [JsonPropertyName("profile_id")]
    public Guid? ProfileId { get; init; }
    private static readonly ListeningSettingsDto DefaultListening = new();

    [JsonPropertyName("queue")]
    public List<ListenQueueItem> Queue { get; init; } = [];

    [JsonPropertyName("history")]
    public List<ListenQueueItem> History { get; init; } = [];

    [JsonPropertyName("audiobook_history")]
    public List<AudiobookListenHistoryItemDto> AudiobookHistory { get; init; } = [];

    [JsonPropertyName("audiobook_session_lease_id")]
    public Guid? AudiobookBookSessionLeaseId { get; init; }

    [JsonPropertyName("audiobook_session_generation")]
    public long AudiobookBookSessionGeneration { get; init; }

    [JsonPropertyName("current_index")]
    public int CurrentIndex { get; init; }

    [JsonPropertyName("source_label")]
    public string? SourceLabel { get; init; }

    [JsonPropertyName("current_browser_stream_url")]
    public string? CurrentBrowserStreamUrl { get; init; }

    [JsonPropertyName("experience")]
    public string Experience { get; init; } = PlayerExperienceModes.Music;

    [JsonPropertyName("playback_request_version")]
    public long PlaybackRequestVersion { get; init; }

    [JsonPropertyName("is_panel_open")]
    public bool IsPanelOpen { get; init; }

    [JsonPropertyName("active_tab")]
    public string ActiveTab { get; init; } = ListenPlaybackTabs.Queue;

    [JsonPropertyName("is_dismissed")]
    public bool IsDismissed { get; init; }

    [JsonPropertyName("current_time_seconds")]
    public double CurrentTimeSeconds { get; init; }

    [JsonPropertyName("duration_seconds")]
    public double DurationSeconds { get; init; }

    [JsonPropertyName("volume")]
    public double Volume { get; init; } = 0.8d;

    [JsonPropertyName("is_muted")]
    public bool IsMuted { get; init; }

    [JsonPropertyName("playback_rate")]
    public double PlaybackRate { get; init; } = 1d;

    [JsonPropertyName("shuffle_enabled")]
    public bool ShuffleEnabled { get; init; }

    [JsonPropertyName("repeat_mode")]
    public string RepeatMode { get; init; } = PlayerRepeatModes.Off;

    [JsonPropertyName("needs_user_gesture_to_start")]
    public bool NeedsUserGestureToStart { get; init; }

    [JsonPropertyName("is_playing")]
    public bool IsPlaying { get; init; }

    [JsonPropertyName("is_popup_open")]
    public bool IsPopupOpen { get; init; }

    [JsonPropertyName("is_video_expanded")]
    public bool IsVideoExpanded { get; init; }

    [JsonPropertyName("current_error")]
    public string? CurrentError { get; init; }

    [JsonPropertyName("skip_back_seconds")]
    public int SkipBackSeconds { get; init; } = DefaultListening.SkipBackSeconds;

    [JsonPropertyName("skip_forward_seconds")]
    public int SkipForwardSeconds { get; init; } = DefaultListening.SkipForwardSeconds;

    [JsonPropertyName("sleep_timer_options_minutes")]
    public List<int> SleepTimerOptionsMinutes { get; init; } = [5, 10, 15, 30, 45, 60];

    [JsonPropertyName("sleep_timer_state")]
    public AudiobookSleepTimerStateDto SleepTimerState { get; init; } = new();

    [JsonPropertyName("sleep_timer_availability")]
    public AudiobookSleepTimerAvailabilityDto SleepTimerAvailability { get; init; } = new();

    [JsonPropertyName("playback_start_version")]
    public long PlaybackStartVersion { get; init; }

}

public sealed record ListenPlaybackClientSettings
{
    [JsonPropertyName("popup_width")]
    public int PopupWidth { get; init; } = 420;
    [JsonPropertyName("popup_height")]
    public int PopupHeight { get; init; } = 780;
    [JsonPropertyName("immediate_action_dedup_milliseconds")]
    public int ImmediateActionDedupMilliseconds { get; init; } = 900;
    [JsonPropertyName("immediate_action_consume_milliseconds")]
    public int ImmediateActionConsumeMilliseconds { get; init; } = 1800;
    [JsonPropertyName("audio_observer_interval_milliseconds")]
    public int AudioObserverIntervalMilliseconds { get; init; } = 1200;
    [JsonPropertyName("audio_observer_minimum_interval_milliseconds")]
    public int AudioObserverMinimumIntervalMilliseconds { get; init; } = 500;
    [JsonPropertyName("lyrics_instrumental_min_seconds")]
    public double LyricsInstrumentalMinSeconds { get; init; } = 2;
    [JsonPropertyName("lyrics_intro_min_seconds")]
    public double LyricsIntroMinSeconds { get; init; } = 3;
    [JsonPropertyName("lyrics_word_gap_min_seconds")]
    public double LyricsWordGapMinSeconds { get; init; } = 4;
    [JsonPropertyName("lyrics_lead_milliseconds")]
    public int LyricsLeadMilliseconds { get; init; } = 150;
    [JsonPropertyName("seek_tolerance_seconds")]
    public double SeekToleranceSeconds { get; init; } = 0.75d;
    [JsonPropertyName("volume_step")]
    public double VolumeStep { get; init; } = 0.05d;
    [JsonPropertyName("transport_ui_update_interval_milliseconds")]
    public int TransportUiUpdateIntervalMilliseconds { get; init; } = 1200;
    [JsonPropertyName("heartbeat_interval_seconds")]
    public int HeartbeatIntervalSeconds { get; init; } = 10;
    [JsonPropertyName("pending_transport_command_limit")]
    public int PendingTransportCommandLimit { get; init; } = 32;
    [JsonPropertyName("default_volume")]
    public double DefaultVolume { get; init; } = 0.8d;

    public ListenPlaybackClientSettings Normalize() => this with
    {
        LyricsLeadMilliseconds = Math.Clamp(LyricsLeadMilliseconds, 0, 500),
        LyricsInstrumentalMinSeconds = double.IsFinite(LyricsInstrumentalMinSeconds) ? Math.Clamp(LyricsInstrumentalMinSeconds, .25, 30) : 2,
        LyricsIntroMinSeconds = double.IsFinite(LyricsIntroMinSeconds) ? Math.Clamp(LyricsIntroMinSeconds, .25, 30) : 3,
        LyricsWordGapMinSeconds = double.IsFinite(LyricsWordGapMinSeconds) ? Math.Clamp(LyricsWordGapMinSeconds, .25, 30) : 4,
        PopupWidth = Math.Clamp(PopupWidth, 280, 1200),
        PopupHeight = Math.Clamp(PopupHeight, 360, 1400),
        ImmediateActionDedupMilliseconds = Math.Clamp(ImmediateActionDedupMilliseconds, 100, 5000),
        ImmediateActionConsumeMilliseconds = Math.Clamp(ImmediateActionConsumeMilliseconds, 100, 10000),
        AudioObserverIntervalMilliseconds = Math.Clamp(AudioObserverIntervalMilliseconds, 100, 10000),
        AudioObserverMinimumIntervalMilliseconds = Math.Clamp(AudioObserverMinimumIntervalMilliseconds, 100, 5000),
        SeekToleranceSeconds = Math.Clamp(SeekToleranceSeconds, 0.05d, 10d),
        VolumeStep = Math.Clamp(VolumeStep, 0.01d, 0.5d),
        TransportUiUpdateIntervalMilliseconds = Math.Clamp(TransportUiUpdateIntervalMilliseconds, 100, 10000),
        HeartbeatIntervalSeconds = Math.Clamp(HeartbeatIntervalSeconds, 1, 120),
        PendingTransportCommandLimit = Math.Clamp(PendingTransportCommandLimit, 1, 256),
        DefaultVolume = Math.Clamp(DefaultVolume, 0d, 1d),
    };
}

public static class ListenQueueItemFactory
{
    public static ListenQueueItem Create(WorkViewModel work) => new()
    {
        WorkId = work.Id,
        CollectionId = work.CollectionId,
        AlbumWorkId = MediaKindClassifier.IsMusic(work.MediaType) && work.RootWorkId != work.Id
            ? work.RootWorkId
            : null,
        AudiobookWorkId = MediaKindClassifier.IsAudiobook(work.MediaType)
            ? (work.RootWorkId is { } rootWorkId && rootWorkId != Guid.Empty ? rootWorkId : work.Id)
            : null,
        ArtistPersonId = MediaKindClassifier.IsMusic(work.MediaType)
            && !string.IsNullOrWhiteSpace(work.Artist) ? work.ArtistPersonId : null,
        AuthorName = work.Author,
        NarratorName = work.Narrator,
        Synopsis = work.Description,
        MediaType = work.MediaType,
        Title = GetDisplayTitle(work),
        Subtitle = MediaKindClassifier.IsMusic(work.MediaType)
            ? StringHelpers.FirstNonBlank(work.Artist)
            : StringHelpers.FirstNonBlank(work.Artist, work.Author, work.Album, work.Series, work.Year),
        Album = StringHelpers.FirstNonBlank(work.Album, work.Series),
        CoverUrl = work.CoverUrl,
        Duration = GetDuration(work),
        AssetId = work.AssetId,
        StreamUrl = work.AssetId.HasValue
            ? $"/media/assets/{work.AssetId.Value:D}/stream"
            : null,
    };

    private static string GetDisplayTitle(WorkViewModel work)
        => StringHelpers.FirstNonBlank(
               CleanUntitled(work.Title) ? null : work.Title,
               Canonical(work, "track_title"),
               Canonical(work, "track_name"),
               Canonical(work, "song_title"),
               Canonical(work, "name"),
               Canonical(work, "file_title"),
               Canonical(work, "file_name"))
           ?? work.Title;

    private static string? Canonical(WorkViewModel work, string key)
        => work.CanonicalValues.FirstOrDefault(value =>
            string.Equals(value.Key, key, StringComparison.OrdinalIgnoreCase))?.Value;

    private static bool CleanUntitled(string? value)
        => string.IsNullOrWhiteSpace(value)
           || value.Trim().StartsWith("Untitled", StringComparison.OrdinalIgnoreCase);

    private static string? GetDuration(WorkViewModel work)
    {
        var raw = work.CanonicalValues.FirstOrDefault(cv =>
            string.Equals(cv.Key, "duration_seconds", StringComparison.OrdinalIgnoreCase)
            || string.Equals(cv.Key, "duration_sec", StringComparison.OrdinalIgnoreCase)
            || string.Equals(cv.Key, "duration", StringComparison.OrdinalIgnoreCase)
            || string.Equals(cv.Key, "runtime", StringComparison.OrdinalIgnoreCase))?.Value;

        var seconds = PlaybackTimeParser.TryParseDurationSeconds(raw);
        if (seconds is not > 0)
        {
            return raw;
        }

        return PlaybackTimeParser.FormatDuration(seconds.Value);
    }
}

/// <summary>Canonical catalog and Listen destinations for identities carried by playback.</summary>
public static class ListenPlaybackIdentityRoutes
{
    public static string? Album(ListenQueueItem? item) => item is not null
        && item.PlaybackExperience == PlaybackExperience.Music
        && item.AlbumWorkId is { } id && id != Guid.Empty
        ? $"/details/musicalbum/{id:D}?context=listen"
        : null;

    public static string? Artist(ListenQueueItem? item) => item?.ArtistPersonId is { } id && id != Guid.Empty
        ? $"/details/person/{id:D}"
        : null;

    public static string? Audiobook(ListenQueueItem? item) => item is not null
        && MediaKindClassifier.IsAudiobook(item.MediaType)
        && (item.AudiobookWorkId ?? item.AlbumWorkId ?? item.WorkId) != Guid.Empty
            ? $"/details/audiobook/{(item.AudiobookWorkId ?? item.AlbumWorkId ?? item.WorkId):D}?context=listen"
            : null;

    public static string? Playlist(ListenQueueItem? item) => item?.PlaylistId is { } id && id != Guid.Empty
        ? $"/listen/music/playlists/{id:D}"
        : null;

    public static string? Contributor(PlaybackContributorIdentity? contributor) => contributor?.PersonId is { } id && id != Guid.Empty
        ? $"/details/person/{id:D}"
        : null;
}
