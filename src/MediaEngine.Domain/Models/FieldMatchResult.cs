using System.Text.Json.Serialization;

namespace MediaEngine.Domain.Models;

/// <summary>Per-field match scores comparing local file metadata against a search candidate.</summary>
public sealed class FieldMatchResult
{
    public IReadOnlyList<RetailFieldScore>? FieldScores { get; init; }

    [JsonPropertyName("title_score")]
    public double TitleScore { get; init; }

    [JsonPropertyName("author_score")]
    public double AuthorScore { get; init; }

    [JsonPropertyName("year_score")]
    public double YearScore { get; init; }

    [JsonPropertyName("format_score")]
    public double FormatScore { get; init; }

    [JsonPropertyName("composite_score")]
    public double CompositeScore { get; init; }

    [JsonPropertyName("title_verdict")]
    public FieldMatchVerdict TitleVerdict { get; init; }

    [JsonPropertyName("author_verdict")]
    public FieldMatchVerdict AuthorVerdict { get; init; }

    [JsonPropertyName("year_verdict")]
    public FieldMatchVerdict YearVerdict { get; init; }

    [JsonPropertyName("format_verdict")]
    public FieldMatchVerdict FormatVerdict { get; init; }

    [JsonPropertyName("cover_score")]
    public double CoverScore { get; set; }

    [JsonPropertyName("cover_verdict")]
    public FieldMatchVerdict CoverVerdict { get; set; }
}

/// <summary>How well a field matched.</summary>
public enum FieldMatchVerdict
{
    /// <summary>Score >= 0.95 — essentially identical.</summary>
    Exact,
    /// <summary>Score >= 0.70 — recognizably similar.</summary>
    Close,
    /// <summary>Score >= 0.0 — different values.</summary>
    Mismatch,
    /// <summary>Score = -1 — field not available on one or both sides.</summary>
    NotAvailable
}

/// <summary>Local file metadata for fuzzy comparison.</summary>
public sealed record LocalMetadata(string Title, string? Author, string? Year, string? MediaType);

/// <summary>Candidate metadata for fuzzy comparison.</summary>
public sealed record CandidateMetadata(string Title, string? Author, string? Year, string? MediaType)
{
    /// <summary>Perceptual hash similarity of cover art (0.0–1.0), or -1.0 if not available.</summary>
    public double CoverSimilarity { get; init; } = -1.0;
}
