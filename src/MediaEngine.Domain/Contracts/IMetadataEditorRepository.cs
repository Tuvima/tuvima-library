using MediaEngine.Domain.Models;

namespace MediaEngine.Domain.Contracts;

public interface IMetadataEditorRepository
{
    Task<MetadataReclassifyTarget> ResolveReclassifyTargetAsync(Guid entityId, CancellationToken ct = default);
    Task UpdateWorkMediaTypeAsync(Guid workId, string mediaType, CancellationToken ct = default);
    Task<MetadataEditorLaunchContext?> ResolveEditorLaunchAsync(Guid entityId, CancellationToken ct = default);
    Task<IReadOnlyDictionary<string, string>> GetDisplayOverridesAsync(Guid workId, CancellationToken ct = default);

    Task<Guid?> ResolveRepresentativeAssetAsync(
        IReadOnlyCollection<Guid> candidateWorkIds,
        CancellationToken ct = default);

    Task<MetadataArtworkResolutionContext> ResolveArtworkContextAsync(
        Guid entityId,
        CancellationToken ct = default);
}
