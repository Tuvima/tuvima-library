using MediaEngine.Domain.Models;

namespace MediaEngine.Domain.Contracts;

/// <summary>
/// Reads EPUB content — metadata, TOC and full-text search. (The book file itself is read in the browser.)
/// Uses an LRU memory cache to avoid re-parsing the same book on every request.
/// </summary>
public interface IEpubContentService
{
    /// <summary>
    /// Extracts high-level metadata from the EPUB at <paramref name="filePath"/>.
    /// </summary>
    Task<EpubBookMetadata> GetBookMetadataAsync(string filePath, CancellationToken ct = default);

    /// <summary>
    /// Builds a hierarchical Table of Contents from the EPUB navigation document.
    /// Falls back to a flat list based on reading order if no navigation is present.
    /// </summary>
    Task<List<EpubTocEntry>> GetTableOfContentsAsync(string filePath, CancellationToken ct = default);

    /// <summary>
    /// Searches all chapters for the given query string (case-insensitive substring match).
    /// Returns context snippets of approximately 50 characters around each match.
    /// </summary>
    Task<List<EpubSearchHit>> SearchAsync(string filePath, string query, CancellationToken ct = default);
}
