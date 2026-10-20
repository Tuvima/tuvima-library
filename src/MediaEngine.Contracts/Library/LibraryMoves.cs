using System.Text.Json.Serialization;

namespace MediaEngine.Contracts.Library;

// Moving items into another album, season, series, audiobook or hand-picked collection (Details overlay spec, WP5). Three Engine
// actions: list the places an item can go, preview a move (the Engine works out the Match step and what will happen), and commit it.
// Confirming sends back the preview's token; if anything changed since, the Engine refuses with a fresh preview (Delete D6 pattern).

/// <summary>The kinds of place an item can be moved into.</summary>
public static class LibraryMoveDestinationKinds
{
    /// <summary>An album: a song is matched against its track list.</summary>
    public const string Album = "album";

    /// <summary>A TV season (Season 0 is the Specials): an episode is matched against its episode list.</summary>
    public const string Season = "season";

    /// <summary>A book, comic or movie series: an item is matched against its positions (decimals such as 1.5 are allowed).</summary>
    public const string Series = "series";

    /// <summary>An audiobook: one of its files is matched against its parts list.</summary>
    public const string Audiobook = "audiobook";

    /// <summary>A hand-picked collection: no list to match against, the person's own order.</summary>
    public const string Collection = "collection";
}

/// <summary>The kinds of list a destination can be matched against.</summary>
public static class LibraryMoveListKinds
{
    public const string Tracks = "tracks";
    public const string Episodes = "episodes";
    public const string Positions = "positions";
    public const string Parts = "parts";

    /// <summary>Nothing known to match against: the move is free and the item goes at the end (Details overlay D14).</summary>
    public const string None = "none";
}

/// <summary>How one item is placed in the destination.</summary>
public static class LibraryMoveModes
{
    /// <summary>Takes the number and place of one entry in the destination's list. With no list, simply goes at the end.</summary>
    public const string Match = "match";

    /// <summary>Joins the destination with no number, in an "Unnumbered" group at the end, tagged "Not matched" (D13).</summary>
    public const string AddAnyway = "add_anyway";

    /// <summary>The entry already has a file: this one is added to it as another version (another Edition) (D16).</summary>
    public const string AddAsVersion = "add_as_version";
}

/// <summary>What happened to one item when a move was committed.</summary>
public static class LibraryMoveOutcomes
{
    public const string Matched = "matched";
    public const string AddedAsVersion = "added_as_version";
    public const string AddedUnmatched = "added_unmatched";

    /// <summary>Joined a destination with no list, at the end.</summary>
    public const string Moved = "moved";
}

/// <summary>One place an item can be moved into. The Engine only offers places that accept the item's kind (D15).</summary>
public sealed class LibraryMoveDestination
{
    /// <summary>The album, season, series or audiobook Work, or the hand-picked collection.</summary>
    [JsonPropertyName("id")] public Guid Id { get; init; }

    /// <summary>One of <see cref="LibraryMoveDestinationKinds"/>.</summary>
    [JsonPropertyName("kind")] public string Kind { get; init; } = string.Empty;

    /// <summary>For example "OK Computer", "Season 2" or "Specials".</summary>
    [JsonPropertyName("label")] public string Label { get; init; } = string.Empty;

    /// <summary>What the place sits under: an album's artist, a season's show, a series' author. Null when there is none.</summary>
    [JsonPropertyName("parent_label")] public string? ParentLabel { get; init; }

    /// <summary>True when the place has its own list, so moving into it goes through the Match step (D12).</summary>
    [JsonPropertyName("has_list")] public bool HasList { get; init; }
}

/// <summary>Plain reasons a preview row is blocked that a screen may need to tell apart from the others.</summary>
public static class LibraryMoveBlockedReasons
{
    /// <summary>Something is still working on the item. It passes by itself, so a screen may suggest trying again later.</summary>
    public const string StillProcessing = "Still being processed";
}

public sealed class LibraryMoveDestinationsResponse
{
    [JsonPropertyName("destinations")] public List<LibraryMoveDestination> Destinations { get; init; } = [];

    /// <summary>
    /// The place the item already sits in, when that place has its own list (the album, season, series or audiobook it is in). It is never
    /// among <see cref="Destinations"/> (those are places to move it to), so a screen opens the Match step on it directly to match an item
    /// that was added anyway, or to change an item's number. Null when the item sits in no such place, or the caller may not edit it.
    /// </summary>
    [JsonPropertyName("current_destination")] public LibraryMoveDestination? CurrentDestination { get; init; }
}

