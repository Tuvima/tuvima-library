using System.Collections.Concurrent;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Services;

namespace MediaEngine.Providers.Helpers;

/// <summary>
/// Makes sure a person's face file is a managed <c>Headshot</c> picture with all its copies (tiny, small, medium, large),
/// so a face is served at the size a screen asks for like any other artwork, never as the raw downloaded original
/// (right-sized pictures spec, B3).
/// </summary>
public static class PersonHeadshotAssets
{
    // One adoption per person at a time, so two requests for the same face cannot register it twice.
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> PersonLocks = new();

    /// <summary>
    /// Returns the managed Headshot for the face file at <paramref name="localPath"/>: the person's existing Headshot that
    /// already uses that file (completing any missing copies), or a new one stamped with copies. The new one is preferred
    /// only when the person has no preferred Headshot yet. Returns null when the file is missing or is not a readable picture.
    /// </summary>
    public static async Task<EntityAsset?> EnsureManagedAsync(
        Guid personId,
        string localPath,
        string? sourceProvider,
        string? imageUrl,
        IEntityAssetRepository repo,
        AssetPathService paths,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(repo);
        ArgumentNullException.ThrowIfNull(paths);

        if (personId == Guid.Empty || string.IsNullOrWhiteSpace(localPath) || !File.Exists(localPath))
        {
            return null;
        }

        var personLock = PersonLocks.GetOrAdd(personId, _ => new SemaphoreSlim(1, 1));
        await personLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var entityId = personId.ToString("D");
            var existing = await repo.GetByEntityAsync(entityId, "Headshot", ct).ConfigureAwait(false);
            var fullPath = Path.GetFullPath(localPath);
            var match = existing.FirstOrDefault(asset =>
                !string.IsNullOrWhiteSpace(asset.LocalImagePath)
                && string.Equals(Path.GetFullPath(asset.LocalImagePath), fullPath, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                if (ArtworkVariantHelper.HasMissingRenditions(match))
                {
                    using var slot = await ArtworkVariantHelper.EnterRenditionWorkAsync(ct).ConfigureAwait(false);
                    if (await Task.Run(() => ArtworkVariantHelper.EnsureMissingRenditions(match, paths), ct).ConfigureAwait(false))
                    {
                        await repo.UpdateRenditionPathsAsync(
                            match.Id,
                            match.LocalImagePathTiny,
                            match.LocalImagePathSmall,
                            match.LocalImagePathMedium,
                            match.LocalImagePathLarge,
                            ct).ConfigureAwait(false);
                    }
                }

                return match;
            }

            var id = Guid.NewGuid();
            var asset = new EntityAsset
            {
                Id = id,
                EntityId = entityId,
                EntityType = "Person",
                AssetTypeValue = "Headshot",
                ImageUrl = string.IsNullOrWhiteSpace(imageUrl) ? $"/stream/artwork/{id}" : imageUrl,
                LocalImagePath = localPath,
                SourceProvider = sourceProvider,
                AssetClassValue = "Artwork",
                StorageLocationValue = "Central",
                OwnerScope = "Person",
                IsPreferred = !existing.Any(candidate => candidate.IsPreferred),
                CreatedAt = DateTimeOffset.UtcNow,
            };

            using (await ArtworkVariantHelper.EnterRenditionWorkAsync(ct).ConfigureAwait(false))
            {
                await Task.Run(() => ArtworkVariantHelper.StampMetadataAndRenditions(asset, paths), ct).ConfigureAwait(false);
            }

            if (asset.WidthPx is null)
            {
                // Not a readable picture: nothing to manage.
                return null;
            }

            await repo.UpsertAsync(asset, ct).ConfigureAwait(false);
            if (asset.IsPreferred)
            {
                await repo.SetPreferredAsync(asset.Id, ct).ConfigureAwait(false);
            }

            return asset;
        }
        finally
        {
            personLock.Release();
        }
    }
}
