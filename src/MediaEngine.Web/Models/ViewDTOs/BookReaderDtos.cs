namespace MediaEngine.Web.Models.ViewDTOs;

/// <summary>
/// What the book reader script (<c>wwwroot/js/book-reader.js</c>) answers when a book is opened. The script never
/// throws across the interop boundary: a failure comes back as <see cref="Ok"/> = false with a short, readable
/// <see cref="Message"/> and a <see cref="Code"/> the page can act on (unauthorized, notfound, busy, offline,
/// unavailable, unsupported, corrupt, cancelled).
/// </summary>
public sealed record BookReaderOpenResult
{
    public bool Ok { get; init; }
    public string? Code { get; init; }
    public string? Message { get; init; }
    public string? Title { get; init; }
    public string? Author { get; init; }
    public string? Dir { get; init; }
    public string? Layout { get; init; }
    public double Fraction { get; init; }
    public bool Restored { get; init; }
}

/// <summary>Options the page hands the reader script when opening a book. Property names are read by the script.</summary>
public sealed record BookReaderOpenOptions
{
    public string? LastLocation { get; init; }
    public string Theme { get; init; } = "light";
    public int FontSize { get; init; } = 18;
    public double LineHeight { get; init; } = 1.6;

    /// <summary>"book" (the book's own typeface), "serif" or "sans".</summary>
    public string Font { get; init; } = "book";

    /// <summary>"narrow", "standard" or "wide": how wide a page of text may grow on a big screen.</summary>
    public string PageWidth { get; init; } = "standard";

    /// <summary>"paginated" (turn pages) or "scrolled" (one long scroll).</summary>
    public string Flow { get; init; } = "paginated";
}

/// <summary>One line of the book's contents, flattened; <see cref="Level"/> is how deeply it is nested.</summary>
public sealed record BookReaderTocItem
{
    public string Label { get; init; } = string.Empty;
    public string Href { get; init; } = string.Empty;
    public int Level { get; init; }
}

/// <summary>A saved highlight as the reader script draws it: its id, where it is (an EPUB CFI range) and its colour.</summary>
public sealed record BookReaderHighlightMark(string Id, string Cfi, string Color);

/// <summary>The latest reading position the script holds, asked for right before the page is left.</summary>
public sealed record BookReaderLocation
{
    public string? Cfi { get; init; }
    public double Fraction { get; init; }
    public string? Label { get; init; }
}
