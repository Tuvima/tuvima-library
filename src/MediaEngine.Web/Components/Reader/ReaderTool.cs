namespace MediaEngine.Web.Components.Reader;

/// <summary>
/// The tools on a reader's top line, in the one fixed order every reader shows them (spec music-table-and-reader-frame D13).
/// A reader lists the ones it has; the frame draws them in this order whatever order they were listed in.
/// </summary>
public enum ReaderTool
{
    Search = 0,
    Contents = 1,
    Pages = 2,
    BookmarkThisPage = 3,
    Bookmarks = 4,
    ReadingSettings = 5,
}

/// <summary>What the frame is showing: the opening spinner, the page itself, or "can't open".</summary>
public enum ReaderFrameState
{
    Loading = 0,
    Ready = 1,
    Error = 2,
}

/// <summary>The tool sets a reader hands the frame.</summary>
public static class ReaderTools
{
    /// <summary>Every tool, as a PDF has them.</summary>
    public static readonly IReadOnlyCollection<ReaderTool> All =
        [ReaderTool.Search, ReaderTool.Contents, ReaderTool.Pages, ReaderTool.BookmarkThisPage, ReaderTool.Bookmarks, ReaderTool.ReadingSettings];

    /// <summary>A book: no Pages grid.</summary>
    public static readonly IReadOnlyCollection<ReaderTool> Book =
        [ReaderTool.Search, ReaderTool.Contents, ReaderTool.BookmarkThisPage, ReaderTool.Bookmarks, ReaderTool.ReadingSettings];

    /// <summary>A comic: no Search or Contents.</summary>
    public static readonly IReadOnlyCollection<ReaderTool> Comic =
        [ReaderTool.Pages, ReaderTool.BookmarkThisPage, ReaderTool.Bookmarks, ReaderTool.ReadingSettings];
}
