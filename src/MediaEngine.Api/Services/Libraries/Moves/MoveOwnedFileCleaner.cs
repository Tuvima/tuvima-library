using MediaEngine.Domain.Models;
using MediaEngine.Domain.Services;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Api.Services.Libraries.Moves;

/// <summary>
/// Removes the files Tuvima itself wrote for a title that a move merged into another (its pictures, its text tracks), once the move has
/// committed and nothing points at them any more. Only files inside Tuvima's own data folder are ever touched: a media file, a sidecar next
/// to one, or anything outside that folder is never deleted here, whatever the list says.
/// </summary>
public sealed class MoveOwnedFileCleaner(AssetPathService assetPaths, ILogger<MoveOwnedFileCleaner> logger)
{
    public void Remove(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0)
        {
            return;
        }

        var root = Path.TrimEndingDirectorySeparator(Normalize(assetPaths.AssetsRoot));
        foreach (var path in paths)
        {
            var full = Normalize(path);

            // Outside Tuvima's data folder (or the folder itself): not ours to remove.
            if (full.Length == 0 || ExcludedSourceFilePaths.RelativePathOf(root, full) is not { Length: > 0 })
            {
                continue;
            }

            try
            {
                File.Delete(full);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "Could not remove Library's own file {FileName} after a move", Path.GetFileName(full));
            }
        }
    }

    private static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // A path that cannot be made absolute cannot be shown to be inside the data folder, so it is left alone.
            _ = ex;
            return string.Empty;
        }
    }
}
