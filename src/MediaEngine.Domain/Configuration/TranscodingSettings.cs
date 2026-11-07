using System.Text.Json.Serialization;

namespace MediaEngine.Domain.Configuration;

/// <summary>
/// FFmpeg and transcoding configuration loaded from <c>config/transcoding.json</c>.
/// All paths default to empty string, which triggers auto-detection.
/// Hardware acceleration defaults to "auto" — the service probes for NVENC,
/// QuickSync, and VAAPI and selects the best available option.
/// </summary>
public sealed class TranscodingSettings
{
    /// <summary>
    /// Explicit path to ffmpeg.exe / ffmpeg binary.
    /// Leave empty to enable auto-detection (tools/ffmpeg/ → PATH).
    /// </summary>
    [JsonPropertyName("ffmpeg_binary_path")]
    public string FfmpegBinaryPath { get; set; } = string.Empty;

    /// <summary>
    /// Explicit path to ffprobe.exe / ffprobe binary.
    /// Leave empty to enable auto-detection (tools/ffmpeg/ → PATH).
    /// </summary>
    [JsonPropertyName("ffprobe_binary_path")]
    public string FfprobeBinaryPath { get; set; } = string.Empty;

    /// <summary>
    /// Hardware acceleration mode: "auto" | "nvenc" | "quicksync" | "vaapi" | "none".
    /// "auto" probes available encoders and selects the best option with software fallback.
    /// </summary>
    [JsonPropertyName("hardware_acceleration")]
    public string HardwareAcceleration { get; set; } = "auto";

    /// <summary>Maximum number of concurrent transcoding jobs.</summary>
    [JsonPropertyName("max_concurrent_transcodes")]
    public int MaxConcurrentTranscodes { get; set; } = 1;

    /// <summary>Maximum disk space (GB) for shadow transcoded copies.</summary>
    [JsonPropertyName("shadow_storage_limit_gb")]
    public int ShadowStorageLimitGb { get; set; } = 500;

    /// <summary>Quality profiles available for transcoding.</summary>
    [JsonPropertyName("quality_profiles")]
    public List<TranscodingQualityProfile> QualityProfiles { get; set; } =
    [
        new() { Name = "mobile-small", Resolution = "540p", Codec = "h264", AudioCodec = "aac", Container = "mp4", Bitrate = "1M", SizeGuidance = "400-700 MB for a typical 2-hour movie" },
        new() { Name = "mobile-standard", Resolution = "720p", Codec = "h264", AudioCodec = "aac", Container = "mp4", Bitrate = "2M", SizeGuidance = "Offline-friendly quality" },
        new() { Name = "tv-adaptive-hls", Resolution = "adaptive", Codec = "h264", AudioCodec = "aac", Container = "hls", Bitrate = "adaptive", SizeGuidance = "Source-aware HLS ladder for compatible native players" },
        new() { Name = "audio-mobile", Resolution = "audio", Codec = "none", AudioCodec = "aac", Container = "m4a", Bitrate = "96k", SizeGuidance = "Lower bitrate audiobook/music option" },
    ];

    [JsonPropertyName("scheduled_encodes_enabled")]
    public bool ScheduledEncodesEnabled { get; set; } = true;

    [JsonPropertyName("maintenance_window")]
    public string MaintenanceWindow { get; set; } = "01:00-05:00";

    [JsonPropertyName("variant_cache_path")]
    public string VariantCachePath { get; set; } = ".data/variants";

    [JsonPropertyName("variant_retention_days")]
    public int VariantRetentionDays { get; set; } = 30;

    [JsonPropertyName("cleanup_lru_enabled")]
    public bool CleanupLruEnabled { get; set; } = true;

    [JsonPropertyName("default_mobile_profile")]
    public string DefaultMobileProfile { get; set; } = "mobile-small";

    /// <summary>Adaptive HTTP Live Streaming package and access policy.</summary>
    [JsonPropertyName("adaptive_hls")]
    public AdaptiveHlsSettings AdaptiveHls { get; set; } = new();

