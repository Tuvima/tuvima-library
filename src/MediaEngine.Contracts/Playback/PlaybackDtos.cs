using System.Text.Json.Serialization;

namespace MediaEngine.Contracts.Playback;

public sealed record PlaybackManifestDto
{
    public Guid AssetId { get; init; }
    public string Client { get; init; } = "web";
    public string MediaType { get; init; } = "Unknown";
    public string SourceExtension { get; init; } = string.Empty;
    public string RecommendedDelivery { get; init; } = PlaybackDeliveryModes.DirectStream;
    public bool DirectPlaySupported { get; init; }
    public string? DirectStreamUrl { get; init; }
    public string? HlsUrl { get; init; }
    public string? HlsStatus { get; init; }
    public double? DurationSeconds { get; init; }
    public DateTimeOffset? HlsExpiresAt { get; init; }
    public PlaybackProfileDto Profile { get; init; } = new();
    public IReadOnlyList<PlaybackTrackDto> AudioTracks { get; init; } = [];
    public IReadOnlyList<PlaybackSubtitleTrackDto> SubtitleTracks { get; init; } = [];
    public IReadOnlyList<PlaybackChapterDto> Chapters { get; init; } = [];
    public IReadOnlyList<OfflineVariantDto> OfflineVariants { get; init; } = [];
    public PlaybackResumeDto? Resume { get; init; }
    public IReadOnlyList<PlaybackSegmentDto> Segments { get; init; } = [];
    public PlaybackTechnicalInfoDto Technical { get; init; } = new();
    public IReadOnlyList<string> Warnings { get; init; } = [];
    public string? ConversionReason { get; init; }
    public int? SourceBitrateKbps { get; init; }
    public PlaybackConnectionContextDto Connection { get; init; } = new();
}

public sealed record PlaybackTechnicalInfoDto
{
    public string? Container { get; init; }
    public string? VideoCodec { get; init; }
    public int? Width { get; init; }
    public int? Height { get; init; }
    public double? FrameRate { get; init; }
    public string? AudioCodec { get; init; }
    public int? AudioBitrateKbps { get; init; }
    public int? SampleRateHz { get; init; }
    public int? BitDepth { get; init; }
    public int? Channels { get; init; }

    public string? QualityLabel => Height switch
    {
        >= 2160 => "4K",
        >= 1440 => "1440p",
        >= 1080 => "1080p",
        >= 720 => "720p",
        >= 480 => "480p",
        > 0 => $"{Height}p",
        _ => null,
    };
}

