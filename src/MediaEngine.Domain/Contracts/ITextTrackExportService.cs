using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Entities;

namespace MediaEngine.Domain.Contracts;

/// <summary>
/// Exports a managed subtitle beside owned media only when the configured source
/// explicitly permits metadata write-back.
/// </summary>
public interface ITextTrackExportService
{
    Task<string?> ExportPreferredSubtitleAsync(
        MediaAsset asset,
        TextTrack track,
        CancellationToken ct = default);

    /// <summary>
    /// Writes the preferred lyrics beside the song (<c>Song.lrc</c> for timed lyrics, <c>Song.txt</c> for plain ones) only when write-back is
    /// on and the source allows it. A file that is not the Library's own is never replaced, a person's own lyrics file is never copied over,
    /// and a changed choice replaces the one file the Library wrote, so the folder holds a single lyrics file. Returns the path written, or
    /// <c>null</c> when nothing was written.
    /// </summary>
    Task<string?> ExportPreferredLyricsAsync(
        MediaAsset asset,
        TextTrack track,
        CancellationToken ct = default);

    /// <summary>
    /// Removes the lyrics file the Library itself wrote beside the song for this track, under the same write-back, source and folder-hold
    /// rules as writing it. Never touches a person's own file or a file outside the song's folder. Returns true when a file was removed.
    /// </summary>
    Task<bool> RemoveWrittenLyricsAsync(
        MediaAsset asset,
        TextTrack track,
        CancellationToken ct = default);
}
