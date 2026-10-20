using System.Text.Json.Serialization;
using MediaEngine.Contracts.Display;

namespace MediaEngine.Contracts.Universe;

/// <summary>
/// One Universe for a paired native app: its identity, member shelves, hero totals, a read-only Cast and a read-only
/// Adaptations list, with no graph edges and no lore. Every count, card and row was already limited to what the
/// requesting profile may see.
/// </summary>
public sealed class UniverseClientDetailDto
{
    [JsonPropertyName("qid")]
    public string Qid { get; init; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    /// <summary>Always <c>universe</c>; see <see cref="MediaEngine.Contracts.Collections.CollectionGroupTypes"/>.</summary>
    [JsonPropertyName("group_type")]
    public string GroupType { get; init; } = MediaEngine.Contracts.Collections.CollectionGroupTypes.Universe;

    /// <summary>
    /// Items across all member shelves that the profile may see: every numbered-run item and every "Also in this world"
    /// item, each counted once.
    /// </summary>
    [JsonPropertyName("item_count")]
    public int ItemCount { get; init; }

    /// <summary>Number of member shelves returned (<see cref="Shelves"/>), for the hero line "3 Series, 14 Works, 2 formats".</summary>
    [JsonPropertyName("series_count")]
    public int SeriesCount { get; init; }

    /// <summary>
    /// The distinct display kinds (Book, Movie, TV, …) of the visible items, in order of first appearance across the
    /// shelves; its length is the "formats" figure of the hero line.
    /// </summary>
    [JsonPropertyName("media_types")]
    public IReadOnlyList<string> MediaTypes { get; init; } = [];

    [JsonPropertyName("shelves")]
    public IReadOnlyList<UniverseMemberShelfDto> Shelves { get; init; } = [];

    /// <summary>
    /// Read-only Cast: one row per person across the characters they play in this Universe, most works first, at most
    /// 50. Only credits from works the profile may see are listed. Empty, never null.
    /// </summary>
    [JsonPropertyName("cast")]
    public IReadOnlyList<UniverseClientCastMemberDto> Cast { get; init; } = [];

    /// <summary>
    /// Read-only Adaptations: one row per adaptation relationship the Library records between things in this Universe
    /// that the profile may see, at most 50. Empty when no such relationship exists, never null.
    /// </summary>
    [JsonPropertyName("adaptations")]
    public IReadOnlyList<UniverseClientAdaptationDto> Adaptations { get; init; } = [];
}

/// <summary>A Series (or TV show) inside a Universe, shown as a display shelf.</summary>
public sealed class UniverseMemberShelfDto
{
    [JsonPropertyName("collection_id")]
    public Guid CollectionId { get; init; }

    /// <summary><c>show</c> for a TV show, otherwise <c>series</c>.</summary>
    [JsonPropertyName("group_type")]
    public string GroupType { get; init; } = MediaEngine.Contracts.Collections.CollectionGroupTypes.Series;

    /// <summary>
    /// All items of this shelf's own run (the numbered run and any unnumbered tail) that the profile may see;
    /// <c>shelf.items</c> holds the first of them. Supplementary, collected and broader-context members are not in
    /// this count; they are in <see cref="AlsoInThisWorldCount"/>.
    /// </summary>
    [JsonPropertyName("item_count")]
    public int ItemCount { get; init; }

    /// <summary>The Series run: <see cref="ItemCount"/> items, the first of them in <c>items</c>, in Series order.</summary>
    [JsonPropertyName("shelf")]
    public DisplayShelfDto Shelf { get; init; } = new(string.Empty, string.Empty, null, [], null);

    /// <summary>
    /// "Also in this world": the supplementary, collected and broader-context members of this Series, in the same Series
    /// order. Null when there are none (always null for a TV show).
    /// </summary>
    [JsonPropertyName("also_in_this_world")]
    public DisplayShelfDto? AlsoInThisWorld { get; init; }

    /// <summary>All "Also in this world" items of this Series the profile may see; <see cref="AlsoInThisWorld"/> lists the first of them.</summary>
    [JsonPropertyName("also_in_this_world_count")]
    public int AlsoInThisWorldCount { get; init; }
}

/// <summary>One person in a Universe's Cast: who they are and the characters they play there.</summary>
public sealed class UniverseClientCastMemberDto
{
    [JsonPropertyName("person_id")]
    public Guid PersonId { get; init; }

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("headshot_url")]
    public string? HeadshotUrl { get; init; }

    /// <summary>The characters this person plays in this Universe, distinct by QID and in alphabetical order of label.</summary>
    [JsonPropertyName("characters")]
    public IReadOnlyList<UniverseClientCastCharacterDto> Characters { get; init; } = [];

    /// <summary>The distinct works the profile may see that these credits come from.</summary>
    [JsonPropertyName("work_count")]
    public int WorkCount { get; init; }
}

/// <summary>A character a cast member plays: its Wikidata QID and name.</summary>
public sealed class UniverseClientCastCharacterDto
{
    [JsonPropertyName("qid")]
    public string Qid { get; init; } = string.Empty;

    [JsonPropertyName("label")]
    public string Label { get; init; } = string.Empty;
}

/// <summary>One adaptation relationship in a Universe, read-only: <c>qid</c> is "<c>relationship</c>" <c>source_qid</c>.</summary>
public sealed class UniverseClientAdaptationDto
{
    [JsonPropertyName("qid")]
    public string Qid { get; init; } = string.Empty;

    [JsonPropertyName("label")]
    public string Label { get; init; } = string.Empty;

    [JsonPropertyName("media_type")]
    public string? MediaType { get; init; }

    /// <summary>The human label of the relationship, for example "based on", "derivative of" or "inspired by".</summary>
    [JsonPropertyName("relationship")]
    public string Relationship { get; init; } = string.Empty;

    [JsonPropertyName("source_qid")]
    public string SourceQid { get; init; } = string.Empty;

    [JsonPropertyName("source_label")]
    public string SourceLabel { get; init; } = string.Empty;
}
