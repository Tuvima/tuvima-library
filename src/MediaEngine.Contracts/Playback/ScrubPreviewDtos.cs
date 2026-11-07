namespace MediaEngine.Contracts.Playback;

/// <summary>
/// The small index that goes with a video's scrub-preview sprite sheets (Unified player v2 D9a). The Engine writes it as
/// <c>index.json</c> and serves it from <see cref="PlaybackManifestDto.ScrubPreviewIndexUrl"/>; the player reads it once per playback.
/// Frame <c>n</c> is the picture at <c>n * IntervalSeconds</c>. It sits at column <c>n % Columns</c>, row <c>(n / Columns) % Rows</c> of
/// sheet <c>n / (Columns * Rows)</c>; each sheet name in <see cref="Sheets"/> is served next to the index.
/// </summary>
public sealed record ScrubPreviewIndexDto
{
    public int Version { get; init; } = 1;

    /// <summary>Seconds between two frames.</summary>
    public double IntervalSeconds { get; init; }

    /// <summary>Width of one frame in pixels.</summary>
    public int TileWidth { get; init; }

    /// <summary>Height of one frame in pixels.</summary>
    public int TileHeight { get; init; }

    /// <summary>Frames across one sheet.</summary>
    public int Columns { get; init; }

    /// <summary>Frames down one sheet.</summary>
    public int Rows { get; init; }

    /// <summary>How many frames the sheets hold in total (the last sheet can be only partly used).</summary>
    public int FrameCount { get; init; }

    /// <summary>Length of the video the frames were taken from, in seconds.</summary>
    public double DurationSeconds { get; init; }

    /// <summary>File names of the sprite sheets, in order.</summary>
    public IReadOnlyList<string> Sheets { get; init; } = [];
}

/// <summary>
/// The Settings > Playback > Advanced "Generate scrub previews" switch and what the previews cost on disk.
/// </summary>
public sealed record ScrubPreviewStatusDto
{
    public bool Enabled { get; init; }

    /// <summary>Bytes the preview cache uses right now.</summary>
    public long BytesUsed { get; init; }

    /// <summary>False when FFmpeg is not available, so previews cannot be made.</summary>
    public bool FfmpegAvailable { get; init; }
}

/// <summary>Turns scrub-preview generation on or off. Off also removes every preview that was made.</summary>
public sealed record UpdateScrubPreviewSettingRequestDto
{
    public bool Enabled { get; init; }
}
