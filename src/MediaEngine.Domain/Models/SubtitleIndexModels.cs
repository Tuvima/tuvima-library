namespace MediaEngine.Domain.Models;

/// <summary>One time-stamped line of subtitle text, in milliseconds from the start of the file.</summary>
public sealed record SubtitleLine(int Index, long StartMs, long EndMs, string Text);

/// <summary>Where an indexed subtitle track came from.</summary>
public enum SubtitleSourceKind
{
    /// <summary>A stored, normalised text track (whichever source supplied it).</summary>
    TextTrack = 0,

    /// <summary>A text subtitle stream inside the video file.</summary>
    Embedded = 1,
}

/// <summary>A subtitle track that is waiting to be read, or whose stored source changed.</summary>
public sealed record SubtitleTrackSource(
    Guid TrackId,
    Guid AssetId,
    SubtitleSourceKind Kind,
    Guid? TextTrackId,
    int? StreamIndex,
    string Language,
    string SourceStamp,
    string? LocalPath);

/// <summary>An indexed track whose name tags are older than the current Universe graph, or that was never tagged.</summary>
public sealed record SubtitleTrackToTag(Guid TrackId, Guid AssetId, string? EntitySignature);

/// <summary>A Universe entity whose name (and plain-matching aliases) may be named in subtitle text.</summary>
public sealed record SubtitleEntityName(
    Guid EntityId,
    string Qid,
    string Label,
    string EntitySubType,
    IReadOnlyList<string> Aliases);

/// <summary>A time window in which one Universe entity is named in a track's subtitle text.</summary>
public sealed record SubtitleNameTag(
    Guid EntityId,
    long WindowStartMs,
    long WindowEndMs,
    int FirstLineIndex,
    int LastLineIndex,
    int MentionCount,
    string MatchedText);

/// <summary>A chapter of a video file, in milliseconds from the start.</summary>
public sealed record MediaAssetChapter(int Index, long StartMs, long? EndMs, string? Title);

/// <summary>A video file waiting for its one-off probe (chapter times and embedded subtitle streams).</summary>
public sealed record SubtitleAssetToProbe(Guid AssetId, string FilePath);

/// <summary>A subtitle stream embedded in a video file, as the probe reports it.</summary>
public sealed record EmbeddedSubtitleStream(int StreamIndex, string? Codec, string Language, bool IsForced, bool IsHearingImpaired);

/// <summary>Stable ids for indexed subtitle tracks, so a track keeps its id when it is read again.</summary>
public static class SubtitleTrackIds
{
    public static Guid ForTextTrack(Guid textTrackId) =>
        Services.Hashing.DeterministicGuid("subtitle-index|text-track|" + textTrackId.ToString("N"));

    public static Guid ForEmbedded(Guid assetId, int streamIndex) =>
        Services.Hashing.DeterministicGuid("subtitle-index|embedded|" + assetId.ToString("N") + "|" + streamIndex.ToString(System.Globalization.CultureInfo.InvariantCulture));
}