    /// <summary>Scrub previews: frames made ahead of time so the player can show a picture while the timeline is dragged.</summary>
    [JsonPropertyName("scrub_previews")]
    public ScrubPreviewSettings ScrubPreviews { get; set; } = new();
}

/// <summary>
/// The Engine's ahead-of-time scrub previews (Unified player v2 D9a): one frame every <see cref="IntervalSeconds"/>, tiled into
/// sprite sheets of <see cref="Columns"/> by <see cref="Rows"/>. <see cref="Enabled"/> is the Settings switch "Generate scrub previews".
/// </summary>
public sealed class ScrubPreviewSettings
{
    /// <summary>On by default. Turning it off stops the job and removes every preview from the Library's cache.</summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    /// <summary>Seconds between two frames.</summary>
    [JsonPropertyName("interval_seconds")]
    public int IntervalSeconds { get; set; } = 10;

    /// <summary>Width of one frame in pixels.</summary>
    [JsonPropertyName("tile_width")]
    public int TileWidth { get; set; } = 320;

    /// <summary>Frames across one sprite sheet.</summary>
    [JsonPropertyName("columns")]
    public int Columns { get; set; } = 10;

    /// <summary>Frames down one sprite sheet.</summary>
    [JsonPropertyName("rows")]
    public int Rows { get; set; } = 10;

    /// <summary>How many files with no previews the gradual pass over the existing library queues each time it runs.</summary>
    [JsonPropertyName("backfill_per_pass")]
    public int BackfillPerPass { get; set; } = 5;

    /// <summary>Minutes between two passes that look for videos with no previews.</summary>
    [JsonPropertyName("backfill_interval_minutes")]
    public int BackfillIntervalMinutes { get; set; } = 2;
}

public sealed class AdaptiveHlsSettings
{
    [JsonPropertyName("profile_name")]
    public string ProfileName { get; set; } = "tv-adaptive-hls";

    [JsonPropertyName("segment_seconds")]
    public int SegmentSeconds { get; set; } = 6;

    [JsonPropertyName("access_lifetime_minutes")]
    public int AccessLifetimeMinutes { get; set; } = 240;

    [JsonPropertyName("preparation_wait_seconds")]
    public int PreparationWaitSeconds { get; set; } = 20;

    [JsonPropertyName("cleanup_interval_minutes")]
    public int CleanupIntervalMinutes { get; set; } = 15;

    [JsonPropertyName("renditions")]
    public List<HlsRenditionProfile> Renditions { get; set; } =
    [
        new() { Name = "1080p", Height = 1080, VideoBitrateKbps = 6000, MaxRateKbps = 6600, BufferSizeKbps = 12000 },
        new() { Name = "720p", Height = 720, VideoBitrateKbps = 3000, MaxRateKbps = 3300, BufferSizeKbps = 6000 },
        new() { Name = "480p", Height = 480, VideoBitrateKbps = 1400, MaxRateKbps = 1540, BufferSizeKbps = 2800 },
    ];
}

public sealed class HlsRenditionProfile
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("height")]
    public int Height { get; set; }

    [JsonPropertyName("video_bitrate_kbps")]
    public int VideoBitrateKbps { get; set; }

    [JsonPropertyName("max_rate_kbps")]
    public int MaxRateKbps { get; set; }

    [JsonPropertyName("buffer_size_kbps")]
    public int BufferSizeKbps { get; set; }
}

/// <summary>A named transcoding quality profile.</summary>
public sealed class TranscodingQualityProfile
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("resolution")]
    public string Resolution { get; set; } = string.Empty;

    [JsonPropertyName("codec")]
    public string Codec { get; set; } = "h264";

    [JsonPropertyName("audio_codec")]
    public string AudioCodec { get; set; } = "aac";

    [JsonPropertyName("container")]
    public string Container { get; set; } = "mp4";

    [JsonPropertyName("bitrate")]
    public string Bitrate { get; set; } = string.Empty;

    [JsonPropertyName("size_guidance")]
    public string SizeGuidance { get; set; } = string.Empty;
}
