using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;

namespace MediaEngine.Domain.Contracts;

public interface ITextTrackRepository
{
    Task<IReadOnlyList<TextTrack>> GetByAssetAsync(Guid assetId, TextTrackKind? kind = null, CancellationToken ct = default);

    Task<TextTrack?> FindByIdAsync(Guid id, CancellationToken ct = default);

    Task<TextTrack?> GetPreferredAsync(Guid assetId, TextTrackKind kind, string? language = null, CancellationToken ct = default);

    Task UpsertAsync(TextTrack track, CancellationToken ct = default);

    Task SetPreferredAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// The preferred downloaded subtitles of files inside the folder <paramref name="folderPath"/> that have not been exported beside their
    /// media file (no sidecar yet), leaving out files that are Unmatched (nothing is written into them). A folder whose first check held the export lists them when the hold ends (Library setup D61).
    /// </summary>
    Task<IReadOnlyList<TextTrack>> ListPreferredSubtitlesWithoutSidecarInFolderAsync(string folderPath, CancellationToken ct = default);
}
