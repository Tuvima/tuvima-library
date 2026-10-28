using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Models;
using Microsoft.Extensions.Logging;
using VersOne.Epub;

namespace MediaEngine.Processors;

/// <summary>
/// Reads and serves EPUB content using VersOne.Epub.
///
/// Maintains an LRU memory cache of up to <see cref="MaxCachedBooks"/> parsed EPUB books
/// to avoid re-reading the ZIP archive on every chapter/resource request.
/// Entries are evicted after <see cref="CacheEvictionMinutes"/> of inactivity.
/// </summary>
public sealed class EpubContentService : IEpubContentService, IDisposable
{
    private const int MaxCachedBooks = 5;
    private const int CacheEvictionMinutes = 10;

    private readonly ILogger<EpubContentService> _logger;
    private readonly ConcurrentDictionary<string, CachedBook> _cache = new();
    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private readonly Timer _evictionTimer;

    /// <summary>
    /// An EPUB book held in the LRU cache along with its last access time.
    /// </summary>
    private sealed class CachedBook
    {
        public required EpubBook Book { get; init; }
        public DateTime LastAccessed { get; set; } = DateTime.UtcNow;
    }

    public EpubContentService(ILogger<EpubContentService> logger)
    {
        _logger = logger;

        // Run eviction sweep every 2 minutes.
        _evictionTimer = new Timer(
            _ => EvictStaleEntries(),
            null,
            TimeSpan.FromMinutes(2),
            TimeSpan.FromMinutes(2));
    }

    // -------------------------------------------------------------------------
    // IEpubContentService
    // -------------------------------------------------------------------------

    /// <inheritdoc/>
    public async Task<EpubBookMetadata> GetBookMetadataAsync(string filePath, CancellationToken ct = default)
    {
        var book = await GetOrLoadBookAsync(filePath, ct).ConfigureAwait(false);
        var meta = book.Schema?.Package?.Metadata;

        var wordCount = CountWords(book);

        return new EpubBookMetadata(
            Title: book.Title ?? "Untitled",
            Author: book.Author ?? "Unknown",
            ChapterCount: book.ReadingOrder?.Count ?? 0,
            WordCount: wordCount,
            Language: meta?.Languages?.FirstOrDefault(),
            HasCoverImage: book.CoverImage is { Length: > 0 })
        {
            ChapterWordCounts = book.ReadingOrder?.Select(chapter => CountWordsInHtml(chapter.Content)).ToArray() ?? []
        };
    }

    /// <inheritdoc/>
    public async Task<List<EpubTocEntry>> GetTableOfContentsAsync(string filePath, CancellationToken ct = default)
    {
        var book = await GetOrLoadBookAsync(filePath, ct).ConfigureAwait(false);

        // EPUB3 navigation document
        if (book.Navigation is { Count: > 0 })
        {
            return BuildTocFromNavigation(book.Navigation, book.ReadingOrder);
        }

        // Fallback: flat list from reading order
        return BuildFlatTocFromReadingOrder(book.ReadingOrder);
    }

    /// <inheritdoc/>
    public async Task<List<EpubSearchHit>> SearchAsync(string filePath, string query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Length < 2)
        {
            return [];
        }

        var book = await GetOrLoadBookAsync(filePath, ct).ConfigureAwait(false);
        var results = new List<EpubSearchHit>();

        if (book.ReadingOrder is null)
        {
            return results;
        }

        for (var i = 0; i < book.ReadingOrder.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            var chapter = book.ReadingOrder[i];
            if (string.IsNullOrWhiteSpace(chapter.Content))
            {
                continue;
            }

            // Strip HTML to plain text for searching.
            var plainText = StripHtml(chapter.Content);
            var title = ExtractChapterTitle(chapter, i, book.Navigation);

            var searchIndex = 0;
            while (searchIndex < plainText.Length)
            {
                var matchIndex = plainText.IndexOf(query, searchIndex, StringComparison.OrdinalIgnoreCase);
                if (matchIndex < 0)
                {
                    break;
                }

                // Build context snippet: ~25 chars before + match + ~25 chars after.
                var snippetStart = Math.Max(0, matchIndex - 25);
                var snippetEnd = Math.Min(plainText.Length, matchIndex + query.Length + 25);
                var snippet = plainText[snippetStart..snippetEnd].Trim();

                if (snippetStart > 0)
                {
                    snippet = "..." + snippet;
                }

                if (snippetEnd < plainText.Length)
                {
                    snippet += "...";
                }

                results.Add(new EpubSearchHit(
                    ChapterIndex: i,
                    ChapterTitle: title,
                    ContextSnippet: snippet,
                    MatchOffset: matchIndex));

                searchIndex = matchIndex + query.Length;
            }
        }

