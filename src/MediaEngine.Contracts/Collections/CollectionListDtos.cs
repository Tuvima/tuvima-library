namespace MediaEngine.Contracts.Collections;

/// <summary>The words used on the wire for an imported list's choices (Collections and lists spec D5, 4b).</summary>
public static class ListImportChoices
{
    public const string OrderList = "list";
    public const string OrderRelease = "release";
    public const string OrderTitle = "title";

    public const string RefreshNever = "never";
    public const string RefreshDaily = "daily";
    public const string RefreshWeekly = "weekly";

    /// <summary>Titles that leave the list leave the collection (the default).</summary>
    public const string SyncFollow = "follow";

    /// <summary>Everything ever added stays.</summary>
    public const string SyncKeep = "keep";

    public const string StatusMatched = "matched";
    public const string StatusUnmatched = "unmatched";
    public const string StatusIgnored = "ignored";
    public const string StatusHandFixed = "hand_fixed";
}

/// <summary>A person's hand-fix for one list entry: pick the right title from the library, or ignore the entry.</summary>
public sealed record ListEntryOverrideDto
{
    /// <summary>The entry's position in the list, starting at 1.</summary>
    public int Position { get; init; }

    /// <summary>The library title the person picked. Null together with <see cref="Ignore"/> false means no override.</summary>
    public Guid? WorkId { get; init; }

    /// <summary>True to leave this entry out for good.</summary>
    public bool Ignore { get; init; }
}

/// <summary>A list file (or pasted text) to read, and the choices that shape the preview.</summary>
public sealed record ListImportPreviewRequest
{
    public string FileName { get; init; } = string.Empty;
    public string? ContentBase64 { get; init; }
    public string? Text { get; init; }

    /// <summary>When set, only the entries on this shelf (Goodreads and StoryGraph) are previewed.</summary>
    public string? Shelf { get; init; }

    public IReadOnlyList<ListEntryOverrideDto> Overrides { get; init; } = [];
    public Guid? ProfileId { get; init; }
}

/// <summary>One shelf found in a Goodreads or StoryGraph export, offered at import as its own collection.</summary>
public sealed record ListImportShelfDto(string Name, int EntryCount, int OwnedCount);

/// <summary>One entry of an imported list and what it matched.</summary>
public sealed record CollectionListEntryDto
{
    public Guid? Id { get; init; }
    public int Position { get; init; }
    public string Title { get; init; } = string.Empty;
    public int? Year { get; init; }
    public string? Creator { get; init; }
    public string? Shelf { get; init; }

    /// <summary>See <see cref="ListImportChoices"/> (matched, unmatched, ignored, hand_fixed).</summary>
    public string Status { get; init; } = ListImportChoices.StatusUnmatched;

    public Guid? MatchedWorkId { get; init; }
    public string? MatchedTitle { get; init; }

    /// <summary>How the match was made: an identifier name such as <c>imdb_id</c>, or <c>title</c>.</summary>
    public string? MatchBasis { get; init; }

    /// <summary>False when the title has since left the list (kept because the list keeps everything ever added).</summary>
    public bool OnList { get; init; } = true;
}

/// <summary>One owned title in the preview, with its cover.</summary>
public sealed record ListImportOwnedItemDto(Guid WorkId, string Title, string MediaType, string? CoverUrl);

/// <summary>The preview of a list: "You own 83 of 250" and what could not be matched.</summary>
public sealed record ListImportPreviewDto
{
    /// <summary>imdb, letterboxd, trakt, goodreads, storygraph, csv or text.</summary>
    public string SourceKind { get; init; } = "csv";

    /// <summary>The source in words, for example "Letterboxd export".</summary>
    public string SourceLabel { get; init; } = string.Empty;

    public string SourceName { get; init; } = string.Empty;

    /// <summary>The web address a web list was read from. Null for a file.</summary>
    public string? SourceAddress { get; init; }

    /// <summary>True when the list comes from a web address, so it can be refreshed from where it came from.</summary>
    public bool IsWebSource { get; init; }

    /// <summary>True when the site did not answer: nothing is read yet, the collection is saved and Library will try again.</summary>
    public bool WillTryAgain { get; init; }

    /// <summary>A name to offer for the collection: the list's own name when the file has one, otherwise the file's.</summary>
    public string SuggestedName { get; init; } = string.Empty;

