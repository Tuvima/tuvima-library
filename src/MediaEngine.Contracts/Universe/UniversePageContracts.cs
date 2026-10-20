using System.Text.Json.Serialization;
using MediaEngine.Contracts.Display;

namespace MediaEngine.Contracts.Universe;

/// <summary>
/// Everything the Dashboard's Universe page (<c>/universe/{qid}</c>) reads in one action:
/// the same Universe detail the helper apps get (<see cref="Detail"/>), the Universe's own standalone works, the
/// missing entries per Series the profile chose to see, the enrichment state, and the size of the connection graph.
/// Every card, count and row was already limited to what the requesting profile may see.
/// </summary>
public sealed class UniversePageDto
{
    [JsonPropertyName("detail")]
    public UniverseClientDetailDto Detail { get; init; } = new();

    /// <summary>
    /// Works that belong directly to the Universe and to no Series, as one shelf; null when there are none.
    /// </summary>
    [JsonPropertyName("standalone")]
    public DisplayShelfDto? Standalone { get; init; }

    /// <summary>One entry per Series shelf in <see cref="Detail"/> that has missing entries, in the same order.</summary>
    [JsonPropertyName("missing")]
    public IReadOnlyList<UniverseShelfMissingDto> Missing { get; init; } = [];

    [JsonPropertyName("enrichment")]
    public UniverseEnrichmentStateDto Enrichment { get; init; } = new();

    /// <summary>Visible people, places and things the Library has learned about in this Universe.</summary>
    [JsonPropertyName("graph_entity_count")]
    public int GraphEntityCount { get; init; }

    /// <summary>Visible relationships between those entities.</summary>
    [JsonPropertyName("graph_relationship_count")]
    public int GraphRelationshipCount { get; init; }
}

/// <summary>Entries of one Series the Library does not own, already resolved against the profile's missing-items preference.</summary>
public sealed class UniverseShelfMissingDto
{
    [JsonPropertyName("collection_id")]
    public Guid CollectionId { get; init; }

    /// <summary>
    /// Whether this profile sees missing entries for this Series: the per-Series override when the profile set one,
    /// otherwise the media default from the library preferences (and only when the media type shows missing entries at all).
    /// </summary>
    [JsonPropertyName("show_missing")]
    public bool ShowMissing { get; init; }

    /// <summary>Main-sequence entries not in the Library; filled whether or not <see cref="ShowMissing"/> is true.</summary>
    [JsonPropertyName("missing_count")]
    public int MissingCount { get; init; }

    /// <summary>The missing entries in Series order; empty when <see cref="ShowMissing"/> is false.</summary>
    [JsonPropertyName("items")]
    public IReadOnlyList<UniverseMissingItemDto> Items { get; init; } = [];
}

/// <summary>One entry of a Series the Library does not hold.</summary>
public sealed class UniverseMissingItemDto
{
    [JsonPropertyName("label")]
    public string Label { get; init; } = string.Empty;

    /// <summary>The number as the source gives it ("3", "2.5"); null when it has none.</summary>
    [JsonPropertyName("raw_ordinal")]
    public string? RawOrdinal { get; init; }

    [JsonPropertyName("parsed_ordinal")]
    public double? ParsedOrdinal { get; init; }

    [JsonPropertyName("item_qid")]
    public string ItemQid { get; init; } = string.Empty;

    /// <summary>
    /// The owned work this entry follows in the Series order, so a reader can place it between cards; null when it
    /// comes before every owned work.
    /// </summary>
    [JsonPropertyName("after_work_id")]
    public Guid? AfterWorkId { get; init; }
}

/// <summary>How far the Engine has got learning about a Universe's works.</summary>
public sealed class UniverseEnrichmentStateDto
{
    public const string NotStarted = "not_started";
    public const string Running = "running";
    public const string Ready = "ready";

    /// <summary><c>not_started</c>, <c>running</c> or <c>ready</c>.</summary>
    [JsonPropertyName("state")]
    public string State { get; init; } = NotStarted;

    [JsonPropertyName("enriched_work_count")]
    public int EnrichedWorkCount { get; init; }

    [JsonPropertyName("work_count")]
    public int WorkCount { get; init; }
}