        return results;
    }

    // -------------------------------------------------------------------------
    // LRU cache
    // -------------------------------------------------------------------------

    private async Task<EpubBook> GetOrLoadBookAsync(string filePath, CancellationToken ct)
    {
        // Fast path: already cached.
        if (_cache.TryGetValue(filePath, out var cached))
        {
            cached.LastAccessed = DateTime.UtcNow;
            return cached.Book;
        }

        // Slow path: load from disk under lock.
        await _loadLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Double-check after acquiring lock.
            if (_cache.TryGetValue(filePath, out cached))
            {
                cached.LastAccessed = DateTime.UtcNow;
                return cached.Book;
            }

            _logger.LogInformation("Loading EPUB into cache: {FilePath}", filePath);
            var book = await EpubReader.ReadBookAsync(filePath).ConfigureAwait(false);

            // Evict oldest if at capacity.
            while (_cache.Count >= MaxCachedBooks)
            {
                var oldest = _cache
                    .OrderBy(kvp => kvp.Value.LastAccessed)
                    .First();
                _cache.TryRemove(oldest.Key, out _);
                _logger.LogDebug("Evicted EPUB from cache: {FilePath}", oldest.Key);
            }

            var entry = new CachedBook { Book = book };
            _cache.TryAdd(filePath, entry);

            return book;
        }
        finally
        {
            _loadLock.Release();
        }
    }

    private void EvictStaleEntries()
    {
        var cutoff = DateTime.UtcNow.AddMinutes(-CacheEvictionMinutes);
        foreach (var kvp in _cache)
        {
            if (kvp.Value.LastAccessed < cutoff)
            {
                _cache.TryRemove(kvp.Key, out _);
                _logger.LogDebug("Time-evicted EPUB from cache: {FilePath}", kvp.Key);
            }
        }
    }

    // -------------------------------------------------------------------------
    // TOC builders
    // -------------------------------------------------------------------------

    private static List<EpubTocEntry> BuildTocFromNavigation(
        List<EpubNavigationItem> navItems,
        List<EpubTextContentFile>? readingOrder)
    {
        var result = new List<EpubTocEntry>();

        foreach (var item in navItems)
        {
            if (item.Type == EpubNavigationItemType.HEADER)
            {
                // Header items may group nested links.
                var entry = new EpubTocEntry
                {
                    Title = item.Title ?? "Section",
                    ChapterIndex = FindChapterIndex(item, readingOrder),
                    FragmentId = item.Link?.Anchor,
                    Children = item.NestedItems is { Count: > 0 }
                        ? BuildTocFromNavigation(item.NestedItems, readingOrder)
                        : []
                };
                result.Add(entry);
            }
            else // LINK
            {
                var entry = new EpubTocEntry
                {
                    Title = item.Title ?? "Chapter",
                    ChapterIndex = FindChapterIndex(item, readingOrder),
                    FragmentId = item.Link?.Anchor,
                    Children = item.NestedItems is { Count: > 0 }
                        ? BuildTocFromNavigation(item.NestedItems, readingOrder)
                        : []
                };
                result.Add(entry);
            }
        }

        return result;
    }

    private static int FindChapterIndex(EpubNavigationItem navItem, List<EpubTextContentFile>? readingOrder)
    {
        if (readingOrder is null)
        {
            return 0;
        }

        // Try to match by HtmlContentFile reference.
        if (navItem.HtmlContentFile is not null)
        {
            for (var i = 0; i < readingOrder.Count; i++)
            {
                if (readingOrder[i].FileName == navItem.HtmlContentFile.FileName)
                {
                    return i;
                }
            }
        }

        // Try to match by Link.ContentFileName.
        if (navItem.Link?.ContentFileName is not null)
        {
            for (var i = 0; i < readingOrder.Count; i++)
            {
                if (readingOrder[i].FileName == navItem.Link.ContentFileName ||
                    readingOrder[i].FileName.EndsWith(navItem.Link.ContentFileName, StringComparison.OrdinalIgnoreCase) ||
                    navItem.Link.ContentFileName.EndsWith(readingOrder[i].FileName, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }
        }

        return 0;
    }

    private static List<EpubTocEntry> BuildFlatTocFromReadingOrder(List<EpubTextContentFile>? readingOrder)
    {
        if (readingOrder is null)
        {
            return [];
        }

        return readingOrder.Select((chapter, index) => new EpubTocEntry
        {
            Title = ExtractTitleFromHtml(chapter.Content) ?? $"Chapter {index + 1}",
            ChapterIndex = index,
        }).ToList();
    }

    // -------------------------------------------------------------------------
    // Text helpers
    // -------------------------------------------------------------------------

    private static long CountWords(EpubBook book)
    {
        if (book.ReadingOrder is not { Count: > 0 })
        {
            return 0;
        }

        long total = 0;
        foreach (var chapter in book.ReadingOrder)
        {
            total += CountWordsInHtml(chapter.Content);
        }
        return total;
    }

    private static int CountWordsInHtml(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return 0;
        }

        var text = StripHtml(html);
        return text.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Length;
    }

    private static string StripHtml(string html)
    {
        return Regex.Replace(html, "<[^>]+>", " ");
    }

    private static string? ExtractTitleFromHtml(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return null;
        }

        // Try <title> tag first.
        var titleMatch = Regex.Match(html, @"<title[^>]*>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (titleMatch.Success)
        {
            var title = StripHtml(titleMatch.Groups[1].Value).Trim();
            if (!string.IsNullOrWhiteSpace(title))
            {
                return title;
            }
        }

        // Try first <h1> or <h2>.
        var headingMatch = Regex.Match(html, @"<h[12][^>]*>(.*?)</h[12]>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (headingMatch.Success)
        {
            var heading = StripHtml(headingMatch.Groups[1].Value).Trim();
            if (!string.IsNullOrWhiteSpace(heading))
            {
                return heading;
            }
        }

        return null;
    }

    private static string ExtractChapterTitle(
        EpubTextContentFile chapter,
        int chapterIndex,
        List<EpubNavigationItem>? navigation)
    {
        // Try to find the navigation item that matches this chapter.
        if (navigation is { Count: > 0 })
        {
            var navTitle = FindNavTitleForChapter(navigation, chapter.FileName);
            if (navTitle is not null)
            {
                return navTitle;
            }
        }

        // Fall back to extracting from HTML.
        return ExtractTitleFromHtml(chapter.Content) ?? $"Chapter {chapterIndex + 1}";
    }

    private static string? FindNavTitleForChapter(List<EpubNavigationItem> navItems, string chapterFileName)
    {
        foreach (var item in navItems)
        {
            if (item.Link?.ContentFileName is not null &&
                (item.Link.ContentFileName == chapterFileName ||
                 chapterFileName.EndsWith(item.Link.ContentFileName, StringComparison.OrdinalIgnoreCase) ||
                 item.Link.ContentFileName.EndsWith(chapterFileName, StringComparison.OrdinalIgnoreCase)))
            {
                return item.Title;
            }

            if (item.HtmlContentFile?.FileName == chapterFileName)
            {
                return item.Title;
            }

            if (item.NestedItems is { Count: > 0 })
            {
                var nested = FindNavTitleForChapter(item.NestedItems, chapterFileName);
                if (nested is not null)
                {
                    return nested;
                }
            }
        }

        return null;
    }

    // -------------------------------------------------------------------------
    // IDisposable
    // -------------------------------------------------------------------------

    public void Dispose()
    {
        _evictionTimer.Dispose();
        _loadLock.Dispose();
    }
}
