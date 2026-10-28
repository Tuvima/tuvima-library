namespace MediaEngine.Domain.Models;

/// <summary>
/// Metadata extracted from an EPUB book — title, author, chapter count, word count, language.
/// </summary>
public sealed record EpubBookMetadata(
    string Title,
    string Author,
    int ChapterCount,
    long WordCount,
    string? Language,
    bool HasCoverImage)
{
    public IReadOnlyList<int> ChapterWordCounts { get; init; } = [];
}

/// <summary>
/// A single entry in the EPUB Table of Contents tree.
/// </summary>
public sealed class EpubTocEntry
{
    /// <summary>Display label for this TOC entry.</summary>
    public required string Title { get; init; }

    /// <summary>Zero-based index into the reading order that this entry points to.</summary>
    public required int ChapterIndex { get; init; }

    /// <summary>Optional fragment identifier within the chapter (e.g. "#section-2").</summary>
    public string? FragmentId { get; init; }

    /// <summary>Nested child entries for hierarchical TOCs.</summary>
    public List<EpubTocEntry> Children { get; init; } = [];
}

/// <summary>
/// A single search hit within an EPUB book.
/// </summary>
public sealed record EpubSearchHit(
    int ChapterIndex,
    string ChapterTitle,
    string ContextSnippet,
    int MatchOffset);
