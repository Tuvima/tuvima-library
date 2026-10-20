using MediaEngine.Domain.Models;

namespace MediaEngine.Domain.Contracts;

/// <summary>
/// Read-only facts about one person's Trash in View: which personal items are trashed, the files they use, which of those
/// paths other items use too, which items are waiting to be added to the Shared Library, and which Manual Galleries hold
/// them. Everything is limited to the Personal Space and owner named, so another person's Trash can never be returned.
/// </summary>
public interface IViewTrashRepository
{
    /// <summary>
    /// Counts by kind and the size of the files used by the owner's trashed personal items, with no file listing. Documents are
    /// left out when <paramref name="includeDocuments"/> is false (a profile without the Documents permission).
    /// </summary>
    Task<ViewTrashTotals> GetTotalsAsync(
        Guid personalSpaceId, Guid ownerProfileId, bool includeDocuments, CancellationToken ct = default);

    /// <summary>
    /// The owner's trashed personal items (<c>trashed_at</c> set) with all of their files, in every role. Each file lists
    /// only the places it is indexed at inside the item's own library.
    /// </summary>
    Task<IReadOnlyList<ViewTrashItem>> GetTrashedItemsAsync(Guid personalSpaceId, Guid ownerProfileId, CancellationToken ct = default);

    /// <summary>
    /// For the owner's trashed items: each path that another item (any owner, trashed or not) also uses, with that other
    /// item. Only the same path in the other item's own library counts; matching content on a different path does not.
    /// </summary>
    Task<IReadOnlyList<ViewTrashPathUse>> GetPathUsesByOtherItemsAsync(Guid personalSpaceId, Guid ownerProfileId, CancellationToken ct = default);

    /// <summary>The owner's trashed items that are waiting to be added to the Shared Library or are being copied there.</summary>
    Task<IReadOnlySet<Guid>> GetItemsWithPendingContributionAsync(Guid personalSpaceId, Guid ownerProfileId, CancellationToken ct = default);

    /// <summary>Every Manual Gallery that holds one of the owner's trashed items, one row per gallery and item.</summary>
    Task<IReadOnlyList<ViewTrashGalleryMembership>> GetManualGalleryMembershipsAsync(Guid personalSpaceId, Guid ownerProfileId, CancellationToken ct = default);
}
