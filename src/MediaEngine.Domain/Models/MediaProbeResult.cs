namespace MediaEngine.Domain.Models;

/// <summary>
/// Structured metadata extracted from a media file via ffprobe.
/// </summary>
public sealed record MediaProbeResult
{
    // ── Duration & size ──────────────────────────────────────────────────────

    public TimeSpan Duration { get; init; }
    public long FileSizeBytes { get; init; }

    // ── Embedded metadata tags ───────────────────────────────────────────────

    public string? Title { get; init; }
    public string? Artist { get; init; }
    public string? Album { get; init; }
    public string? AlbumArtist { get; init; }
    public string? Genre { get; init; }
    public string? Date { get; init; }
    public string? Comment { get; init; }
    public string? Narrator { get; init; }
    public int? TrackNumber { get; init; }
    public string? Publisher { get; init; }
    public string? Description { get; init; }
    public DateTimeOffset? CapturedAt { get; init; }
    public string? DeviceMake { get; init; }
    public string? DeviceModel { get; init; }
    public double? Latitude { get; init; }
    public double? Longitude { get; init; }
    public string? ContentIdentifier { get; init; }
    public bool IsLivePhoto { get; init; }

    // ── Audio stream ─────────────────────────────────────────────────────────

    public IReadOnlyList<MediaProbeAudioStream> AudioStreams { get; init; } = [];
    public string? AudioCodec { get; init; }
    public string? AudioLanguage { get; init; }
    public int? AudioBitrate { get; init; }   // kbps
    public int? SampleRate { get; init; }   // Hz
    public int? Channels { get; init; }

    // ── Video stream (if present) ────────────────────────────────────────────

    public string? VideoCodec { get; init; }
    public int? Width { get; init; }
    public int? Height { get; init; }
    public double? FrameRate { get; init; }

    // ── Cover art & chapters ─────────────────────────────────────────────────

    public bool HasEmbeddedCover { get; init; }
    public int ChapterCount { get; init; }
    public IReadOnlyList<MediaProbeChapter> Chapters { get; init; } = [];
    public IReadOnlyList<string> SubtitleLanguages { get; init; } = [];
}

public sealed record MediaProbeChapter(
    int Index,
    string? Title,
    double StartSeconds,
    double? EndSeconds);

public sealed record MediaProbeAudioStream(int Index, string? Codec, string? Language, bool IsDefault);
