namespace MediaEngine.Contracts.Playback;

// The wire shapes of the editor's Bookmarks tab (universal editor v5 D37, WP15): an administrator makes the definitive set of Everyone's
// bookmarks for a title, staged like every editor change and written on Save changes. The player only reads the result.

/// <summary>Whether a title's Everyone's list is pooled from every profile's bookmarks or an administrator's definitive set.</summary>
public static class EditorBookmarkStates
{
    /// <summary>Nobody has curated the title: Everyone's list is every profile's bookmarks pooled, same-spot ones merged.</summary>
    public const string Pooled = "pooled";

    /// <summary>An administrator changed the list at least once: Everyone's list is exactly the set they made.</summary>
    public const string Definitive = "definitive";
}

/// <summary>What one staged bookmark operation does. A change holds a list of them, applied in order.</summary>
public static class EditorBookmarkOpKinds
{
    /// <summary>Adds a profile's bookmark to Everyone's (the existing promote flag on the same row, with the same-spot merge).</summary>
    public const string Add = "add";

    /// <summary>Takes a bookmark out of Everyone's (clears the flag; the owner keeps the bookmark under Mine).</summary>
    public const string Remove = "remove";

    /// <summary>Gives a bookmark in Everyone's another name.</summary>
    public const string Rename = "rename";

    /// <summary>Moves a bookmark in Everyone's to another time.</summary>
    public const string ChangeTime = "change_time";

    /// <summary>Adds the bookmarks chosen in the compare view of an imported bookmark file.</summary>
    public const string Import = "import";

    /// <summary>Throws the definitive set away: Everyone's list pools from the profiles again.</summary>
    public const string StartOver = "start_over";

    public static readonly IReadOnlyList<string> All = [Add, Remove, Rename, ChangeTime, Import, StartOver];

    public static bool IsKnown(string? kind) => kind is not null && All.Contains(kind, StringComparer.Ordinal);
}

/// <summary>Limits shared by the Engine and the Dashboard for one bookmarks change.</summary>
public static class EditorBookmarkLimits
{
    /// <summary>The most operations one title's staged bookmark change may hold.</summary>
    public const int MaximumOps = 500;

    /// <summary>The most bookmarks one import operation may add.</summary>
    public const int MaximumImportItems = BookmarkPackLimits.MaximumBookmarks;

    /// <summary>The largest show file the Engine reads, in bytes.</summary>
    public const int MaximumShowFileBytes = 4 * 1024 * 1024;

    /// <summary>The most episodes one show file may hold.</summary>
    public const int MaximumShowEpisodes = 2000;

    /// <summary>The most bookmarks one show file may hold in all.</summary>
    public const int MaximumShowBookmarks = 20_000;
}

/// <summary>One bookmark chosen in the compare view, ready to be added to Everyone's.</summary>
public sealed record EditorBookmarkImportItem
{
    public double PositionSeconds { get; init; }
    public double? EndSeconds { get; init; }
    public string? ChapterTitle { get; init; }

    /// <summary>The bookmark's name (the file's note).</summary>
    public string? Name { get; init; }
    public string? Description { get; init; }

    /// <summary>The bookmark in Everyone's it replaces (it leaves Everyone's, its owner keeps it under Mine); null on a New row.</summary>
    public Guid? ReplacesBookmarkId { get; init; }
}

/// <summary>One staged bookmark operation. Which fields are used depends on <see cref="Kind"/> (see <see cref="EditorBookmarkOpKinds"/>).</summary>
public sealed record EditorBookmarkOp
{
    /// <summary>The Dashboard's own id for the operation, so Review can take just this one back out.</summary>
    public string Id { get; init; } = string.Empty;

    public string Kind { get; init; } = string.Empty;

    /// <summary>The bookmark the operation is about (add, remove, rename and change time).</summary>
    public Guid? BookmarkId { get; init; }

    /// <summary>The new name (rename).</summary>
    public string? Name { get; init; }

    /// <summary>The new time in seconds (change time).</summary>
    public double? PositionSeconds { get; init; }

    /// <summary>The bookmarks to add (import).</summary>
    public IReadOnlyList<EditorBookmarkImportItem> Items { get; init; } = [];
}

/// <summary>The staged bookmark changes of one title: the operations in the order the administrator made them.</summary>
public sealed record EditorBookmarkChangeDto
{
    public IReadOnlyList<EditorBookmarkOp> Ops { get; init; } = [];
}

/// <summary>One bookmark row of the Bookmarks tab, in either list.</summary>
public sealed record EditorBookmarkRowDto
{
    public Guid Id { get; init; }
    public Guid AssetId { get; init; }

    /// <summary><c>time</c> for a film, episode or audiobook; <c>text</c> or <c>page</c> for a book.</summary>
    public string PositionKind { get; init; } = "time";

    /// <summary>Where the bookmark is: seconds for a time, the page for a page.</summary>
    public double PositionSeconds { get; init; }
    public double? EndSeconds { get; init; }
    public int? ChapterIndex { get; init; }
    public string? ChapterTitle { get; init; }

    /// <summary>The bookmark's name (its label, else its note); empty when it has none.</summary>
    public string Name { get; init; } = string.Empty;
    public string? Note { get; init; }

