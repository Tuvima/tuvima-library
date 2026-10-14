using MediaEngine.Domain.PersonalMedia;

namespace MediaEngine.Domain.Contracts;

public interface IViewGalleryRepository
{
    /// <remarks>
    /// <c>includeDocuments</c> is false for a viewer whose profile may not use Documents: the item count then leaves
    /// documents out, so a Gallery never tells that profile how many documents it holds.
    /// </remarks>
    Task<ViewGallery?> GetAsync(Guid galleryId, bool includeDocuments = true, CancellationToken ct = default);
    Task<IReadOnlyList<ViewGallery>> GetOwnedAsync(
        Guid ownerProfileId, bool includeDocuments = true, CancellationToken ct = default);
    Task<IReadOnlyList<ViewGallery>> GetSharedWithAsync(
        Guid profileId, bool includeDocuments = true, CancellationToken ct = default);
    Task<ViewGallery> CreateAsync(CreateViewGalleryCommand command, CancellationToken ct = default);
    Task<ViewGallery?> UpdateAsync(UpdateViewGalleryCommand command, CancellationToken ct = default);
    Task<bool> DeleteAsync(Guid galleryId, CancellationToken ct = default);
    /// <remarks>Without <c>includeDocuments</c> the page leaves documents out (the viewer may not use Documents).</remarks>
    Task<ViewGalleryItemPage> GetItemsAsync(
        Guid galleryId,
        int? afterPosition = null,
        Guid? afterItemId = null,
        int limit = 100,
        bool includeDocuments = true,
        CancellationToken ct = default);
    /// <remarks>
    /// Without <c>includeDocuments</c> a document is refused with <see cref="UnauthorizedAccessException"/>, after the
    /// check that every item belongs to the Gallery owner's Personal Space.
    /// </remarks>
    Task<AddViewGalleryItemsResult> AddItemsAsync(
        Guid galleryId,
        IReadOnlyCollection<Guid> itemIds,
        bool includeDocuments = true,
        CancellationToken ct = default);
    /// <remarks>Without <c>includeDocuments</c> a document is left alone and not counted.</remarks>
    Task<int> RemoveItemsAsync(
        Guid galleryId, IReadOnlyCollection<Guid> itemIds, bool includeDocuments = true, CancellationToken ct = default);
    Task<bool> SetItemPositionAsync(
        Guid galleryId, Guid itemId, int position, bool includeDocuments = true, CancellationToken ct = default);
    Task ReplaceSharesAsync(
        Guid galleryId,
        IReadOnlyCollection<(Guid ProfileId, ViewGallerySharePermission Permission)> shares,
        CancellationToken ct = default);
    Task<IReadOnlyList<ViewGalleryShare>> GetSharesAsync(Guid galleryId, CancellationToken ct = default);
    /// <summary>
    /// Returns whether an item is exposed to a profile by an exact Gallery
    /// share, including dynamic membership in a shared Smart Gallery.
    /// </summary>
    Task<bool> IsItemSharedWithProfileAsync(Guid itemId, Guid profileId, CancellationToken ct = default);
}
