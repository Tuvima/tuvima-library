using MediaEngine.Domain.Entities;

namespace MediaEngine.Domain.Contracts;

/// <summary>Persistence for page bookmarks of comics and PDFs, kept in the one shared bookmark table as kind <c>page</c>.</summary>
public interface IPageBookmarkRepository
{
    /// <summary>The person's own bookmarks on the file, and the ones in Everyone's. Nobody else's personal bookmarks.</summary>
    Task<IReadOnlyList<PageBookmark>> ListByAssetAsync(string userId, Guid assetId, CancellationToken ct = default);

    Task<PageBookmark?> FindByIdAsync(Guid id, CancellationToken ct = default);

    Task InsertAsync(PageBookmark bookmark, CancellationToken ct = default);

    Task DeleteAsync(Guid id, CancellationToken ct = default);
}
