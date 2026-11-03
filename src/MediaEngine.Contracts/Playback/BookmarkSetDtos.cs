namespace MediaEngine.Contracts.Playback;

/// <summary>
/// Find bookmarks (Global bookmarks spec D6, D7): a set of bookmarks from outside (today a bookmark file) is compared with the bookmarks
/// the person already has, row by row, and applied in one go. The Engine decides how close two bookmarks are; the Dashboard only shows
/// the label and the gap it is sent.
/// </summary>
public static class BookmarkClosenessLabels
{
    public const string SameSpot = "SameSpot";
    public const string Close = "Close";
    public const string Far = "Far";
    public const string New = "New";
}

/// <summary>Which of the two cards on a compare row is kept when the set is applied.</summary>
public static class BookmarkRowChoices
{
    /// <summary>Keep the bookmark from the set; it replaces the nearest one the person has (or is simply added on a New row).</summary>
    public const string Incoming = "Incoming";

    /// <summary>Keep the bookmark the person already has; the incoming one is dropped.</summary>
    public const string Yours = "Yours";

    /// <summary>A New row the person ticked off: it is not added.</summary>
    public const string Skip = "Skip";
}

/// <summary>A bookmark set (a bookmark file's bytes) to compare with, or apply to, one file of a title.</summary>
public sealed record BookmarkSetRequestDto
{
    public Guid? ProfileId { get; init; }
    public Guid AssetId { get; init; }
    public string ContentBase64 { get; init; } = string.Empty;
    public double? RunningTimeSeconds { get; init; }
    /// <summary>The chapters of this copy, used when the set is aligned by chapter.</summary>
    public IReadOnlyList<BookmarkPackChapterDto> Chapters { get; init; } = [];
    /// <summary>See <see cref="BookmarkPackImportModes"/>. A set for a different cut is only placed when this is AlignByChapter.</summary>
    public string Mode { get; init; } = BookmarkPackImportModes.AsIs;
}

/// <summary>One bookmark as the set carries it, placed on this copy.</summary>
public sealed record BookmarkSetItemDto
{
    public double PositionSeconds { get; init; }
    public double? EndSeconds { get; init; }
    public string? ChapterTitle { get; init; }
    public string? Note { get; init; }
    public string? Description { get; init; }
}

/// <summary>The nearest bookmark the person already has (their own or a Global one).</summary>
public sealed record BookmarkSetExistingDto
{
    public Guid Id { get; init; }
    public double PositionSeconds { get; init; }
    public string? Name { get; init; }
    public string? Note { get; init; }
    public bool IsGlobal { get; init; }
}

public sealed record BookmarkSetCompareRowDto
{
    /// <summary>Where the row sits in the set, so the choice for it can be sent back.</summary>
    public int Index { get; init; }
    public BookmarkSetItemDto Incoming { get; init; } = new();
    /// <summary>Null on a New row: nothing nearby.</summary>
    public BookmarkSetExistingDto? Yours { get; init; }
    /// <summary>How far apart the two are, in seconds. Null on a New row.</summary>
    public double? GapSeconds { get; init; }
    /// <summary>See <see cref="BookmarkClosenessLabels"/>.</summary>
    public string Closeness { get; init; } = BookmarkClosenessLabels.New;
    /// <summary>The choice that is ticked when the person just presses Apply. See <see cref="BookmarkRowChoices"/>.</summary>
    public string DefaultChoice { get; init; } = BookmarkRowChoices.Incoming;
}

/// <summary>What the Engine found in a bookmark set, whether it is for this title, and each row beside the nearest bookmark the person has.</summary>
public sealed record BookmarkSetCompareDto
{
    public string Name { get; init; } = string.Empty;
    public string? Author { get; init; }
    public int BookmarkCount { get; init; }
    /// <summary>See <see cref="BookmarkPackMatchResults"/>.</summary>
    public string Match { get; init; } = BookmarkPackMatchResults.WrongTitle;
    public string Reason { get; init; } = string.Empty;
    /// <summary>True when the set is for a different cut and at least one bookmark names a chapter, so aligning is worth offering.</summary>
    public bool CanAlignByChapter { get; init; }
    /// <summary>True when the rows are placed on this copy. False for a different cut until the person chooses to align it.</summary>
    public bool RowsReady { get; init; }
    /// <summary>Bookmarks in the set that could not be placed on this copy (past its end, or no chapter of that name).</summary>
    public int SkippedCount { get; init; }
    /// <summary>True when the person applying is an administrator: Add and Replace then change Global, and the whole set can be replaced.</summary>
    public bool AppliesToGlobal { get; init; }
    public IReadOnlyList<BookmarkSetCompareRowDto> Rows { get; init; } = [];
    /// <summary>How many Global bookmarks the title has now (shown beside "Replace the whole Global set").</summary>
    public int GlobalCount { get; init; }
}

public sealed record BookmarkSetRowChoiceDto
{
    public int Index { get; init; }
    /// <summary>See <see cref="BookmarkRowChoices"/>.</summary>
    public string Choice { get; init; } = BookmarkRowChoices.Incoming;
}

public sealed record BookmarkSetApplyRequestDto
{
    public BookmarkSetRequestDto Set { get; init; } = new();
    /// <summary>The person's choice per row. A row with no entry takes its default.</summary>
    public IReadOnlyList<BookmarkSetRowChoiceDto> Choices { get; init; } = [];
    /// <summary>Administrators only: delete every existing Global bookmark on the title and add the whole set instead.</summary>
    public bool ReplaceGlobalSet { get; init; }
}

public sealed record BookmarkSetApplyResultDto
{
    public int Added { get; init; }
    public int Replaced { get; init; }
    public int Kept { get; init; }
    public int Skipped { get; init; }
    /// <summary>True when the added bookmarks are Global (an administrator applied them).</summary>
    public bool AddedToGlobal { get; init; }
}

/// <summary>Asks the Engine to save this title's Global bookmarks as a file (administrators only).</summary>
public sealed record ExportGlobalBookmarksRequestDto
{
    public Guid? ProfileId { get; init; }
    public Guid AssetId { get; init; }
    /// <summary>The running time of the copy being played, in seconds.</summary>
    public double? RunningTimeSeconds { get; init; }
    /// <summary>The chapters of the copy being played, so each bookmark can carry how far into its chapter it is.</summary>
    public IReadOnlyList<BookmarkPackChapterDto> Chapters { get; init; } = [];
}
