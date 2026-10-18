using MediaEngine.Domain.PersonalMedia;

namespace MediaEngine.Domain.Contracts;

public interface ICollectionViewSourceRepository
{
    Task<CollectionViewSource> AddGalleryAsync(
        AddCollectionGallerySourceCommand command,
        CancellationToken ct = default);
    Task<CollectionViewSource> AddSmartRuleAsync(
        AddCollectionViewRuleSourceCommand command,
        CancellationToken ct = default);
    Task<IReadOnlyList<CollectionViewSource>> ListAsync(
        Guid collectionId,
        Guid ownerProfileId,
        CancellationToken ct = default);
    Task<CollectionViewSource?> UpdateAsync(
        UpdateCollectionViewSourceCommand command,
        CancellationToken ct = default);
    Task<bool> RemoveAsync(
        Guid collectionId,
        Guid sourceId,
        Guid ownerProfileId,
        CancellationToken ct = default);
    /// <summary>
    /// Removes a source from a manually curated collection only while another member remains (a collection item, or
    /// another source). The check and the delete are one write, so two removals cannot both pass and empty the collection.
    /// </summary>
    Task<CollectionViewSourceRemoval> RemoveUnlessLastMemberAsync(
        Guid collectionId,
        Guid sourceId,
        Guid ownerProfileId,
        CancellationToken ct = default);
    Task<IReadOnlyList<CollectionViewSourceProjection>> GetAuthorizedProjectionAsync(
        IReadOnlyCollection<Guid> collectionIds,
        Guid viewerProfileId,
        CancellationToken ct = default);
}
