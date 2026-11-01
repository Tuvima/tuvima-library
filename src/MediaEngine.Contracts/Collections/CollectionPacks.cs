using System.Text.Json.Serialization;

namespace MediaEngine.Contracts.Collections;

// Packs (Collections and lists spec D14 and 4e): ready-made collections built from the person's own library. A pack is a
// set of rule collections (or one collection). The same definition is also a small file (names, rules, windows and
// artwork hints, never any user data) that can be exported from a set and imported on another Library.

/// <summary>The id, name and wording of the built-in starter packs are fixed by the Engine; this is how one reads on the wire.</summary>
public sealed class CollectionPackSummaryDto
{
    [JsonPropertyName("id")] public string Id { get; init; } = string.Empty;
    [JsonPropertyName("name")] public string Name { get; init; } = string.Empty;
    [JsonPropertyName("description")] public string Description { get; init; } = string.Empty;
    [JsonPropertyName("icon_name")] public string? IconName { get; init; }

    /// <summary>True when the pack creates a set (a parent collection) holding several collections; false when it creates one collection.</summary>
    [JsonPropertyName("creates_set")] public bool CreatesSet { get; init; }

    /// <summary>"Would create N collections from your library": how many collections the library supports right now.</summary>
    [JsonPropertyName("would_create_count")] public int WouldCreateCount { get; init; }

    /// <summary>True when the counts depend on who is looking (Unwatched and the like) and were worked out for the caller.</summary>
    [JsonPropertyName("uses_per_profile_fields")] public bool UsesPerProfileFields { get; init; }

    /// <summary>The collections the pack would create, with how many titles each holds today.</summary>
    [JsonPropertyName("collections")] public List<CollectionPackEntryDto> Collections { get; init; } = [];
}

public sealed class CollectionPackEntryDto
{
    [JsonPropertyName("name")] public string Name { get; init; } = string.Empty;
    [JsonPropertyName("description")] public string? Description { get; init; }
    [JsonPropertyName("item_count")] public int ItemCount { get; init; }
    [JsonPropertyName("media_type_counts")] public Dictionary<string, int> MediaTypeCounts { get; init; } = [];

    /// <summary>"1 Oct to 1 Nov" for a seasonal collection; absent otherwise.</summary>
    [JsonPropertyName("season_text")] public string? SeasonText { get; init; }
}

public sealed class CollectionPackListResponse
{
    [JsonPropertyName("packs")] public List<CollectionPackSummaryDto> Packs { get; init; } = [];
}

/// <summary>Creates the collections of one or more packs, either built-in ones by id or the pack file whose text is given.</summary>
public sealed class CollectionPackCreateRequest
{
    /// <summary>Built-in packs to create (ticking several is allowed).</summary>
    [JsonPropertyName("pack_ids")] public List<string> PackIds { get; init; } = [];

    /// <summary>The text of a pack file to create instead of (or as well as) built-in packs.</summary>
    [JsonPropertyName("file_content")] public string? FileContent { get; init; }

    /// <summary>Keeps the packs to these media types (a playlist flow passes Music); empty means everything.</summary>
    [JsonPropertyName("media_types")] public List<string> MediaTypes { get; init; } = [];

    [JsonPropertyName("owner_kind")] public string OwnerKind { get; init; } = "Profile";
    [JsonPropertyName("audience")] public string Audience { get; init; } = "Private";
    [JsonPropertyName("selected_profile_ids")] public List<Guid> SelectedProfileIds { get; init; } = [];

    /// <summary>Where every created collection shows as a lane: any of home, watch, read, listen. Empty shows none.</summary>
    [JsonPropertyName("show_on")] public List<string>? ShowOn { get; init; }
}

/// <summary>The preview of one pack file, the answer to "what would this file create here".</summary>
public sealed class CollectionPackFilePreviewRequest
{
    [JsonPropertyName("content")] public string Content { get; init; } = string.Empty;
    [JsonPropertyName("media_types")] public List<string> MediaTypes { get; init; } = [];
}

public sealed class CollectionPackCreatedDto
{
    [JsonPropertyName("id")] public Guid Id { get; init; }
    [JsonPropertyName("name")] public string Name { get; init; } = string.Empty;

