using System.Collections.Concurrent;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Services;
using MediaEngine.Providers.Helpers;

namespace MediaEngine.Api.Services.Artwork;

/// <inheritdoc cref="IArtworkRenditionService"/>
public sealed class ArtworkRenditionService(
    IEntityAssetRepository assets,
    AssetPathService assetPaths,
    ArtworkRenditionQueue queue,
    ILogger<ArtworkRenditionService> logger) : IArtworkRenditionService
{
    // One creation per picture at a time; a second request for the same picture waits for the first.
    private readonly ConcurrentDictionary<Guid, Lazy<Task<EntityAsset?>>> _inFlight = new();

    public async Task<ResolvedArtworkFile?> ResolveAsync(EntityAsset asset, string? size, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(asset);

        var original = ExistingFile(asset.LocalImagePath);
        if (!ArtworkVariantHelper.TryParseRenditionSize(size, out var key)
            || !ArtworkVariantHelper.ShouldGenerateRenditions(asset.AssetTypeValue))
        {
            return original is null ? null : new ResolvedArtworkFile(original, true);
        }

        var exact = ExistingFile(ArtworkVariantHelper.GetRenditionPath(asset, key));
        if (exact is not null)
        {
            return new ResolvedArtworkFile(exact, true);
        }

        var keys = ArtworkVariantHelper.RenditionSizeKeys;
        var index = keys.ToList().IndexOf(key);
        for (var i = index + 1; i < keys.Count; i++)
        {
            var larger = ExistingFile(ArtworkVariantHelper.GetRenditionPath(asset, keys[i]));
            if (larger is not null)
            {
                // Never the original: send the next bigger copy now and make the missing one in the background.
                queue.TryEnqueue(asset.Id);
                return new ResolvedArtworkFile(larger, false);
            }
        }

        if (original is null)
        {
            // Nothing to make copies from: the biggest smaller copy beats no picture.
            for (var i = index - 1; i >= 0; i--)
            {
                var smaller = ExistingFile(ArtworkVariantHelper.GetRenditionPath(asset, keys[i]));
                if (smaller is not null)
                {
                    return new ResolvedArtworkFile(smaller, false);
                }
            }

            return null;
        }

        // Only the original exists: make the copies now (a few at a time), then send the size that was asked for.
        await EnsureCopiesAsync(asset, ct).ConfigureAwait(false);
        exact = ExistingFile(ArtworkVariantHelper.GetRenditionPath(asset, key));
        if (exact is not null)
        {
            return new ResolvedArtworkFile(exact, true);
        }

        logger.LogDebug(
            "Artwork {AssetId} has no {Size} copy and one could not be made; sending the original.",
            asset.Id,
            key);
        return new ResolvedArtworkFile(original, false);
    }

    public async Task CreateMissingCopiesAsync(Guid assetId, CancellationToken ct)
    {
        var asset = await assets.FindByIdAsync(assetId, ct).ConfigureAwait(false);
        if (asset is null || !ArtworkVariantHelper.HasMissingRenditions(asset))
        {
            return;
        }

        await EnsureCopiesAsync(asset, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Makes the asset's missing copies and records them. Returns the asset whose paths were filled in (the caller's
    /// own, or the one a concurrent request made them on, copied across), or null when nothing could be made.
    /// </summary>
    private async Task<EntityAsset?> EnsureCopiesAsync(EntityAsset asset, CancellationToken ct)
    {
        var work = _inFlight.GetOrAdd(asset.Id, _ => new Lazy<Task<EntityAsset?>>(() => MakeCopiesAsync(asset)));
        var done = await work.Value.WaitAsync(ct).ConfigureAwait(false);
        if (done is not null && !ReferenceEquals(done, asset))
        {
            asset.LocalImagePathTiny = done.LocalImagePathTiny;
            asset.LocalImagePathSmall = done.LocalImagePathSmall;
            asset.LocalImagePathMedium = done.LocalImagePathMedium;
            asset.LocalImagePathLarge = done.LocalImagePathLarge;
        }

        return done;
    }

    private async Task<EntityAsset?> MakeCopiesAsync(EntityAsset asset)
    {
        try
        {
            // Not tied to the first request's cancellation: other requests may be waiting on this work.
            using var slot = await ArtworkVariantHelper.EnterRenditionWorkAsync(CancellationToken.None).ConfigureAwait(false);
            var made = await Task.Run(() => ArtworkVariantHelper.EnsureMissingRenditions(asset, assetPaths)).ConfigureAwait(false);
            if (!made)
            {
                return null;
            }

            await assets.UpdateRenditionPathsAsync(
                asset.Id,
                asset.LocalImagePathTiny,
                asset.LocalImagePathSmall,
                asset.LocalImagePathMedium,
                asset.LocalImagePathLarge,
                CancellationToken.None).ConfigureAwait(false);
            return asset;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not make the missing copies of artwork {AssetId}.", asset.Id);
            return null;
        }
        finally
        {
            _inFlight.TryRemove(asset.Id, out _);
        }
    }

    private static string? ExistingFile(string? path) =>
        !string.IsNullOrWhiteSpace(path) && File.Exists(path) ? path : null;
}
