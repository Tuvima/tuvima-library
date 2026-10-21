using MediaEngine.Domain.Models;

namespace MediaEngine.Domain.Contracts;

/// <summary>
/// Storage for the subtitle text index (Universes U-WP14): time-stamped subtitle lines, the Universe entities named in them,
/// and the chapter times of video files. Reads the Library's stored text tracks; never one specific subtitle source.
/// </summary>
public interface ISubtitleIndexRepository
{
    /// <summary>Stored subtitle tracks never read, or whose stored file changed, or whose last read failed and may be retried.</summary>
    Task<IReadOnlyList<SubtitleTrackSource>> GetStoredTracksNeedingIndexAsync(int limit, DateTimeOffset now, CancellationToken ct = default);

    /// <summary>Indexed tracks whose tags were never made or are older than <paramref name="taggedBefore"/>, oldest first.</summary>
    Task<IReadOnlyList<SubtitleTrackToTag>> GetTracksNeedingTagsAsync(int limit, DateTimeOffset taggedBefore, CancellationToken ct = default);

    /// <summary>Video files (movies and TV) that have not been probed yet, or whose last probe failed and may be retried.</summary>
    Task<IReadOnlyList<SubtitleAssetToProbe>> GetAssetsAwaitingProbeAsync(int limit, DateTimeOffset now, CancellationToken ct = default);

    /// <summary>The languages that already have a stored (non-embedded) subtitle track for the file.</summary>
    Task<IReadOnlySet<string>> GetStoredTrackLanguagesAsync(Guid assetId, CancellationToken ct = default);

    /// <summary>The Universe entities (labels and aliases) that can be named in the file's subtitles.</summary>
    Task<IReadOnlyList<SubtitleEntityName>> GetEntityNamesForAssetAsync(Guid assetId, CancellationToken ct = default);

    /// <summary>Replaces the lines and tags of one track and marks it indexed.</summary>
    Task SaveIndexedTrackAsync(
        SubtitleTrackSource source,
        IReadOnlyList<SubtitleLine> lines,
        IReadOnlyList<SubtitleNameTag> tags,
        string entitySignature,
        DateTimeOffset now,
        CancellationToken ct = default);

    /// <summary>Replaces only the tags of an already indexed track.</summary>
    Task SaveTagsAsync(
        Guid trackId,
        IReadOnlyList<SubtitleNameTag> tags,
        string entitySignature,
        DateTimeOffset now,
        CancellationToken ct = default);

    /// <summary>Notes that a track's tags were checked against the Universe graph and are still current.</summary>
    Task MarkTagsCheckedAsync(Guid trackId, DateTimeOffset now, CancellationToken ct = default);

    /// <summary>Records a failed read, with the earliest time it may be tried again.</summary>
    Task MarkTrackFailedAsync(SubtitleTrackSource source, string error, DateTimeOffset now, DateTimeOffset nextRetryAt, CancellationToken ct = default);

    /// <summary>Stores a file's chapter times and marks the file probed.</summary>
    Task SaveProbedAssetAsync(
        Guid assetId,
        IReadOnlyList<MediaAssetChapter> chapters,
        int embeddedStreamCount,
        DateTimeOffset now,
        CancellationToken ct = default);

    /// <summary>Records a failed probe, with the earliest time it may be tried again.</summary>
    Task MarkAssetProbeFailedAsync(Guid assetId, string error, DateTimeOffset now, DateTimeOffset nextRetryAt, CancellationToken ct = default);

    Task<IReadOnlyList<SubtitleLine>> GetLinesAsync(Guid trackId, CancellationToken ct = default);

    Task<IReadOnlyList<SubtitleNameTag>> GetTagsAsync(Guid trackId, CancellationToken ct = default);

    Task<IReadOnlyList<MediaAssetChapter>> GetChaptersAsync(Guid assetId, CancellationToken ct = default);
}
