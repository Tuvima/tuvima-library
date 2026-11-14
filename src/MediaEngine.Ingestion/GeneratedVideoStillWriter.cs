using MediaEngine.Domain;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Services;
using MediaEngine.Providers.Helpers;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Ingestion;

/// <summary>
/// Keeps a frame taken from a video as that video's own picture (issue 997). Before this, a frame lived only in the frame cache and in
/// the folder's cover, so a video inside a collection had no picture of its own for the details page, its rows, the editor header,
/// Needs Review or the Continue tile. The frame is saved like any other picture, from the source "generated": the artwork picker lists
/// it, a real still replaces it, and nothing is written beside the media.
/// </summary>
public sealed class GeneratedVideoStillWriter(
    IEntityAssetRepository assets,
    ICanonicalValueRepository canonicals,
    AssetPathService assetPaths,
    ILogger? logger = null)
{
    public const string GeneratedSource = "generated";

    /// <summary>Saves the frame for <paramref name="workId"/> and returns what happened (also written to the log).</summary>
    public async Task<GeneratedStillOutcome> SaveAsync(Guid workId, byte[] frame, CancellationToken ct = default)
    {
        var kind = AssetType.EpisodeStill.ToString();
        var existing = await assets.GetByEntityAsync(workId.ToString(), kind, ct).ConfigureAwait(false);
        if (existing.Any(asset => asset.IsPreferred && !string.Equals(asset.SourceProvider, GeneratedSource, StringComparison.OrdinalIgnoreCase)))
        {
            logger?.LogInformation("Video frame step: work {WorkId} already has a real still, so the generated frame was not saved", workId);
            return GeneratedStillOutcome.KeptRealStill;
        }

        var current = existing.FirstOrDefault(asset => string.Equals(asset.SourceProvider, GeneratedSource, StringComparison.OrdinalIgnoreCase));
        if (current is { LocalImagePath: { Length: > 0 } path } && File.Exists(path) && new FileInfo(path).Length == frame.Length)
        {
            logger?.LogInformation("Video frame step: work {WorkId} already has this generated frame (reused)", workId);
            return GeneratedStillOutcome.Reused;
        }

        var variant = current ?? new EntityAsset
        {
            Id = Guid.NewGuid(),
            EntityId = workId.ToString(),
            EntityType = "Work",
            AssetTypeValue = kind,
            AssetClassValue = "Artwork",
            StorageLocationValue = "Central",
            OwnerScope = "Episode",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        variant.ImageUrl = string.Empty;
        variant.SourceProvider = GeneratedSource;
        variant.IsUserOverride = false;
        variant.LocalImagePath = assetPaths.GetCentralAssetPath("Work", workId, kind, variant.Id, ".jpg");
        AssetPathService.EnsureDirectory(variant.LocalImagePath);
        await File.WriteAllBytesAsync(variant.LocalImagePath, frame, ct).ConfigureAwait(false);
        ArtworkVariantHelper.StampMetadataAndRenditions(variant, assetPaths);
        await assets.UpsertAsync(variant, ct).ConfigureAwait(false);
        await assets.SetPreferredAsync(variant.Id, ct).ConfigureAwait(false);
        await canonicals.UpsertBatchAsync(
            [.. ArtworkCanonicalHelper.CreatePreferredAssetCanonicals(workId, variant, DateTimeOffset.UtcNow)], ct).ConfigureAwait(false);
        logger?.LogInformation("Video frame step: saved a generated frame as the picture of work {WorkId}", workId);
        return GeneratedStillOutcome.Saved;
    }
}

public enum GeneratedStillOutcome
{
    Saved,
    Reused,
    KeptRealStill,
}
