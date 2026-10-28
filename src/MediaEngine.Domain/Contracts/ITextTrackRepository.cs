using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;

namespace MediaEngine.Domain.Contracts;

/// <summary>A source result marked wrong for one item: the source and the result's id inside it.</summary>
public sealed record TextTrackRejection(string Provider, string SourceId);

public interface ITextTrackRepository
{
    Task<IReadOnlyList<TextTrack>> GetByAssetAsync(Guid assetId, TextTrackKind? kind = null, CancellationToken ct = default);

    Task<TextTrack?> FindByIdAsync(Guid id, CancellationToken ct = default);

    Task<TextTrack?> GetPreferredAsync(Guid assetId, TextTrackKind kind, string? language = null, CancellationToken ct = default);

    Task UpsertAsync(TextTrack track, CancellationToken ct = default);

    Task SetPreferredAsync(Guid id, CancellationToken ct = default);

    /// <summary>Removes one track's row. The caller removes the managed file.</summary>
    Task DeleteAsync(Guid id, CancellationToken ct = default);

    /// <summary>The results a person marked wrong for this item, so they are never picked again (Plugin architecture spec C24).</summary>
    Task<IReadOnlyList<TextTrackRejection>> GetRejectionsAsync(Guid assetId, TextTrackKind kind, CancellationToken ct = default);

    Task AddRejectionAsync(Guid assetId, TextTrackKind kind, string provider, string sourceId, CancellationToken ct = default);

    /// <summary>
    /// The preferred downloaded subtitles of files inside the folder of the first check <paramref name="sourceId"/> that have not been exported
    /// beside their media file (no sidecar yet), leaving out files that are Unmatched (nothing is written into them). A folder whose first
    /// check held the export lists them when the hold ends (Library setup D61).
    /// </summary>
    Task<IReadOnlyList<TextTrack>> ListPreferredSubtitlesWithoutSidecarInFolderAsync(string sourceId, CancellationToken ct = default);
}
