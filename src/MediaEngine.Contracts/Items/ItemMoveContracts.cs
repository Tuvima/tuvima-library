using System.Text.Json.Serialization;

namespace MediaEngine.Contracts.Items;

/// <summary>How a title joins its destination (details overlay spec D12 to D16).</summary>
public static class ItemMoveModes
{
    /// <summary>The title takes the number and identity of an empty entry in the destination's list.</summary>
    public const string Match = "match";

    /// <summary>The chosen entry already has a file; the file is added as another version of that entry.</summary>
    public const string AnotherVersion = "another_version";

    /// <summary>Nothing fits: the title joins with no number, tagged "Not matched".</summary>
    public const string AddAnyway = "add_anyway";
}

/// <summary>Asks which places a title can move to. Nothing is changed.</summary>
public sealed class ItemMoveDestinationsRequest
{
    [JsonPropertyName("work_id")] public Guid WorkId { get; init; }

    /// <summary>Optional words to narrow the places by title.</summary>
    [JsonPropertyName("query")] public string? Query { get; init; }
}

/// <summary>A place a title can move to (an album, a season, an audiobook). Only places that accept this kind are offered.</summary>
public sealed class ItemMoveDestinationDto
{
    [JsonPropertyName("id")] public Guid Id { get; init; }
    [JsonPropertyName("title")] public string Title { get; init; } = string.Empty;
    [JsonPropertyName("entry_count")] public int EntryCount { get; init; }
}

public sealed class ItemMoveDestinationsResponse
{
    [JsonPropertyName("destinations")] public List<ItemMoveDestinationDto> Destinations { get; init; } = [];
}

/// <summary>Asks what a move would do. Nothing is changed.</summary>
public sealed class ItemMovePreviewRequest
{
    [JsonPropertyName("work_id")] public Guid WorkId { get; init; }
    [JsonPropertyName("destination_id")] public Guid DestinationId { get; init; }

    /// <summary>The entry in the destination's list the person picked, or null to let the Engine pre-select its best guess.</summary>
    [JsonPropertyName("entry_id")] public Guid? EntryId { get; init; }

    /// <summary>One of <see cref="ItemMoveModes"/>. Null means a match (or another version when the picked entry is taken).</summary>
    [JsonPropertyName("mode")] public string? Mode { get; init; }
}

/// <summary>One line of the destination's own list.</summary>
public sealed class ItemMoveEntryDto
{
    [JsonPropertyName("id")] public Guid Id { get; init; }
    [JsonPropertyName("title")] public string Title { get; init; } = string.Empty;

    /// <summary>Track, episode or part number; null for an entry that was added without one.</summary>
    [JsonPropertyName("number")] public int? Number { get; init; }

    /// <summary>True when the entry already has a file (spec D16).</summary>
    [JsonPropertyName("taken")] public bool Taken { get; init; }
}

/// <summary>What the Engine will do, worked out fresh for each request.</summary>
public sealed class ItemMovePreview
{
    [JsonPropertyName("work_id")] public Guid WorkId { get; init; }
    [JsonPropertyName("title")] public string Title { get; init; } = string.Empty;
    [JsonPropertyName("from_title")] public string? FromTitle { get; init; }
    [JsonPropertyName("destination_id")] public Guid DestinationId { get; init; }
    [JsonPropertyName("destination_title")] public string DestinationTitle { get; init; } = string.Empty;
    [JsonPropertyName("entries")] public List<ItemMoveEntryDto> Entries { get; init; } = [];

    /// <summary>The entry that is chosen now: the person's pick, else the Engine's best guess; null for Add anyway or when nothing fits.</summary>
    [JsonPropertyName("selected_entry_id")] public Guid? SelectedEntryId { get; init; }

    /// <summary>Why the Engine picked it ("Same title"), when it was the Engine's guess.</summary>
    [JsonPropertyName("guess_reason")] public string? GuessReason { get; init; }

    /// <summary>The mode the confirm would use (one of <see cref="ItemMoveModes"/>).</summary>
    [JsonPropertyName("mode")] public string Mode { get; init; } = ItemMoveModes.Match;

    /// <summary>True when the chosen entry already has a file, so the choice is Add as another version or Pick another entry.</summary>
    [JsonPropertyName("selected_entry_taken")] public bool SelectedEntryTaken { get; init; }

    /// <summary>The plain sentence the confirm dialog shows (spec D17), in the Engine's words.</summary>
    [JsonPropertyName("message")] public string Message { get; init; } = string.Empty;

    /// <summary>True when the file is renamed and moved to fit its new place (managed folders; the organiser still decides).</summary>
    [JsonPropertyName("renames_file")] public bool RenamesFile { get; init; }

    /// <summary>True when the old place would be left with no files.</summary>
    [JsonPropertyName("leaves_old_place_empty")] public bool LeavesOldPlaceEmpty { get; init; }

    /// <summary>Why the confirm is off, when it is (nothing chosen yet, or the title is no longer movable).</summary>
    [JsonPropertyName("blocked_reason")] public string? BlockedReason { get; init; }

    /// <summary>Identifies this exact plan. Confirming sends it back so the Engine can tell nothing changed since the preview.</summary>
    [JsonPropertyName("fingerprint")] public string Fingerprint { get; init; } = string.Empty;
}

/// <summary>Confirms a move by its fingerprint.</summary>
public sealed class ItemMoveRequest
{
    [JsonPropertyName("work_id")] public Guid WorkId { get; init; }
    [JsonPropertyName("destination_id")] public Guid DestinationId { get; init; }
    [JsonPropertyName("entry_id")] public Guid? EntryId { get; init; }
    [JsonPropertyName("mode")] public string Mode { get; init; } = ItemMoveModes.Match;
    [JsonPropertyName("fingerprint")] public string Fingerprint { get; init; } = string.Empty;
}

public sealed class ItemMoveResultDto
{
    [JsonPropertyName("work_id")] public Guid WorkId { get; init; }

    /// <summary>The Work the person should look at now: the matched entry, or the moved title itself for Add anyway.</summary>
    [JsonPropertyName("landed_work_id")] public Guid LandedWorkId { get; init; }
    [JsonPropertyName("destination_title")] public string DestinationTitle { get; init; } = string.Empty;
    [JsonPropertyName("mode")] public string Mode { get; init; } = ItemMoveModes.Match;
}

/// <summary>A 409: the plan changed since the preview. Nothing was moved; the fresh preview is attached.</summary>
public sealed class ItemMoveConflictDto
{
    [JsonPropertyName("message")] public string Message { get; init; } = string.Empty;
    [JsonPropertyName("preview")] public ItemMovePreview? Preview { get; init; }
}
