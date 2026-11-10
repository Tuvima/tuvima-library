using System.Text.Json.Serialization;

namespace MediaEngine.Contracts.Reading;

/// <summary>A comic's pages in reading order (PDF and comic reader spec D2, D4).</summary>
public sealed class ComicPageListDto
{
    [JsonPropertyName("page_count")]
    public int PageCount { get; init; }

    /// <summary>True when the comic's own information says it reads right to left (manga).</summary>
    [JsonPropertyName("right_to_left")]
    public bool RightToLeft { get; init; }

    [JsonPropertyName("pages")]
    public IReadOnlyList<ComicPageDto> Pages { get; init; } = [];
}

/// <summary>One page of a comic. <see cref="Number"/> starts at 1.</summary>
public sealed class ComicPageDto
{
    [JsonPropertyName("number")]
    public int Number { get; init; }

    [JsonPropertyName("width")]
    public int Width { get; init; }

    [JsonPropertyName("height")]
    public int Height { get; init; }
}
