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
    public async Task<EpubChapterContent?> GetChapterContentAsync(
        string filePath,
        int chapterIndex,
        string resourceBaseUrl,
        CancellationToken ct = default)
    {
        var book = await GetOrLoadBookAsync(filePath, ct).ConfigureAwait(false);
        if (book.ReadingOrder is null || chapterIndex < 0 || chapterIndex >= book.ReadingOrder.Count)
        {
            return null;
        }

        var chapter = book.ReadingOrder[chapterIndex];
        var html = RewriteResourceUrls(chapter.Content, chapter.FileName, resourceBaseUrl);
        var title = ExtractChapterTitle(chapter, chapterIndex, book.Navigation);

        var wordCount = CountWordsInHtml(chapter.Content);

        return new EpubChapterContent(
            Index: chapterIndex,
            Title: title,
            HtmlContent: html,
            WordCount: wordCount);
    }

    /// <inheritdoc/>
    public async Task<EpubResourceResult?> GetResourceAsync(string filePath, string resourcePath, CancellationToken ct = default)
    {
        var book = await GetOrLoadBookAsync(filePath, ct).ConfigureAwait(false);

        // Try byte content files first (images, fonts)
        if (book.Content.Images is not null)
        {
            foreach (var kvp in book.Content.Images)
            {
                if (PathMatchesResource(kvp.Key, resourcePath))
                {
                    return new EpubResourceResult(
                        kvp.Value.Content,
                        kvp.Value.ContentMimeType ?? SniffMimeType(kvp.Value.Content),
                        Path.GetFileName(kvp.Key));
                }
            }
        }

        if (book.Content.Fonts is not null)
        {
            foreach (var kvp in book.Content.Fonts)
            {
                if (PathMatchesResource(kvp.Key, resourcePath))
                {
                    return new EpubResourceResult(
                        kvp.Value.Content,
                        kvp.Value.ContentMimeType ?? "application/octet-stream",
                        Path.GetFileName(kvp.Key));
                }
            }
        }

        // Try text content files (CSS)
        if (book.Content.Css is not null)
        {
            foreach (var kvp in book.Content.Css)
            {
                if (PathMatchesResource(kvp.Key, resourcePath))
                {
                    var bytes = System.Text.Encoding.UTF8.GetBytes(kvp.Value.Content);
                    return new EpubResourceResult(
                        bytes,
                        kvp.Value.ContentMimeType ?? "text/css",
                        Path.GetFileName(kvp.Key));
                }
            }
        }

        // Fallback: check AllFiles dictionary
        if (book.Content.AllFiles is not null)
        {
            foreach (var kvp in book.Content.AllFiles)
            {
                if (PathMatchesResource(kvp.Key, resourcePath))
                {
                    if (kvp.Value is EpubByteContentFile byteFile)
                    {
                        return new EpubResourceResult(
                            byteFile.Content,
                            byteFile.ContentMimeType ?? "application/octet-stream",
                            Path.GetFileName(kvp.Key));
                    }
                    if (kvp.Value is EpubTextContentFile textFile)
                    {
                        var bytes = System.Text.Encoding.UTF8.GetBytes(textFile.Content);
                        return new EpubResourceResult(
                            bytes,
                            textFile.ContentMimeType ?? "text/plain",
                            Path.GetFileName(kvp.Key));
                    }
                }
            }
        }

        _logger.LogWarning("EPUB resource not found: {ResourcePath} in {FilePath}", resourcePath, filePath);
        return null;
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
    // URL rewriting
    // -------------------------------------------------------------------------

    /// <summary>
    /// Rewrites relative resource URLs in EPUB chapter HTML to point at the Engine's
    /// resource endpoint. For example, <c>src="images/fig1.png"</c> becomes
    /// <c>src="/read/{assetId}/resource/OEBPS/images/fig1.png"</c>.
    /// </summary>
    private static string RewriteResourceUrls(string html, string chapterFileName, string resourceBaseUrl)
    {
        if (string.IsNullOrEmpty(html))
        {
            return html;
        }

        // Ensure base URL ends with /
        if (!resourceBaseUrl.EndsWith('/'))
        {
            resourceBaseUrl += "/";
        }

        // Determine the directory of the current chapter file for resolving relative paths.
        var chapterDir = Path.GetDirectoryName(chapterFileName)?.Replace('\\', '/') ?? "";
        if (chapterDir.Length > 0 && !chapterDir.EndsWith('/'))
        {
            chapterDir += "/";
        }

        // Rewrite src="..." and href="..." attributes that reference relative paths.
        // Exclude anchors (#), absolute URLs (http/https/data), and mailto links.
        return Regex.Replace(html,
            @"((?:src|href|xlink:href)\s*=\s*"")([^""#][^""]*?)("")",
            match =>
            {
                var prefix = match.Groups[1].Value;
                var path = match.Groups[2].Value;
                var suffix = match.Groups[3].Value;

                // Skip absolute URLs, data URIs, and fragment-only references.
                if (path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                    path.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                    path.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ||
                    path.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
                {
                    return match.Value;
                }

                // Resolve relative path against the chapter's directory.
                var resolvedPath = path.StartsWith("../")
                    ? ResolveRelativePath(chapterDir, path)
                    : chapterDir + path;

                return prefix + resourceBaseUrl + resolvedPath + suffix;
            },
            RegexOptions.IgnoreCase);
    }

    /// <summary>
    /// Resolves a relative path with <c>../</c> segments against a base directory.
    /// </summary>
    private static string ResolveRelativePath(string baseDir, string relativePath)
    {
        var segments = baseDir.TrimEnd('/').Split('/').ToList();
        var parts = relativePath.Split('/');

        foreach (var part in parts)
        {
            if (part == "..")
            {
                if (segments.Count > 0)
                {
                    segments.RemoveAt(segments.Count - 1);
                }
            }
            else if (part != ".")
            {
                segments.Add(part);
            }
        }

        return string.Join("/", segments);
    }

    // -------------------------------------------------------------------------
    // Resource path matching
    // -------------------------------------------------------------------------

    /// <summary>
    /// Checks whether an EPUB internal file key matches a requested resource path.
    /// Handles cases where the EPUB key includes a directory prefix (e.g. "OEBPS/images/fig1.png")
    /// and the request may or may not include it.
    /// </summary>
    private static bool PathMatchesResource(string epubKey, string requestedPath)
    {
        // Normalize separators.
        var normalizedKey = epubKey.Replace('\\', '/').TrimStart('/');
        var normalizedReq = requestedPath.Replace('\\', '/').TrimStart('/');

        return string.Equals(normalizedKey, normalizedReq, StringComparison.OrdinalIgnoreCase) ||
               normalizedKey.EndsWith("/" + normalizedReq, StringComparison.OrdinalIgnoreCase) ||
               normalizedReq.EndsWith("/" + normalizedKey, StringComparison.OrdinalIgnoreCase);
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
    // MIME sniffing (reused from EpubProcessor pattern)
    // -------------------------------------------------------------------------

    private static string SniffMimeType(byte[] data)
    {
        if (data.Length < 4)
        {
            return "application/octet-stream";
        }

        if (data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF)
        {
            return "image/jpeg";
        }

        if (data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47)
        {
            return "image/png";
        }

        if (data[0] == 0x47 && data[1] == 0x49 && data[2] == 0x46 && data[3] == 0x38)
        {
            return "image/gif";
        }

        if (data.Length >= 12 &&
            data[0] == 0x52 && data[1] == 0x49 && data[2] == 0x46 && data[3] == 0x46 &&
            data[8] == 0x57 && data[9] == 0x45 && data[10] == 0x42 && data[11] == 0x50)
        {
            return "image/webp";
        }

        return "application/octet-stream";
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
