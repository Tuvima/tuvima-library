using System.Collections.Concurrent;
using MediaEngine.Domain.Services;
using MediaEngine.Providers.Helpers;

namespace MediaEngine.Api.Services.Artwork;

/// <summary>
/// Sized copies of character pictures (right-sized pictures spec, B4). Character pictures have no data-store row for
/// their copies, so each copy is made the first time its size is asked for and kept in the managed derived folder,
/// keyed by portrait id and size. A copy older than its original is made again.
/// </summary>
public static class CharacterPortraitRenditions
{
    private static readonly ConcurrentDictionary<string, Lazy<Task<bool>>> InFlight = new();

    /// <summary>
    /// The file to send for a portrait at <paramref name="size"/>. A missing, empty or unrecognised size means the original.
    /// When a copy cannot be made the original is sent and reported as a stand-in.
    /// </summary>
    public static async Task<ResolvedArtworkFile> ResolveAsync(
        Guid portraitId,
        string originalPath,
        string? size,
        AssetPathService assetPaths,
        ILogger logger,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(originalPath);
        ArgumentNullException.ThrowIfNull(assetPaths);
        ArgumentNullException.ThrowIfNull(logger);

        if (!ArtworkVariantHelper.TryParseRenditionSize(size, out var key))
        {
            return new ResolvedArtworkFile(originalPath, true);
        }

        var extension = string.Equals(Path.GetExtension(originalPath), ".png", StringComparison.OrdinalIgnoreCase) ? ".png" : ".jpg";
        var copyPath = assetPaths.GetCentralDerivedPath("CharacterPortrait", portraitId, "renditions", key + extension);
        if (IsCurrent(copyPath, originalPath))
        {
            return new ResolvedArtworkFile(copyPath, true);
        }

        var work = InFlight.GetOrAdd(copyPath, _ => new Lazy<Task<bool>>(() => MakeCopyAsync(copyPath, originalPath, key, logger)));
        var made = await work.Value.WaitAsync(ct).ConfigureAwait(false);
        return made && IsCurrent(copyPath, originalPath)
            ? new ResolvedArtworkFile(copyPath, true)
            : new ResolvedArtworkFile(originalPath, false);
    }

    private static async Task<bool> MakeCopyAsync(string copyPath, string originalPath, string key, ILogger logger)
    {
        try
        {
            // Not tied to the first request's cancellation: other requests may be waiting on this work.
            using var slot = await ArtworkVariantHelper.EnterRenditionWorkAsync(CancellationToken.None).ConfigureAwait(false);
            return await Task.Run(() => ArtworkVariantHelper.WriteResizedCopy(
                originalPath, copyPath, ArtworkVariantHelper.LongEdgeFor(key))).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not make the {Size} copy of character picture {Path}.", key, originalPath);
            return false;
        }
        finally
        {
            InFlight.TryRemove(copyPath, out _);
        }
    }

    private static bool IsCurrent(string copyPath, string originalPath) =>
        File.Exists(copyPath) && File.GetLastWriteTimeUtc(copyPath) >= File.GetLastWriteTimeUtc(originalPath);
}
