namespace MediaEngine.Domain.Entities;

/// <summary>
/// Where an imported list collection came from and how it stays in step (Collections and lists spec D5). Maps to the
/// <c>collection_list_sources</c> table; one row per list collection.
/// </summary>
public sealed class CollectionListSource
{
    public Guid CollectionId { get; set; }

    /// <summary>imdb, letterboxd, trakt, goodreads, storygraph, csv or text (web sources add their own).</summary>
    public string SourceKind { get; set; } = "csv";

    /// <summary>The source in words, for example "Letterboxd export".</summary>
    public string SourceLabel { get; set; } = string.Empty;

    /// <summary>The file name the list came from. Never a path.</summary>
    public string SourceName { get; set; } = string.Empty;

    /// <summary>The web address, for a web source. Null for a file.</summary>
    public string? SourceAddress { get; set; }

    /// <summary>The shelf of a Goodreads, StoryGraph or Letterboxd export this collection follows.</summary>
    public string? Shelf { get; set; }

    /// <summary>list, release or title.</summary>
    public string OrderBy { get; set; } = "list";

    /// <summary>never, daily or weekly.</summary>
    public string Refresh { get; set; } = "never";

    /// <summary>follow (titles that leave the list leave the collection) or keep (everything ever added stays).</summary>
    public string SyncMode { get; set; } = "follow";

    public DateTimeOffset? LastRefreshedAt { get; set; }
    public DateTimeOffset? NextRefreshAt { get; set; }
    public int EntryCount { get; set; }
    public int MatchedCount { get; set; }
    public int UnmatchedCount { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>One entry of an imported list. Maps to <c>collection_list_entries</c>.</summary>
public sealed class CollectionListEntry
{
    public Guid Id { get; set; }
    public Guid CollectionId { get; set; }

    /// <summary>The entry's place in the list, starting at 1.</summary>
    public int Position { get; set; }

    public string Title { get; set; } = string.Empty;
    public int? Year { get; set; }
    public string? Creator { get; set; }
    public string? Shelf { get; set; }

    /// <summary>Every identifier the list gave, as JSON: <c>{"imdb_id":"tt0123456"}</c>.</summary>
    public string IdsJson { get; set; } = "{}";

    /// <summary>The library media types the entry can be, comma separated (a Letterboxd row is a film). Empty means any.</summary>
    public string? MediaTypes { get; set; }

    /// <summary>The entry's identity across refreshes: its strongest identifier, otherwise its normalised title and year.</summary>
    public string EntryKey { get; set; } = string.Empty;

    /// <summary>matched, unmatched, ignored or hand_fixed.</summary>
    public string Status { get; set; } = "unmatched";

    public Guid? MatchedWorkId { get; set; }

    /// <summary>An identifier name such as <c>imdb_id</c>, or <c>title</c>, or <c>hand</c> for a hand-fix.</summary>
    public string? MatchBasis { get; set; }

    /// <summary>False when the title has since left the list but the collection keeps everything ever added.</summary>
    public bool OnList { get; set; } = true;
}

/// <summary>A title's place in a list collection, as the refresh wants the collection to end up.</summary>
/// <param name="WorkId">The library title.</param>
/// <param name="SortOrder">Its place in the collection.</param>
/// <param name="AddedBy"><c>hand</c> or <c>list</c>.</param>
public sealed record ListCollectionItemPlacement(Guid WorkId, int SortOrder, string AddedBy);

/// <summary>A row of <c>collection_items</c> with who added it.</summary>
public sealed class ListCollectionItem
{
    public Guid Id { get; init; }

    public Guid WorkId { get; init; }

    public int SortOrder { get; init; }

    public string AddedBy { get; init; } = "hand";
}
