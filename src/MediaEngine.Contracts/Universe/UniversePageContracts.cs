using System.Text.Json.Serialization;
using MediaEngine.Contracts.Display;

namespace MediaEngine.Contracts.Universe;

/// <summary>
/// Everything the Dashboard's Universe page (<c>/universe/{qid}</c>) reads in one action:
/// the same Universe detail the helper apps get (<see cref="Detail"/>), the Universe's own standalone works, the
/// missing entries per Series the profile chose to see, the enrichment state, the size of the connection graph, and the
/// evidence-backed Connections with a source on every row (<see cref="Connections"/>).
/// Every card, count and row was already limited to what the requesting profile may see.
/// </summary>
public sealed class UniversePageDto
{
    /// <summary>
    /// The helper-app detail, except that <see cref="UniverseClientDetailDto.Cast"/> and
    /// <see cref="UniverseClientDetailDto.Adaptations"/> are left empty here: the page reads the same rows, with their
    /// sources, from <see cref="Connections"/>.
    /// </summary>
    [JsonPropertyName("detail")]
    public UniverseClientDetailDto Detail { get; init; } = new();

    /// <summary>The Universe's Characters, Cast and Adaptations, each row carrying the source it came from.</summary>
    [JsonPropertyName("connections")]
    public UniverseConnectionsDto Connections { get; init; } = new();

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

/// <summary>
/// What the Universe page's Connections section lists, evidence only: every row was recorded from a source (Wikidata or an
/// approved lore source), every row names that source, and nothing is a sample or a guess. Empty lists mean the Library has
/// not learned (or cannot show) anything of that kind yet.
/// </summary>
public sealed class UniverseConnectionsDto
{
    /// <summary>
    /// The Universe's characters the profile may see, the ones seen in the most works first, at most the Engine's cap;
    /// characters with no plot details come last.
    /// </summary>
    [JsonPropertyName("characters")]
    public IReadOnlyList<UniverseCharacterDto> Characters { get; init; } = [];

    /// <summary>All visible characters, including any beyond <see cref="Characters"/>.</summary>
    [JsonPropertyName("character_count")]
    public int CharacterCount { get; init; }

    /// <summary>The helper-app Cast rows, in the same order, each with its sources.</summary>
    [JsonPropertyName("cast")]
    public IReadOnlyList<UniverseSourcedCastMemberDto> Cast { get; init; } = [];

    /// <summary>The helper-app Adaptations rows, in the same order, each with its sources.</summary>
    [JsonPropertyName("adaptations")]
    public IReadOnlyList<UniverseSourcedAdaptationDto> Adaptations { get; init; } = [];
}

/// <summary>One character of a Universe, read-only: its name, where the Library learned it, and how much it may show.</summary>
public sealed class UniverseCharacterDto
{
    [JsonPropertyName("qid")]
    public string Qid { get; init; } = string.Empty;

    [JsonPropertyName("label")]
    public string Label { get; init; } = string.Empty;

    /// <summary>
    /// Works the profile may see where this character appears without a spoiler marker; 0 when
    /// <see cref="NoPlotDetails"/> is true.
    /// </summary>
    [JsonPropertyName("work_count")]
    public int WorkCount { get; init; }

    /// <summary>
    /// True when every appearance the profile may see is marked as a spoiler for a work. Until the Library knows how far the
    /// viewer has got, such a character is listed by name and source only: no work count, no "Also appears in".
    /// </summary>
    [JsonPropertyName("no_plot_details")]
    public bool NoPlotDetails { get; init; }

    /// <summary>Where the Library learned about this character; never empty.</summary>
    [JsonPropertyName("sources")]
    public IReadOnlyList<UniverseSourceDto> Sources { get; init; } = [];
}

/// <summary>A helper-app Cast row with the sources its credits came from.</summary>
public sealed class UniverseSourcedCastMemberDto
{
    [JsonPropertyName("member")]
    public UniverseClientCastMemberDto Member { get; init; } = new();

    /// <summary>Where the Library learned these credits; never empty.</summary>
    [JsonPropertyName("sources")]
    public IReadOnlyList<UniverseSourceDto> Sources { get; init; } = [];
}

/// <summary>A helper-app Adaptations row with the sources of its relationship.</summary>
public sealed class UniverseSourcedAdaptationDto
{
    [JsonPropertyName("adaptation")]
    public UniverseClientAdaptationDto Adaptation { get; init; } = new();

    /// <summary>Where the Library learned this relationship; never empty.</summary>
    [JsonPropertyName("sources")]
    public IReadOnlyList<UniverseSourceDto> Sources { get; init; } = [];
}

/// <summary>Where a connection came from, as the person sees it ("Wikidata", "Dune Wiki").</summary>
public sealed class UniverseSourceDto
{
    public const string Wikidata = "wikidata";
    public const string LorePlugin = "lore_plugin";
    public const string Other = "other";

    /// <summary><c>wikidata</c>, <c>lore_plugin</c> or <c>other</c>.</summary>
    [JsonPropertyName("kind")]
    public string Kind { get; init; } = Wikidata;

    /// <summary>The name shown on screen, for example "Wikidata" or the lore source's name.</summary>
    [JsonPropertyName("label")]
    public string Label { get; init; } = string.Empty;
}