    /// <summary>The profile that made the bookmark ("chosen from").</summary>
    public string? OwnerName { get; init; }

    /// <summary>True when the administrator reading the list made the bookmark.</summary>
    public bool IsYours { get; init; }

    /// <summary>Where the picture of that moment is, or null before it exists and for anything but a video.</summary>
    public string? FrameUrl { get; init; }

    /// <summary>True when the bookmark is in Everyone's, or is merged into one that is (a bookmark at the same spot).</summary>
    public bool InEveryone { get; init; }

    public DateTimeOffset CreatedAt { get; init; }
}

/// <summary>A title's Bookmarks tab: Everyone's list as the player shows it, and every profile's own bookmarks beside it.</summary>
public sealed record EditorBookmarksDto
{
    public Guid WorkId { get; init; }

    /// <summary>See <see cref="EditorBookmarkStates"/>.</summary>
    public string State { get; init; } = EditorBookmarkStates.Pooled;

    public IReadOnlyList<EditorBookmarkRowDto> Everyone { get; init; } = [];
    public IReadOnlyList<EditorBookmarkRowDto> FromProfiles { get; init; } = [];

    /// <summary>The file the title's bookmarks are on (the one an exported or imported file is for); null when the title has no playable file.</summary>
    public Guid? AssetId { get; init; }

    /// <summary>True when the bookmarks are times (a film, episode or audiobook): only those can be moved, exported and imported.</summary>
    public bool HasTimes { get; init; }

    /// <summary>True when the title is a video, so its rows carry a picture of the moment.</summary>
    public bool HasFrames { get; init; }

    /// <summary>The running time of the file in seconds, when known (a bookmark cannot be moved past it).</summary>
    public double? RunningTimeSeconds { get; init; }
}

/// <summary>One member (an episode, a track) of the overview on a show, season or album.</summary>
public sealed record EditorBookmarkOverviewRowDto
{
    public Guid WorkId { get; init; }

    /// <summary>"S1 E3" for an episode, "Track 4" for a song, or empty.</summary>
    public string Code { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public int? Number { get; init; }

    /// <summary>How many bookmarks Everyone sees on it.</summary>
    public int EveryoneCount { get; init; }

    /// <summary>See <see cref="EditorBookmarkStates"/>.</summary>
    public string State { get; init; } = EditorBookmarkStates.Pooled;

    /// <summary>How many bookmarks the profiles have made on it.</summary>
    public int ProfileCount { get; init; }
}

/// <summary>A season (or the loose members of an album) in the overview, with its members.</summary>
public sealed record EditorBookmarkOverviewGroupDto
{
    public Guid? GroupId { get; init; }

    /// <summary>"Season 1", or empty for an album's tracks.</summary>
    public string Title { get; init; } = string.Empty;
    public int? Number { get; init; }
    public int BookmarkCount { get; init; }
    public IReadOnlyList<EditorBookmarkOverviewRowDto> Rows { get; init; } = [];
}

/// <summary>The Bookmarks tab of a show, season or album: one row per member, grouped by season.</summary>
public sealed record EditorBookmarkOverviewDto
{
    public Guid RootId { get; init; }
    public string RootTitle { get; init; } = string.Empty;

    /// <summary>Bookmarks Everyone sees across the listed members.</summary>
    public int EveryoneTotal { get; init; }
    public int MemberCount { get; init; }

    /// <summary>How many of the listed members have a definitive set.</summary>
    public int DefinitiveCount { get; init; }
    public IReadOnlyList<EditorBookmarkOverviewGroupDto> Groups { get; init; } = [];
}

/// <summary>Asks the Engine to compare a bookmark file with a title's Everyone's list. The file's bytes are base64.</summary>
public sealed record EditorBookmarkCompareRequestDto
{
    public string ContentBase64 { get; init; } = string.Empty;
    public double? RunningTimeSeconds { get; init; }
    public IReadOnlyList<BookmarkPackChapterDto> Chapters { get; init; } = [];

    /// <summary>See <see cref="BookmarkPackImportModes"/>.</summary>
    public string Mode { get; init; } = BookmarkPackImportModes.AsIs;
}

/// <summary>One episode of a show file set beside what the library has for it.</summary>
public sealed record EditorBookmarkShowCompareEpisodeDto
{
    public int SeasonNumber { get; init; }
    public int EpisodeNumber { get; init; }

    /// <summary>The library's episode this one is for; null when the show has no such episode.</summary>
    public Guid? WorkId { get; init; }
    public string? Title { get; init; }

    /// <summary>How the episode's bookmarks sit beside Everyone's; null when the show has no such episode.</summary>
    public BookmarkSetCompareDto? Compare { get; init; }

    /// <summary>How many of the file's bookmarks for this episode are New (nothing near them in Everyone's).</summary>
    public int NewCount { get; init; }
}

/// <summary>A show file compared with the show, episode by episode.</summary>
public sealed record EditorBookmarkShowCompareDto
{
    public string Name { get; init; } = string.Empty;
    public int BookmarkCount { get; init; }

    /// <summary>Episodes in the file that the show has no match for (named by number in the dialog).</summary>
    public int UnmatchedEpisodes { get; init; }
    public IReadOnlyList<EditorBookmarkShowCompareEpisodeDto> Episodes { get; init; } = [];
}