public sealed record ReaderManifestDto
{
    public Guid AssetId { get; init; }
    public string Client { get; init; } = "web";
    public string MediaType { get; init; } = "Books";
    public string ReaderKind { get; init; } = "epub";
    public string? ResourceBaseUrl { get; init; }
    public int? PageCount { get; init; }
    public IReadOnlyList<PlaybackChapterDto> Chapters { get; init; } = [];
    public PlaybackResumeDto? Resume { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

public sealed record PlaybackProfileDto
{
    public string Key { get; init; } = "web";
    public string DisplayName { get; init; } = "Web";
    public string PreferredDelivery { get; init; } = PlaybackDeliveryModes.DirectStream;
    public IReadOnlyList<string> SupportedContainers { get; init; } = [];
    public IReadOnlyList<string> SupportedVideoCodecs { get; init; } = [];
    public IReadOnlyList<string> SupportedAudioCodecs { get; init; } = [];
    public IReadOnlyList<string> SupportedSubtitleFormats { get; init; } = [];
    public int? MaxHeight { get; init; }
    public int? MaxBitrateKbps { get; init; }
    public bool SupportsPlaybackSpeed { get; init; }
    public bool SupportsAlternateAudio { get; init; }
    public bool SupportsSubtitles { get; init; }
    public bool SupportsOfflineDownloads { get; init; }
}

public sealed record PlaybackTrackDto
{
    public int Index { get; init; }
    public string Kind { get; init; } = "audio";
    public string? Language { get; init; }
    public string? Codec { get; init; }
    public string? DisplayName { get; init; }
    public bool IsDefault { get; init; }
    public int? Channels { get; init; }
    public int? BitrateKbps { get; init; }
}

public sealed record PlaybackSubtitleTrackDto
{
    public int Index { get; init; }
    public string? Language { get; init; }
    public string? Codec { get; init; }
    public string? DisplayName { get; init; }
    public bool IsDefault { get; init; }
    public bool IsForced { get; init; }
    public string? DeliveryUrl { get; init; }
}

public sealed record PlaybackChapterDto
{
    public Guid? AssetId { get; init; }
    public int Index { get; init; }
    public string Title { get; init; } = string.Empty;
    public string? OriginalTitle { get; init; }
    public string Kind { get; init; } = PlaybackChapterKinds.Chapter;
    public string TitleSource { get; init; } = PlaybackChapterTitleSources.Generated;
    public double StartSeconds { get; init; }
    public double? EndSeconds { get; init; }
}

public static class PlaybackChapterKinds
{
    public const string Chapter = "Chapter";
}

public static class PlaybackChapterTitleSources
{
    public const string Embedded = "Embedded";
    public const string Generated = "Generated";
    public const string Override = "Override";
}

public static class AudiobookStartKinds
{
    public const string Resume = "Resume";
    public const string Chapter = "Chapter";
    public const string History = "History";
    public const string Bookmark = "Bookmark";
}

public sealed record PlaybackResumeDto
{
    public double ProgressPct { get; init; }
    public double? PositionSeconds { get; init; }
    public DateTimeOffset? LastAccessed { get; init; }
}

public sealed record PlayerStateDto
{
    public Guid ProfileId { get; init; }
    public Guid SessionId { get; init; }
    public string DeviceId { get; init; } = "web";
    public string Client { get; init; } = "web";
    public string PlaybackState { get; init; } = PlayerPlaybackStates.Stopped;
    public string Experience { get; init; } = PlayerExperienceModes.Music;
    public long StateVersion { get; init; }
    public Guid? CurrentQueueItemId { get; init; }
    public PlayerQueueItemDto? CurrentItem { get; init; }
    public IReadOnlyList<PlayerQueueItemDto> Queue { get; init; } = [];
    public double PositionSeconds { get; init; }
    public double? DurationSeconds { get; init; }
    public double ProgressPct { get; init; }
    public double Volume { get; init; } = 0.8d;
    public bool IsMuted { get; init; }
    public double PlaybackRate { get; init; } = 1d;
    public bool ShuffleEnabled { get; init; }
    public string RepeatMode { get; init; } = PlayerRepeatModes.Off;
    public string? SourceLabel { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public DateTimeOffset? LastHeartbeatAt { get; init; }
    public bool IsStale { get; init; }
    public PlayerCapabilitiesDto Capabilities { get; init; } = new();
    public IReadOnlyList<AudiobookListenHistoryItemDto> AudiobookHistory { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];
    public PlaybackConnectionContextDto Connection { get; init; } = new();
}

public sealed record AudiobookListenHistoryItemDto
{
    public Guid Id { get; init; }
    public Guid ProfileId { get; init; }
    public Guid WorkId { get; init; }
    public Guid AssetId { get; init; }
    public string Title { get; init; } = string.Empty;
    public string? ChapterTitle { get; init; }
    public int? ChapterIndex { get; init; }
    public double PositionSeconds { get; init; }
    public double? DurationSeconds { get; init; }
    public double ProgressPct { get; init; }
    public string DeviceId { get; init; } = "web";
    public string Client { get; init; } = "web";
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset EndedAt { get; init; }
}

public sealed record AudiobookBookmarkDto
{
    public Guid Id { get; init; }
    public Guid ProfileId { get; init; }
    public Guid WorkId { get; init; }
    public Guid AssetId { get; init; }
    public int? ChapterIndex { get; init; }
    public string? ChapterTitle { get; init; }
    public double PositionSeconds { get; init; }
    public double? DurationSeconds { get; init; }
    public string? Label { get; init; }
    public string? Note { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}

/// <summary>Native owner proof returned when capturing a live audiobook position.</summary>
public sealed record AudiobookBookmarkNativeCaptureDto
{
    public Guid AssetId { get; init; }
    public long PlaybackRequestVersion { get; init; }
    public bool SourceVerified { get; init; }
    public double PositionSeconds { get; init; }
    public double? DurationSeconds { get; init; }
}

public sealed record CreateAudiobookBookmarkRequestDto
{
    public Guid? ProfileId { get; init; }
    public Guid AssetId { get; init; }
    public int? ChapterIndex { get; init; }
    public string? ChapterTitle { get; init; }
    public double PositionSeconds { get; init; }
    public double? DurationSeconds { get; init; }
    public string? Label { get; init; }
    public string? Note { get; init; }
}

public sealed record AudiobookChapterTitleOverrideDto
{
    public Guid WorkId { get; init; }
    public Guid AssetId { get; init; }
    public int ChapterIndex { get; init; }
    public string Title { get; init; } = string.Empty;
    public string TitleSource { get; init; } = PlaybackChapterTitleSources.Override;
    public DateTimeOffset UpdatedAt { get; init; }
}

public sealed record UpsertAudiobookChapterTitleOverrideRequestDto
{
    public Guid? ProfileId { get; init; }
    public Guid AssetId { get; init; }
    public int ChapterIndex { get; init; }
    public string Title { get; init; } = string.Empty;
    public string? TitleSource { get; init; }
}

public sealed record PlayerQueueItemDto
{
    public Guid QueueItemId { get; init; }
    public Guid WorkId { get; init; }
    public Guid? AssetId { get; init; }
    public Guid? CollectionId { get; init; }
    public string MediaType { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string? Subtitle { get; init; }
    public string? Album { get; init; }
    public string? Author { get; init; }
    public string? Artist { get; init; }
    public string? Narrator { get; init; }
    public string? Series { get; init; }
    public string? Year { get; init; }
    public string? ContentRating { get; init; }
    public string? SeasonNumber { get; init; }
    public string? EpisodeNumber { get; init; }
    public string? EpisodeTitle { get; init; }
    public string? Quality { get; init; }
    public string? CoverUrl { get; init; }
    public double? DurationSeconds { get; init; }
    public double? PositionSeconds { get; init; }
    public double? ProgressPct { get; init; }
    public string? StreamUrl { get; init; }
    public string? DownloadUrl { get; init; }
    public IReadOnlyList<PlaybackChapterDto> Chapters { get; init; } = [];
    public PlaybackManifestDto? Manifest { get; init; }
    public DateTimeOffset AddedAt { get; init; }
}

public sealed record PlayerCommandRequestDto
{
    public Guid? ProfileId { get; init; }
    public string? DeviceId { get; init; }
    public string? Client { get; init; }
    public long? ExpectedStateVersion { get; init; }
    public bool Force { get; init; }
    public string Command { get; init; } = PlayerCommands.Pause;
    public Guid? QueueItemId { get; init; }
    public double? PositionSeconds { get; init; }
    public double? DeltaSeconds { get; init; }
    public double? DurationSeconds { get; init; }
    public double? Volume { get; init; }
    public bool? IsMuted { get; init; }
    public double? PlaybackRate { get; init; }
    public bool? ShuffleEnabled { get; init; }
    public string? RepeatMode { get; init; }
}

public sealed record PlayerQueueMutationDto
{
    public Guid? ProfileId { get; init; }
    public string? DeviceId { get; init; }
    public string? Client { get; init; }
    public long? ExpectedStateVersion { get; init; }
    public bool Force { get; init; }
    public string Mode { get; init; } = PlayerQueueMutationModes.Replace;
    public IReadOnlyList<PlayerQueueMutationItemDto> Items { get; init; } = [];
    public IReadOnlyList<Guid> WorkIds { get; init; } = [];
    public IReadOnlyList<Guid> QueueItemIds { get; init; } = [];
    public int? StartIndex { get; init; }
    public Guid? StartWorkId { get; init; }
    public Guid? StartQueueItemId { get; init; }
    public string? SourceLabel { get; init; }
    public bool Shuffle { get; init; }
    public bool ClearExisting { get; init; }
}

public sealed record PlayerQueueMutationItemDto
{
    public Guid WorkId { get; init; }
    public Guid? AssetId { get; init; }
    public Guid? CollectionId { get; init; }
    public string? MediaType { get; init; }
    public string? Title { get; init; }
    public string? Subtitle { get; init; }
    public string? Album { get; init; }
    public string? Author { get; init; }
    public string? Artist { get; init; }
    public string? Narrator { get; init; }
    public string? Series { get; init; }
    public string? Year { get; init; }
    public string? ContentRating { get; init; }
    public string? SeasonNumber { get; init; }
    public string? EpisodeNumber { get; init; }
    public string? EpisodeTitle { get; init; }
    public string? Quality { get; init; }
    public string? CoverUrl { get; init; }
    public double? DurationSeconds { get; init; }
    public double? PositionSeconds { get; init; }
    public string? StreamUrl { get; init; }
    public string? DownloadUrl { get; init; }
}

public sealed record PlayerHeartbeatDto
{
    public Guid? ProfileId { get; init; }
    public Guid? SessionId { get; init; }
    /// <summary>
    /// Optional monotonic sequence for idempotent telemetry updates within the
    /// server-issued player session. It does not participate in authorization.
    /// </summary>
    public long? Sequence { get; init; }
    public string? DeviceId { get; init; }
    public string? Client { get; init; }
    public Guid? QueueItemId { get; init; }
    public Guid? AssetId { get; init; }
    public bool IsPlaying { get; init; }
    public bool HasPlaybackEnded { get; init; }
    public double PositionSeconds { get; init; }
    public double? DurationSeconds { get; init; }
    public double? ProgressPct { get; init; }
    public double? Volume { get; init; }
    public bool? IsMuted { get; init; }
    public double? PlaybackRate { get; init; }
    public long? StateVersion { get; init; }
    public int? ChapterIndex { get; init; }
    public string? ChapterTitle { get; init; }
    public string? AudiobookStartKind { get; init; }
    public PlaybackConnectionContextDto? Connection { get; init; }
}

/// <summary>
/// Observed playback connection facts. These values guide delivery selection and
/// diagnostics; they are never trusted as an authorization signal.
/// </summary>
public sealed record PlaybackConnectionContextDto
{
    public string ConnectionPath { get; init; } = PlaybackConnectionPaths.Local;
    public string? RemoteConnectivityProvider { get; init; }
    public double? EstimatedBandwidthMbps { get; init; }
    public int? LatencyMs { get; init; }
    public Guid? RoomId { get; init; }
}

public static class PlaybackConnectionPaths
{
    public const string Local = "local";
    public const string RemoteDirect = "remote-direct";
    public const string RemoteSecureProvider = "remote-secure-provider";
    public const string RemoteCustom = "remote-custom";
    public const string RemoteRelay = "remote-relay";

    public static bool IsKnown(string? value) => value is Local or RemoteDirect
        or RemoteSecureProvider or RemoteCustom or RemoteRelay;
}

public sealed record PlayerCapabilitiesDto
{
    public bool CanPlay { get; init; } = true;
    public bool CanPause { get; init; } = true;
    public bool CanStop { get; init; } = true;
    public bool CanSeek { get; init; } = true;
    public bool CanSkipNext { get; init; } = true;
    public bool CanSkipPrevious { get; init; } = true;
    public bool CanSetVolume { get; init; } = true;
    public bool CanMute { get; init; } = true;
    public bool CanSetSpeed { get; init; } = true;
    public bool CanShuffle { get; init; } = true;
    public bool CanRepeat { get; init; } = true;
    public bool CanUseChapters { get; init; } = true;
    public bool CanUseCaptions { get; init; } = true;
    public bool CanSelectAudioTrack { get; init; } = true;
    public bool CanSelectQuality { get; init; } = true;
    public bool CanUseFullscreen { get; init; } = true;
    public bool CanUsePictureInPicture { get; init; } = true;
    public bool CanReorderQueue { get; init; } = true;
    public bool CanTakeover { get; init; } = true;
    public IReadOnlyList<string> SupportedMediaTypes { get; init; } = ["Music", "Audiobooks", "Movie", "TV"];
    public IReadOnlyList<double> SupportedPlaybackRates { get; init; } = [0.5d, 0.75d, 1d, 1.25d, 1.5d, 1.75d, 2d, 2.5d, 3d];
    public IReadOnlyList<double> SupportedScanRates { get; init; } = [2d, 4d, 8d, 16d];
}

public sealed record PlayerSessionTakeoverRequestDto
{
    public Guid? ProfileId { get; init; }
    public string? DeviceId { get; init; }
    public string? Client { get; init; }
    public bool Force { get; init; }
}

public sealed record EncodeJobDto
{
    public Guid Id { get; init; }
    public Guid AssetId { get; init; }
    public string ProfileKey { get; init; } = string.Empty;
    public string Status { get; init; } = EncodeJobStatuses.Queued;
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? ScheduledFor { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public double ProgressPct { get; init; }
    public string? OutputPath { get; init; }
    public long? OutputBytes { get; init; }
    public string? LastError { get; init; }
    public int RetryCount { get; init; }
}

public sealed record OfflineVariantDto
{
    public Guid Id { get; init; }
    public Guid AssetId { get; init; }
    public string ProfileKey { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string Status { get; init; } = OfflineVariantStatuses.Pending;
    public string? DownloadUrl { get; init; }
    public long? FileSizeBytes { get; init; }
    public string? Container { get; init; }
    public string? VideoCodec { get; init; }
    public string? AudioCodec { get; init; }
    public int? Width { get; init; }
    public int? Height { get; init; }
    public int? BitrateKbps { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
    public string? SourceHash { get; init; }
}

public sealed record QueueEncodeRequestDto
{
    public string ProfileKey { get; init; } = "mobile-standard";
    public DateTimeOffset? ScheduledFor { get; init; }
}

public sealed record PlaybackDiagnosticsDto
{
    public bool FFmpegAvailable { get; init; }
    public string? FFmpegVersion { get; init; }
    public bool HlsMuxerAvailable { get; init; }
    public bool H264EncoderAvailable { get; init; }
    public bool AacEncoderAvailable { get; init; }
    public bool WebVttEncoderAvailable { get; init; }
    public string? PreferredHardwareEncoder { get; init; }
    public bool MediaInfoAvailable { get; init; }
    public string? MediaInfoVersion { get; init; }
    public IReadOnlyList<EncodeJobDto> ActiveJobs { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class UserPlaybackSettingsDto
{
    public Guid ProfileId { get; set; }
    public PlaybackGeneralSettingsDto General { get; set; } = new();
    public WatchingSettingsDto Watching { get; set; } = new();
    public ListeningSettingsDto Listening { get; set; } = new();
    public ReadingSettingsDto Reading { get; set; } = new();
    public SubtitleLanguageSettingsDto Subtitles { get; set; } = new();
    public Dictionary<string, ContextSidebarLayoutDto> ContextSidebars { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public DateTimeOffset UpdatedAt { get; set; }

    public static UserPlaybackSettingsDto CreateDefaults(Guid profileId) => new()
    {
        ProfileId = profileId,
        UpdatedAt = DateTimeOffset.UtcNow,
    };
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class ContextSidebarLayoutDto
{
    public bool Open { get; set; }
    public int Width { get; set; } = 340;
    public string? ActivePanelKey { get; set; }
}

public sealed class PlaybackGeneralSettingsDto
{
    public bool ResumePlayback { get; set; } = true;
    public bool AskBeforeResuming { get; set; }
    public int MinimumProgressToTrackPercent { get; set; } = 5;
    public int MarkCompleteThresholdPercent { get; set; } = 90;
    public bool TrackPartiallyPlayedItems { get; set; } = true;
    public bool SyncProgressBetweenBookAndAudiobook { get; set; } = true;
    public bool SpoilerSafeProgress { get; set; }
    public bool SyncAcrossDevices { get; set; } = true;
    public int SyncFrequencyMinutes { get; set; } = 15;
    public bool UseBannerArtWhenAvailable { get; set; } = true;
}

public sealed class WatchingSettingsDto
{
    public decimal DefaultPlaybackSpeed { get; set; } = 1.0m;
    public int SkipBackSeconds { get; set; } = 10;
    public int SkipForwardSeconds { get; set; } = 30;
    public bool AutoplayNextEpisode { get; set; } = true;
    public bool ContinueFromCredits { get; set; }
    public bool PreferHeroBanners { get; set; } = true;
    public bool RememberPerTitlePlaybackSpeed { get; set; } = true;
    public string PreferredVideoQuality { get; set; } = PlaybackPreferenceValues.Auto;
}

public sealed class ListeningSettingsDto
{
    public decimal AudiobookDefaultSpeed { get; set; } = 1.25m;
    public int SkipBackSeconds { get; set; } = 15;
    public int SkipForwardSeconds { get; set; } = 15;
    public int ResumeRewindSeconds { get; set; } = 10;
    public int AudiobookListenQualificationSeconds { get; set; } = 60;
    public int AudiobookHistoryLimit { get; set; } = 25;
    public int AudiobookResumeRegressionGuardSeconds { get; set; } = 90;
    public int AudiobookNearStartGuardSeconds { get; set; } = 30;
    public int AudiobookHistoryActiveSegmentGapSeconds { get; set; } = 20;
    public int AudiobookHistoryPositionJumpToleranceSeconds { get; set; } = 12;
    public List<double> AudiobookScanRates { get; set; } = [2d, 4d, 8d, 16d];
    public List<int> SleepTimerOptionsMinutes { get; set; } = [5, 10, 15, 30, 45, 60];
    public bool AllowEndOfChapterSleepTimer { get; set; } = true;
    public bool MusicCrossfade { get; set; }
    public int CrossfadeSeconds { get; set; } = 5;
    public string DefaultSleepTimer { get; set; } = "30";
    public bool SkipSilence { get; set; } = true;
    public bool RememberPerTitlePlaybackSpeed { get; set; } = true;
    public string OutputPreference { get; set; } = PlaybackPreferenceValues.Auto;
}

public sealed class ReadingSettingsDto
{
    public string ReadingMode { get; set; } = PlaybackPreferenceValues.Paginated;
    public int FontSizePercent { get; set; } = 100;
    public string LineSpacing { get; set; } = PlaybackPreferenceValues.Comfortable;
    public string Margins { get; set; } = PlaybackPreferenceValues.Medium;
    public bool KeepScreenAwake { get; set; } = true;
    public bool ShowChapterProgress { get; set; } = true;
    public bool ShowPageNumbers { get; set; } = true;
    public string DefaultComicMode { get; set; } = PlaybackPreferenceValues.Page;
}

public sealed class SubtitleLanguageSettingsDto
{
    public string DefaultSubtitleLanguage { get; set; } = "English";
    public string ForcedSubtitlesMode { get; set; } = PlaybackPreferenceValues.Auto;
    public string AudioLanguage { get; set; } = "English";
    public string SubtitleSize { get; set; } = PlaybackPreferenceValues.Medium;
    public bool SubtitleBackground { get; set; } = true;
    public string SubtitlePosition { get; set; } = PlaybackPreferenceValues.Bottom;
    public string SubtitleStyle { get; set; } = PlaybackPreferenceValues.Clean;
}

public static class PlaybackPreferenceValues
{
    public const string Auto = "Auto";
    public const string Original = "Original";
    public const string High = "High";
    public const string Balanced = "Balanced";
    public const string DataSaver = "DataSaver";
    public const string Headphones = "Headphones";
    public const string Speakers = "Speakers";
    public const string Bluetooth = "Bluetooth";
    public const string Off = "Off";
    public const string EndOfChapter = "EndOfChapter";
    public const string EndOfEpisode = "EndOfEpisode";
    public const string Paginated = "Paginated";
    public const string Scroll = "Scroll";
    public const string Compact = "Compact";
    public const string Comfortable = "Comfortable";
    public const string Spacious = "Spacious";
    public const string Narrow = "Narrow";
    public const string Medium = "Medium";
    public const string Wide = "Wide";
    public const string Page = "Page";
    public const string Webtoon = "Webtoon";
    public const string DoublePage = "DoublePage";
    public const string FitWidth = "FitWidth";
    public const string Always = "Always";
    public const string Never = "Never";
    public const string Small = "Small";
    public const string Large = "Large";
    public const string ExtraLarge = "ExtraLarge";
    public const string Bottom = "Bottom";
    public const string Top = "Top";
    public const string Clean = "Clean";
    public const string HighContrast = "HighContrast";
    public const string Shadowed = "Shadowed";
}

public static class PlaybackDeliveryModes
{
    public const string DirectStream = "direct-stream";
    public const string Hls = "hls";
    public const string OfflineVariant = "offline-variant";
    public const string Reader = "reader";
}

public static class PlayerPlaybackStates
{
    public const string Stopped = "stopped";
    public const string Playing = "playing";
    public const string Paused = "paused";
}

public static class PlayerExperienceModes
{
    public const string Music = "music";
    public const string Audiobook = "audiobook";
    public const string Video = "video";
}

public static class PlayerRepeatModes
{
    public const string Off = "off";
    public const string One = "one";
    public const string All = "all";
}

public static class PlayerCommands
{
    public const string Play = "play";
    public const string Pause = "pause";
    public const string Stop = "stop";
    public const string Next = "next";
    public const string Previous = "previous";
    public const string Seek = "seek";
    public const string RelativeSeek = "relative-seek";
    public const string Volume = "volume";
    public const string Mute = "mute";
    public const string Speed = "speed";
    public const string Shuffle = "shuffle";
    public const string Repeat = "repeat";
    public const string ScanStart = "scan-start";
    public const string ScanStop = "scan-stop";
    public const string SleepTimer = "sleep-timer";
}

public static class PlayerQueueMutationModes
{
    public const string Replace = "replace";
    public const string AddNext = "add-next";
    public const string AddEnd = "add-end";
    public const string Reorder = "reorder";
    public const string Remove = "remove";
    public const string Clear = "clear";
}

public static class EncodeJobStatuses
{
    public const string Queued = "queued";
    public const string Scheduled = "scheduled";
    public const string Running = "running";
    public const string Complete = "complete";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
}

public static class OfflineVariantStatuses
{
    public const string Pending = "pending";
    public const string Ready = "ready";
    public const string Expired = "expired";
    public const string Failed = "failed";
}