    /// <summary>The set this collection sits in; absent for a set itself and for a pack that creates one collection.</summary>
    [JsonPropertyName("set_id")] public Guid? SetId { get; init; }
}

public sealed class CollectionPackSkippedDto
{
    [JsonPropertyName("name")] public string Name { get; init; } = string.Empty;
    [JsonPropertyName("reason")] public string Reason { get; init; } = string.Empty;
}

public sealed class CollectionPackCreateResponse
{
    [JsonPropertyName("sets")] public List<CollectionPackCreatedDto> Sets { get; init; } = [];
    [JsonPropertyName("collections")] public List<CollectionPackCreatedDto> Collections { get; init; } = [];

    /// <summary>Collections left out and why: not enough titles, or one with the same rules already exists.</summary>
    [JsonPropertyName("skipped")] public List<CollectionPackSkippedDto> Skipped { get; init; } = [];
}

/// <summary>A set written out as a pack file, with the names of any collections that could not travel in it.</summary>
public sealed class CollectionPackExportResponse
{
    [JsonPropertyName("file")] public CollectionPackFileDto File { get; init; } = new();

    /// <summary>Collections left out because they read this Library's folders or other collections, or no longer use rules.</summary>
    [JsonPropertyName("omitted")] public List<string> Omitted { get; init; } = [];
}

/// <summary>What removing a set did.</summary>
public sealed class CollectionSetRemovedResponse
{
    [JsonPropertyName("removed_collections")] public int RemovedCollections { get; init; }
}

/// <summary>
/// The pack file. A small definition with the names, rules, windows and artwork hints of the collections, and nothing
/// about the person's library or activity.
/// </summary>
public sealed class CollectionPackFileDto
{
    public const string FormatName = "tuvima-collection-pack";
    public const int CurrentVersion = 1;

    [JsonPropertyName("format")] public string Format { get; init; } = FormatName;
    [JsonPropertyName("version")] public int Version { get; init; } = CurrentVersion;
    [JsonPropertyName("name")] public string Name { get; init; } = string.Empty;
    [JsonPropertyName("description")] public string? Description { get; init; }

    /// <summary>True when importing it creates a set holding the collections; false for a pack with one collection.</summary>
    [JsonPropertyName("creates_set")] public bool CreatesSet { get; init; } = true;

    [JsonPropertyName("collections")] public List<CollectionPackFileEntryDto> Collections { get; init; } = [];
}

public sealed class CollectionPackFileEntryDto
{
    [JsonPropertyName("name")] public string Name { get; init; } = string.Empty;
    [JsonPropertyName("description")] public string? Description { get; init; }
    [JsonPropertyName("icon_name")] public string? IconName { get; init; }
    [JsonPropertyName("primary_area")] public string PrimaryArea { get; init; } = "Mixed";
    [JsonPropertyName("rule_definition")] public CollectionRuleDefinitionDto RuleDefinition { get; init; } = new();
    [JsonPropertyName("sort_field")] public string? SortField { get; init; }
    [JsonPropertyName("sort_direction")] public string SortDirection { get; init; } = "desc";
    [JsonPropertyName("secondary_sort_field")] public string? SecondarySortField { get; init; }
    [JsonPropertyName("secondary_sort_direction")] public string? SecondarySortDirection { get; init; }

    /// <summary>Fewest titles the collection needs before it is shown; zero uses the lane default. It is also how many titles the library needs before the pack creates it.</summary>
    [JsonPropertyName("min_items")] public int MinItems { get; init; }
    [JsonPropertyName("max_items")] public int MaxItems { get; init; }

    /// <summary>Seasonal window as MM-dd; both edges or neither.</summary>
    [JsonPropertyName("season_from")] public string? SeasonFrom { get; init; }
    [JsonPropertyName("season_to")] public string? SeasonTo { get; init; }

    /// <summary>A hint for the collection's artwork: <c>stack</c> (the automatic stack of covers) is the only one defined so far.</summary>
    [JsonPropertyName("artwork_hint")] public string? ArtworkHint { get; init; } = "stack";
}