    public int EntryCount { get; init; }
    public int OwnedCount { get; init; }
    public int UnmatchedCount { get; init; }
    public int IgnoredCount { get; init; }
    public IReadOnlyList<ListImportShelfDto> Shelves { get; init; } = [];
    public IReadOnlyList<CollectionListEntryDto> Entries { get; init; } = [];
    public IReadOnlyList<ListImportOwnedItemDto> Owned { get; init; } = [];

    /// <summary>Every title the list matched, in list order. The New collection flow creates the collection with these.</summary>
    public IReadOnlyList<Guid> MatchedWorkIds { get; init; } = [];

    /// <summary>The media types of the owned titles, so the flow can tick the right chips.</summary>
    public IReadOnlyList<string> MediaTypes { get; init; } = [];
}

/// <summary>Creates the list source for a collection that was just made, from the file it came from.</summary>
public sealed record ListImportRequest
{
    public string FileName { get; init; } = string.Empty;
    public string? ContentBase64 { get; init; }
    public string? Text { get; init; }
    public string? Shelf { get; init; }

    /// <summary>See <see cref="ListImportChoices"/>: list, release or title.</summary>
    public string Order { get; init; } = ListImportChoices.OrderList;

    public string Refresh { get; init; } = ListImportChoices.RefreshNever;
    public string SyncMode { get; init; } = ListImportChoices.SyncFollow;
    public IReadOnlyList<ListEntryOverrideDto> Overrides { get; init; } = [];
    public Guid? ProfileId { get; init; }
}

public sealed record ListImportResultDto(Guid CollectionId, int EntryCount, int MatchedCount, int UnmatchedCount);

/// <summary>A collection's list source, its counts and the entries that could not be matched.</summary>
public sealed record CollectionListSourceDto
{
    public Guid CollectionId { get; init; }
    public string SourceKind { get; init; } = "csv";
    public string SourceLabel { get; init; } = string.Empty;
    public string SourceName { get; init; } = string.Empty;

    /// <summary>The web address a web list was read from. Null for a file.</summary>
    public string? SourceAddress { get; init; }

    /// <summary>True when the list comes from a web address, so Refresh (never, daily, weekly) applies.</summary>
    public bool IsWebSource { get; init; }

    public string? Shelf { get; init; }
    public string Order { get; init; } = ListImportChoices.OrderList;
    public string Refresh { get; init; } = ListImportChoices.RefreshNever;
    public string SyncMode { get; init; } = ListImportChoices.SyncFollow;
    public DateTimeOffset? LastRefreshedAt { get; init; }
    public int EntryCount { get; init; }
    public int OwnedCount { get; init; }
    public int UnmatchedCount { get; init; }
    public IReadOnlyList<CollectionListEntryDto> Unmatched { get; init; } = [];

    /// <summary>
    /// True when the list was read with a profile's own account on its site (a private or own Trakt list) and that account is no
    /// longer signed in, so the list cannot refresh until the person signs in again. The collection keeps what it has.
    /// </summary>
    public bool NeedsSignIn { get; init; }

    /// <summary>Where the person signs in again (a path in the Dashboard). Null unless <see cref="NeedsSignIn"/>.</summary>
    public string? SignInHref { get; init; }
}

/// <summary>Changes the choices on a list collection.</summary>
public sealed record ListSourceSettingsRequest
{
    public string Order { get; init; } = ListImportChoices.OrderList;
    public string Refresh { get; init; } = ListImportChoices.RefreshNever;
    public string SyncMode { get; init; } = ListImportChoices.SyncFollow;
    public Guid? ProfileId { get; init; }
}

/// <summary>Hand-fixes one entry: <c>pick</c> a library title, <c>ignore</c> it, or <c>restore</c> it to automatic matching.</summary>
public sealed record ListEntryFixRequest
{
    public string Action { get; init; } = "pick";
    public Guid? WorkId { get; init; }
    public Guid? ProfileId { get; init; }
}

/// <summary>
/// Refreshes a list collection now. A newer copy of the list may come with it (a re-exported file); without one the stored
/// entries are matched again against the library, which picks up titles added since.
/// </summary>
public sealed record ListRefreshRequest
{
    public string? FileName { get; init; }
    public string? ContentBase64 { get; init; }
    public string? Text { get; init; }
    public Guid? ProfileId { get; init; }
}

/// <param name="WillTryAgain">True when a web list could not be reached: the collection kept what it had and Library will try again.</param>
public sealed record ListRefreshResultDto(int Added, int Removed, int Matched, int Unmatched, bool WillTryAgain = false);
