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

/// <summary>Asks the Engine to build a pack from some of the profile's own bookmarks on a title.</summary>
public sealed record BookmarkPackExportRequestDto
{
    public Guid? ProfileId { get; init; }
    public Guid AssetId { get; init; }
    public IReadOnlyList<Guid> BookmarkIds { get; init; } = [];
    /// <summary>The pack's name, shown on every bookmark it adds. Required.</summary>
    public string Name { get; init; } = string.Empty;
    /// <summary>Who made the pack, typed by the person exporting. Optional; never filled from the profile.</summary>
    public string? Author { get; init; }
    /// <summary>The running time of the copy being played, in seconds.</summary>
    public double? RunningTimeSeconds { get; init; }
    /// <summary>The chapters of the copy being played, so each bookmark can carry how far into its chapter it is.</summary>
    public IReadOnlyList<BookmarkPackChapterDto> Chapters { get; init; } = [];
}

/// <summary>A pack file ready to save. The content is the file's bytes, base64 encoded.</summary>
public sealed record BookmarkPackFileDto
{
    public string FileName { get; init; } = string.Empty;
    public string ContentBase64 { get; init; } = string.Empty;
    public int BookmarkCount { get; init; }
}

/// <summary>A pack file to check or import on a title.</summary>
public sealed record BookmarkPackImportRequestDto
{
    public Guid? ProfileId { get; init; }
    public Guid AssetId { get; init; }
    public string ContentBase64 { get; init; } = string.Empty;
    public double? RunningTimeSeconds { get; init; }
    /// <summary>The chapters of this copy, used when the pack is aligned by chapter.</summary>
    public IReadOnlyList<BookmarkPackChapterDto> Chapters { get; init; } = [];
    /// <summary>See <see cref="BookmarkPackImportModes"/>. Ignored by the check.</summary>
    public string Mode { get; init; } = BookmarkPackImportModes.AsIs;
}

/// <summary>What the Engine found in a pack file and whether it is for this title.</summary>
public sealed record BookmarkPackPreviewDto
{
    public string Name { get; init; } = string.Empty;
    public string? Author { get; init; }
    public int BookmarkCount { get; init; }
    /// <summary>See <see cref="BookmarkPackMatchResults"/>.</summary>
    public string Match { get; init; } = BookmarkPackMatchResults.WrongTitle;
    public string Reason { get; init; } = string.Empty;
    /// <summary>True when the pack is for a different cut and at least one bookmark names a chapter, so aligning is worth offering.</summary>
    public bool CanAlignByChapter { get; init; }
    /// <summary>True when this exact pack is already on the title.</summary>
    public bool AlreadyImported { get; init; }
}

public sealed record PackBookmarkDto
{
    public Guid Id { get; init; }
    public double PositionSeconds { get; init; }
    public double? EndSeconds { get; init; }
    public string? ChapterTitle { get; init; }
    public string? Note { get; init; }
    public string? Description { get; init; }
}

/// <summary>An imported pack and its read-only bookmarks, as the player shows them.</summary>
public sealed record BookmarkPackDto
{
    public Guid Id { get; init; }
    public Guid WorkId { get; init; }
    public string Name { get; init; } = string.Empty;
    public string? Author { get; init; }
    public bool IsEnabled { get; init; } = true;
    public bool IsAligned { get; init; }
    public DateTimeOffset ImportedAt { get; init; }
    public IReadOnlyList<PackBookmarkDto> Bookmarks { get; init; } = [];
}

public sealed record SetBookmarkPackEnabledRequestDto
{
    public Guid? ProfileId { get; init; }
    public bool IsEnabled { get; init; }
}
