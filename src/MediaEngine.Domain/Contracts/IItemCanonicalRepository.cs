using MediaEngine.Domain.Models;

namespace MediaEngine.Domain.Contracts;

public interface IItemCanonicalRepository
{
    Task<ItemCanonicalWorkAssetContext?> ResolveWorkAssetContextAsync(Guid entityId, CancellationToken ct = default);
    Task<ItemCanonicalDisplayOverrideState> LoadDisplayOverridesAsync(Guid workId, CancellationToken ct = default);

    Task<bool> SaveDisplayOverridesAsync(
        Guid workId,
        IReadOnlyDictionary<string, string> overrides,
        CancellationToken ct = default);

    /// <summary>The media type name stored on a work ("Videos", "Movies" and so on), or null when there is no such work.</summary>
    Task<string?> GetWorkMediaTypeAsync(Guid workId, CancellationToken ct = default);

    Task<Guid?> ResolveWorkIdForAssetAsync(Guid assetId, CancellationToken ct = default);
    Task<ItemCanonicalWorkWikidataState?> LoadWorkWikidataStateAsync(Guid workId, CancellationToken ct = default);
    Task UpdateWorkIdentityAsync(Guid workId, string wikidataQid, CancellationToken ct = default);

    Task DeleteIdentityArtifactsAsync(
        IReadOnlyCollection<ItemCanonicalIdentityArtifact> artifacts,
        CancellationToken ct = default);

    Task ReplaceExternalIdentifiersAsync(
        Guid workId,
        IReadOnlyCollection<string> keysToRemove,
        IReadOnlyDictionary<string, string> replacements,
        CancellationToken ct = default);

    Task<string> AppendRejectedQidAsync(Guid workId, string? rejectedQid, CancellationToken ct = default);
}
