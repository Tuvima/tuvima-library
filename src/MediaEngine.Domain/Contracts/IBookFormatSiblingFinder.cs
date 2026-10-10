namespace MediaEngine.Domain.Contracts;

/// <summary>
/// Finds an existing standalone Books Work that already represents the same book in another
/// file format, so a second format (EPUB next to AZW3, ...) becomes an Edition of that Work
/// instead of a duplicate Work.
/// </summary>
public interface IBookFormatSiblingFinder
{
    /// <summary>
    /// Returns the id of the standalone Books Work the file at <paramref name="sourceFilePath"/>
    /// should join, or <c>null</c> when the file is a new book.
    /// </summary>
    /// <remarks>
    /// A Work qualifies when it has no file of the same format and either
    /// (a) one of its files carries the same calibre UUID, or
    /// (b) one of its files sits in the same directory with the same normalised title and
    /// the same normalised primary author. Files in other directories are never matched by title.
    /// </remarks>
    Task<Guid?> FindSiblingWorkAsync(
        string sourceFilePath,
        string? title,
        string? author,
        string? calibreUuid,
        CancellationToken ct = default);
}
