using MediaEngine.Domain;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;

namespace MediaEngine.Api.Services.Metadata;

/// <summary>
/// Keeps an item's canonical picture values (<c>cover_url</c>, its sized copies, <c>cover_state</c> and the others
/// <see cref="ArtworkCanonicalHelper"/> writes) in step with the picture that is chosen for it. A show's page and every tile read those
/// values, not the stored picture list, so a choice that skips this step looks saved in the editor and never reaches the page (universal
/// editor v7 D10, issue 990).
/// </summary>
public sealed class ArtworkCanonicalSync(ICanonicalValueRepository canonicalRepo, IEntityAssetRepository entityAssets)
{
    /// <summary>
    /// Reads the picture now chosen for a work's slot and writes the canonical values to match it (or marks the slot as having none). A slot
    /// the canonical values do not carry, and anything that is not a work, is left alone.
    /// </summary>
    public async Task SyncChosenAsync(string entityType, Guid entityId, string? slot, CancellationToken ct)
    {
        if (!string.Equals(entityType, "Work", StringComparison.OrdinalIgnoreCase)
            || slot is null
            || ArtworkScopeService.NormalizeUploadedArtworkType(slot) is not { } assetType)
        {
            return;
        }

        var chosen = await entityAssets.GetPreferredAsync(entityId.ToString("D"), assetType, ct).ConfigureAwait(false);
        await SyncAsync(entityId, assetType, chosen, ct).ConfigureAwait(false);
    }

    public async Task SyncAsync(Guid entityId, string assetType, EntityAsset? preferredAsset, CancellationToken ct)
    {
        var canonicalKey = ArtworkScopeService.GetArtworkCanonicalKey(assetType);

        if (preferredAsset is not null && string.Equals(assetType, "EpisodeStill", StringComparison.OrdinalIgnoreCase))
        {
            // A real picture (from TheTVDB or chosen by a person) replaces the stand-in with no confirmation.
            await canonicalRepo.DeleteByKeyAsync(entityId, MetadataFieldConstants.EpisodeStillStandIn, ct).ConfigureAwait(false);
        }

        if (preferredAsset is null)
        {
            await canonicalRepo.DeleteByKeyAsync(entityId, canonicalKey, ct).ConfigureAwait(false);

            if (string.Equals(assetType, "CoverArt", StringComparison.OrdinalIgnoreCase))
            {
                await canonicalRepo.UpsertBatchAsync(
                    ArtworkCanonicalHelper.CreateFlags(
                        entityId,
                        coverState: "missing",
                        coverSource: null,
                        heroState: "missing",
                        lastScoredAt: DateTimeOffset.UtcNow,
                        settled: true),
                    ct).ConfigureAwait(false);
            }

            return;
        }

        var canonicals = ArtworkCanonicalHelper.CreatePreferredAssetCanonicals(
            entityId,
            preferredAsset,
            DateTimeOffset.UtcNow);

        if (string.Equals(assetType, "CoverArt", StringComparison.OrdinalIgnoreCase))
        {
            var coverSource = string.Equals(preferredAsset.SourceProvider, "user_upload", StringComparison.OrdinalIgnoreCase)
                ? "manual"
                : !string.IsNullOrWhiteSpace(preferredAsset.SourceProvider)
                    ? "provider"
                    : "stored";

            canonicals.AddRange(ArtworkCanonicalHelper.CreateFlags(
                entityId,
                coverState: "present",
                coverSource: coverSource,
                heroState: "missing",
                lastScoredAt: DateTimeOffset.UtcNow,
                settled: true));
        }

        await canonicalRepo.UpsertBatchAsync(canonicals, ct).ConfigureAwait(false);
    }
}
