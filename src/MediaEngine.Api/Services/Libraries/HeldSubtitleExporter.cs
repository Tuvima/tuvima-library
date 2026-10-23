using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Services;

namespace MediaEngine.Api.Services.Libraries;

/// <summary>
/// Exports the subtitles that were held back while a folder's first check ran (Library setup D61). A preferred subtitle is exported beside
/// its media file when it is first downloaded; for a file in a new folder that happens during the check, when nothing may be written there,
/// so the export is made once the check passes or an administrator chooses Keep going.
/// </summary>
public sealed class HeldSubtitleExporter(
    ITextTrackRepository tracks,
    IMediaAssetRepository assets,
    ITextTrackExportService export,
    AssetPathService assetPaths,
    ILogger<HeldSubtitleExporter> logger)
{
    /// <summary>Exports the preferred subtitles of the folder with first check <paramref name="sourceId"/> that have no copy beside their file yet. Returns how many were exported.</summary>
    public async Task<int> ExportForFolderAsync(string sourceId, string folderPath, CancellationToken ct)
    {
        if (!assetPaths.ShouldKeepPreferredSubtitlesLocal)
        {
            return 0;
        }

        var exported = 0;
        foreach (var track in await tracks.ListPreferredSubtitlesWithoutSidecarInFolderAsync(sourceId, ct).ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();
            var asset = await assets.FindByIdAsync(track.AssetId, ct).ConfigureAwait(false);
            if (asset is null)
            {
                continue;
            }

            var path = await export.ExportPreferredSubtitleAsync(asset, track, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            track.SidecarPath = path;
            await tracks.UpsertAsync(track, ct).ConfigureAwait(false);
            exported++;
        }

        if (exported > 0)
        {
            logger.LogInformation("{Count} held subtitle(s) were exported beside their files in {Path}", exported, folderPath);
        }

        return exported;
    }
}
