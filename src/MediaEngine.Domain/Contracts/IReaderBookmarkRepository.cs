using MediaEngine.Domain.Entities;

namespace MediaEngine.Domain.Contracts;

/// <summary>
/// Persistence contract for EPUB reader bookmarks.
/// </summary>
public interface IReaderBookmarkRepository
{
    Task<IReadOnlyList<ReaderBookmark>> ListByAssetAsync(string userId, Guid assetId, CancellationToken ct = default);
    /// <summary>Every profile's personal bookmarks in the book, each with its owner's name (the administrator's Everyone's list).</summary>
    Task<IReadOnlyList<ReaderBookmark>> ListEveryonesByAssetAsync(Guid assetId, CancellationToken ct = default);
    /// <summary>Makes a personal bookmark Global, replacing personal bookmarks in the same paragraph. Null when there is no such personal bookmark.</summary>
    Task<ReaderBookmark?> PromoteAsync(Guid id, string? name, CancellationToken ct = default);
    /// <summary>
    /// Applies a bookmark set in one write (Global bookmarks spec D8): the bookmarks in <paramref name="deleteIds"/> go (only the
    /// caller's own personal ones and, for an administrator, Global ones), every bookmark in <paramref name="adds"/> is saved (Global
    /// when <paramref name="addAsGlobal"/>, replacing any personal bookmark in the same paragraph), and with
    /// <paramref name="replaceGlobalSet"/> every Global bookmark in the book goes first.
    /// </summary>
    Task<ReaderSetOutcome> ApplySetAsync(
        Guid profileId, Guid assetId, IReadOnlyList<ReaderSetBookmark> adds, IReadOnlyCollection<Guid> deleteIds,
        bool addAsGlobal, bool replaceGlobalSet, CancellationToken ct = default);
    Task<ReaderBookmark?> FindByIdAsync(Guid id, CancellationToken ct = default);
    Task InsertAsync(ReaderBookmark bookmark, CancellationToken ct = default);
    Task DeleteAsync(Guid id, CancellationToken ct = default);
}