/// <summary>Asks the Engine what moving these items into a destination would do. Nothing is changed.</summary>
public sealed class LibraryMovePreviewRequest
{
    /// <summary>Ids of the items to move: titles (Works), or for an audiobook, one of its parts (an Edition). Duplicates are collapsed.</summary>
    [JsonPropertyName("item_ids")] public IReadOnlyList<Guid> ItemIds { get; init; } = [];

    [JsonPropertyName("destination_id")] public Guid DestinationId { get; init; }

    /// <summary>
    /// Optional. A hand-picked collection the items are being moved out of: they leave it as they join the destination, and if that
    /// leaves it empty it is removed (the preview says so). Without it, moving into a hand-picked collection only adds them to it.
    /// </summary>
    [JsonPropertyName("source_collection_id")] public Guid? SourceCollectionId { get; init; }
}

/// <summary>One entry in the destination's own list: a track, an episode, a position or a part.</summary>
public sealed class LibraryMoveEntry
{
    /// <summary>Names this entry in a commit. Stable for as long as the destination's list is unchanged.</summary>
    [JsonPropertyName("entry_key")] public string EntryKey { get; init; } = string.Empty;

    /// <summary>What the person reads: "2", "1.5", "S0 E3" or "D2 T5".</summary>
    [JsonPropertyName("number")] public string Number { get; init; } = string.Empty;

    [JsonPropertyName("title")] public string Title { get; init; } = string.Empty;

    [JsonPropertyName("length_seconds")] public double? LengthSeconds { get; init; }

    /// <summary>The item that already has a file at this entry. Null when the entry is free.</summary>
    [JsonPropertyName("taken_by_item_id")] public Guid? TakenByItemId { get; init; }

    [JsonPropertyName("taken_by_title")] public string? TakenByTitle { get; init; }

    /// <summary>
    /// True when a taken entry can take another version of the file (<c>add_as_version</c>). False for a taken entry the library cannot
    /// attach to (it is owned elsewhere), and for a free entry, which is matched instead.
    /// </summary>
    [JsonPropertyName("can_add_version")] public bool CanAddVersion { get; init; }

    /// <summary>
    /// True when matching an item to this entry merges it into a title that is already in the list (the entry's own title). An item whose
    /// details the person set cannot do that (see <see cref="LibraryMovePreviewRow.MergeBlockedReason"/>). False when the entry is a slot
    /// with no title yet, which the item simply takes.
    /// </summary>
    [JsonPropertyName("joins_existing_title")] public bool JoinsExistingTitle { get; init; }
}

/// <summary>A file's name and place before and after a move. Filled in when the move also renames and moves files.</summary>
public sealed class LibraryMoveFileChange
{
    [JsonPropertyName("from")] public string From { get; init; } = string.Empty;
    [JsonPropertyName("to")] public string To { get; init; } = string.Empty;
}

/// <summary>One item in a move preview: where it is now and the Engine's best guess for where it goes.</summary>
public sealed class LibraryMovePreviewRow
{
    [JsonPropertyName("item_id")] public Guid ItemId { get; init; }
    [JsonPropertyName("title")] public string Title { get; init; } = string.Empty;

    /// <summary>For example "The Best Of, track 3".</summary>
    [JsonPropertyName("current_placement_label")] public string CurrentPlacementLabel { get; init; } = string.Empty;

    /// <summary>The entry the Engine pre-selects (by title, then by length). Null when nothing fits or the destination has no list.</summary>
    [JsonPropertyName("suggested_entry_key")] public string? SuggestedEntryKey { get; init; }

    /// <summary>Why: "Same title" or "Closest length". Empty when there is no suggestion.</summary>
    [JsonPropertyName("suggestion_reason")] public string SuggestionReason { get; init; } = string.Empty;

    /// <summary>What happens to the file on disk. Null: the move changes the Library only. (Filled in when moves rename files.)</summary>
    [JsonPropertyName("file_change")] public LibraryMoveFileChange? FileChange { get; init; }

    /// <summary>One plain line about the file ("As-is folder: the file stays where it is"). Null when there is nothing to say.</summary>
    [JsonPropertyName("file_note")] public string? FileNote { get; init; }

