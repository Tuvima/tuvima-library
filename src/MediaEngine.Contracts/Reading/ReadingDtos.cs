using MediaEngine.Domain.Enums;

namespace MediaEngine.Contracts.Reading;

/// <summary>EPUB book metadata returned by the reading API.</summary>
public sealed record EpubBookMetadataDto(
    string Title,
    string Author,
    int ChapterCount,
    long WordCount,
    string? Language,
    bool HasCoverImage)
{
    public IReadOnlyList<int> ChapterWordCounts { get; init; } = [];
}

/// <summary>A recursive EPUB table-of-contents entry.</summary>
public sealed class EpubTocEntryDto
{
    public required string Title { get; init; }
    public required int ChapterIndex { get; init; }
    public string? FragmentId { get; init; }
    public List<EpubTocEntryDto> Children { get; init; } = [];
}

/// <summary>Rendered HTML and metadata for one EPUB chapter.</summary>
public sealed record EpubChapterContentDto(
    int Index,
    string Title,
    string HtmlContent,
    int WordCount);

/// <summary>A full-text search match inside an EPUB.</summary>
public sealed record EpubSearchHitDto(
    int ChapterIndex,
    string ChapterTitle,
    string ContextSnippet,
    int MatchOffset);

/// <summary>A bookmark stored for an EPUB asset.</summary>
public sealed class ReaderBookmarkDto
{
    public Guid Id { get; init; }
    public string UserId { get; init; } = string.Empty;
    public Guid AssetId { get; init; }
    public int ChapterIndex { get; init; }
    public string? CfiPosition { get; init; }
    public string? Label { get; init; }
    public DateTime CreatedAt { get; init; }
}

/// <summary>A text highlight stored for an EPUB asset.</summary>
public sealed class ReaderHighlightDto
{
    public Guid Id { get; init; }
    public string UserId { get; init; } = string.Empty;
    public Guid AssetId { get; init; }
    public int ChapterIndex { get; init; }
    public int StartOffset { get; init; }
    public int EndOffset { get; init; }
    public string SelectedText { get; init; } = string.Empty;
    public string Color { get; init; } = "#EAB308";
    public string? NoteText { get; init; }
    public DateTime CreatedAt { get; init; }
}

/// <summary>Aggregated reading statistics for an EPUB asset.</summary>
public sealed class ReaderStatisticsDto
{
    public Guid Id { get; init; }
    public string UserId { get; init; } = string.Empty;
    public Guid AssetId { get; init; }
    public int ChaptersRead { get; init; }
    public long TotalReadingTimeSecs { get; init; }
    public long WordsRead { get; init; }
    public int SessionsCount { get; init; }
    public double AvgWordsPerMinute { get; init; }
    public DateTime? LastSessionAt { get; init; }
}

/// <summary>Request body for creating a reader bookmark.</summary>
public sealed record CreateReaderBookmarkRequestDto(
    int ChapterIndex,
    string? CfiPosition,
    string? Label);

/// <summary>Request body for creating a reader highlight.</summary>
public sealed record CreateReaderHighlightRequestDto(
    int ChapterIndex,
    int StartOffset,
    int EndOffset,
    string SelectedText,
    string? Color,
    string? NoteText);

/// <summary>Request body for updating a reader highlight.</summary>
public sealed record UpdateReaderHighlightRequestDto(
    string? Color,
    string? NoteText);

/// <summary>Request body for updating reading statistics.</summary>
public sealed record UpdateReaderStatisticsRequestDto(
    int ChaptersRead,
    long TotalReadingTimeSecs,
    long WordsRead,
    int SessionsCount,
    double AvgWordsPerMinute);

/// <summary>Request body for creating an ebook-to-audiobook alignment job.</summary>
public sealed record CreateAlignmentRequestDto(Guid AudiobookAssetId);

/// <summary>Wire representation of an ebook-to-audiobook alignment job.</summary>
public sealed class AlignmentJobDto
{
    public Guid Id { get; set; }
    public Guid EbookAssetId { get; set; }
    public Guid AudiobookAssetId { get; set; }
    public AlignmentJobStatus Status { get; set; } = AlignmentJobStatus.Pending;
    public string? AlignmentData { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
}
