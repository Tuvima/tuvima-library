namespace MediaEngine.Contracts.Playback;

/// <summary>Limits shared by the Engine and the Dashboard for bookmark packs (Videos spec D46).</summary>
public static class BookmarkPackLimits
{
    public const int MaximumBookmarks = 500;
    public const int MaximumNameLength = 100;
    public const int MaximumAuthorLength = 100;
    public const int MaximumFileBytes = 1024 * 1024;
}

/// <summary>How a pack relates to the title it is being imported on.</summary>
public static class BookmarkPackMatchResults
{
    public const string Match = "Match";
    public const string DifferentCut = "DifferentCut";
    public const string WrongTitle = "WrongTitle";
}

/// <summary>How an import places a pack whose cut differs from this copy.</summary>
public static class BookmarkPackImportModes
{
    /// <summary>Use the pack's times as they are (the pack is for this cut).</summary>
    public const string AsIs = "AsIs";

    /// <summary>Move each bookmark to the chapter of the same name; bookmarks without a matching chapter are skipped.</summary>
    public const string AlignByChapter = "AlignByChapter";
}

public sealed record BookmarkPackChapterDto
{
    public string Title { get; init; } = string.Empty;
    public double StartSeconds { get; init; }
}

/// <summary>A pack file ready to save. The content is the file's bytes, base64 encoded.</summary>
public sealed record BookmarkPackFileDto
{
    public string FileName { get; init; } = string.Empty;
    public string ContentBase64 { get; init; } = string.Empty;
    public int BookmarkCount { get; init; }
}
