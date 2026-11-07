using MediaEngine.Domain.Entities;

namespace MediaEngine.Domain.Contracts;

/// <summary>
/// Persistence contract for EPUB reader bookmarks.
/// </summary>
public interface IReaderBookmarkRepository
{
    Task<IReadOnlyList<ReaderBookmark>> ListByAssetAsync(string userId, Guid assetId, CancellationToken ct = default);
    Task<ReaderBookmark?> FindByIdAsync(Guid id, CancellationToken ct = default);
    Task InsertAsync(ReaderBookmark bookmark, CancellationToken ct = default);
    Task DeleteAsync(Guid id, CancellationToken ct = default);
}