    /// <summary>Why this item cannot be moved right now ("Still being processed"). Null when it can. Apply stays off while any row is blocked.</summary>
    [JsonPropertyName("blocked_reason")] public string? BlockedReason { get; init; }

    /// <summary>
    /// False when the caller may write this item but not match it to an entry (matching needs the metadata-match permission as well as
    /// metadata-write). Such a row can only be added anyway, or joined to a place with no list; asking to match it fails at the commit.
    /// </summary>
    [JsonPropertyName("can_match")] public bool CanMatch { get; init; } = true;

    /// <summary>
    /// Why this item cannot be matched to an entry that already has a title in the list ("You changed this item's details, so it can't
    /// be merged into an existing entry..."), while adding it anyway, or taking a slot with no title yet, still works. Null when it can.
    /// Unlike <see cref="BlockedReason"/> it does not stop Apply: the person picks a choice that works.
    /// </summary>
    [JsonPropertyName("merge_blocked_reason")] public string? MergeBlockedReason { get; init; }
}

/// <summary>What the Engine will do if the items are moved, worked out fresh for each request.</summary>
public sealed class LibraryMovePreview
{
    [JsonPropertyName("destination")] public LibraryMoveDestination Destination { get; init; } = new();

    /// <summary>One of <see cref="LibraryMoveListKinds"/>.</summary>
    [JsonPropertyName("list_kind")] public string ListKind { get; init; } = LibraryMoveListKinds.None;

    /// <summary>The destination's own list, taken entries marked. Empty when <see cref="ListKind"/> is "none".</summary>
    [JsonPropertyName("entries")] public List<LibraryMoveEntry> Entries { get; init; } = [];

    [JsonPropertyName("rows")] public List<LibraryMovePreviewRow> Rows { get; init; } = [];

    /// <summary>Plain consequences: "Leaves The Best Of", "Season 2 will be empty and removed".</summary>
    [JsonPropertyName("warnings")] public List<string> Warnings { get; init; } = [];

    /// <summary>Confirming sends it back. It lasts 10 minutes and works only for the person who asked and this exact request.</summary>
    [JsonPropertyName("preview_token")] public string PreviewToken { get; init; } = string.Empty;
}

/// <summary>The person's choice for one item.</summary>
public sealed class LibraryMoveCommitRow
{
    [JsonPropertyName("item_id")] public Guid ItemId { get; init; }

    /// <summary>One of <see cref="LibraryMoveModes"/>.</summary>
    [JsonPropertyName("mode")] public string Mode { get; init; } = LibraryMoveModes.Match;

    /// <summary>The chosen entry. Required for "match" and "add_as_version" when the destination has a list; absent otherwise.</summary>
    [JsonPropertyName("entry_key")] public string? EntryKey { get; init; }
}

public sealed class LibraryMoveCommitRequest
{
    [JsonPropertyName("preview_token")] public string PreviewToken { get; init; } = string.Empty;
    [JsonPropertyName("rows")] public IReadOnlyList<LibraryMoveCommitRow> Rows { get; init; } = [];
}

public sealed class LibraryMoveRowResult
{
    [JsonPropertyName("item_id")] public Guid ItemId { get; init; }

    /// <summary>One of <see cref="LibraryMoveOutcomes"/>.</summary>
    [JsonPropertyName("outcome")] public string Outcome { get; init; } = string.Empty;

    /// <summary>
    /// The Work the item now lives in or as: its own Work when it stayed a title of its own (placed, or added anyway), the entry's Work when
    /// it was merged into one (the item's own Work is gone), and the audiobook for a part. A screen showing the item re-targets to it.
    /// </summary>
    [JsonPropertyName("result_entity_id")] public Guid ResultEntityId { get; init; }
}

public sealed class LibraryMoveCommitResult
{
    [JsonPropertyName("moved_count")] public int MovedCount { get; init; }
    [JsonPropertyName("rows")] public List<LibraryMoveRowResult> Rows { get; init; } = [];

    /// <summary>Names of the places the move left empty and removed ("Season 2", "My road trip").</summary>
    [JsonPropertyName("removed_containers")] public List<string> RemovedContainers { get; init; } = [];
}

/// <summary>The 409 body when something changed since the preview: nothing was moved, and the fresh preview is here.</summary>
public sealed class LibraryMoveConflictDto
{
    [JsonPropertyName("message")] public string Message { get; init; } = string.Empty;
    [JsonPropertyName("preview")] public LibraryMovePreview Preview { get; init; } = new();
}
