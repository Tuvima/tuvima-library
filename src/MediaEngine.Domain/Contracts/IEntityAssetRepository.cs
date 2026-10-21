using MediaEngine.Domain.Entities;

namespace MediaEngine.Domain.Contracts;

/// <summary>
/// CRUD operations for the <c>entity_assets</c> table — formalized typed
/// image storage for any entity (Work, Person, Universe, FictionalEntity).
/// </summary>
public interface IEntityAssetRepository
{
    /// <summary>Get all assets for an entity, optionally filtered by type.</summary>
    Task<IReadOnlyList<EntityAsset>> GetByEntityAsync(
        string entityId, string? assetType = null, CancellationToken ct = default);

    /// <summary>Get assets for multiple entities in bounded batches.</summary>
    Task<IReadOnlyList<EntityAsset>> GetByEntitiesAsync(
        IReadOnlyCollection<string> entityIds,
        string? assetType = null,
        CancellationToken ct = default);

    /// <summary>Find a single entity asset by its variant id.</summary>
    Task<EntityAsset?> FindByIdAsync(Guid assetId, CancellationToken ct = default);

    /// <summary>Insert or update an asset. Upsert keyed on (entity_id, entity_type, asset_type, source_provider).</summary>
    Task UpsertAsync(EntityAsset asset, CancellationToken ct = default);

    /// <summary>
    /// Set a specific asset as preferred for its entity + asset type.
    /// Clears preferred flag on other assets of the same type for the same entity.
    /// </summary>
    Task SetPreferredAsync(Guid assetId, CancellationToken ct = default);

    /// <summary>
    /// Makes a variant the user's own choice for its entity + asset type: preferred, and marked as a user
    /// override so later artwork passes keep it. A provider image picked earlier for the same slot goes back
    /// to provider-managed; uploads and artwork-library choices stay the user's.
    /// </summary>
    Task SetUserChoiceAsync(Guid assetId, CancellationToken ct = default);

    /// <summary>
    /// Deletes a variant, and its artwork-library row when no other entity links that picture any more.
    /// Returns the variant's files that no remaining row references, for the caller to remove; artwork is
    /// content-addressed, so another entity can be served by the same file.
    /// </summary>
    Task<IReadOnlyList<string>> DeleteVariantAsync(Guid assetId, CancellationToken ct = default);

    /// <summary>Delete all assets for an entity (used during entity cleanup).</summary>
    Task DeleteByEntityAsync(string entityId, CancellationToken ct = default);

    /// <summary>Delete a single variant row.</summary>
    Task DeleteAsync(Guid assetId, CancellationToken ct = default);

    /// <summary>Get the preferred asset of a given type for an entity.</summary>
    Task<EntityAsset?> GetPreferredAsync(
        string entityId, string assetType, CancellationToken ct = default);

    /// <summary>
    /// Get all preferred assets for a set of entities.
    /// Returned rows are limited to preferred variants only.
    /// </summary>
    Task<IReadOnlyList<EntityAsset>> GetPreferredByEntitiesAsync(
        IReadOnlyCollection<string> entityIds,
        CancellationToken ct = default);

    /// <summary>
    /// Get every preferred artwork asset for maintenance and repair passes.
    /// </summary>
    Task<IReadOnlyList<EntityAsset>> GetPreferredArtworkAsync(CancellationToken ct = default);

    /// <summary>
    /// Records the paths of a variant's tiny, small, medium and large copies and nothing else on the row, so a copy made
    /// in the background cannot overwrite a choice (preferred, user override) made in the meantime.
    /// </summary>
    Task UpdateRenditionPathsAsync(
        Guid assetId,
        string? tinyPath,
        string? smallPath,
        string? mediumPath,
        string? largePath,
        CancellationToken ct = default);
}
