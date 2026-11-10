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

    /// <summary>The picture widths the Engine will make for a page (0, the original, is always allowed).</summary>
    [JsonPropertyName("widths")]
    public IReadOnlyList<int> Widths { get; init; } = [];

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

/// <summary>Which reader opens a title: the book reader (<c>epub</c>), the page reader (<c>comic</c>, <c>pdf</c>) or none (<c>unsupported</c>).</summary>
public sealed class ReaderFormatDto
{
    [JsonPropertyName("format")]
    public string Format { get; init; } = "unsupported";

    /// <summary>The title shown in the reader's top line, when the catalogue has one.</summary>
    [JsonPropertyName("title")]
    public string? Title { get; init; }

    /// <summary>The line under the title: the author, or for a comic its Series and issue.</summary>
    [JsonPropertyName("subtitle")]
    public string? Subtitle { get; init; }

    /// <summary>The Series a comic belongs to, used to remember a reading direction for the whole Series.</summary>
    [JsonPropertyName("series")]
    public string? Series { get; init; }
}
