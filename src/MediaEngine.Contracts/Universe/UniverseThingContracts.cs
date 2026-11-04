using System.Text.Json.Serialization;

namespace MediaEngine.Contracts.Universe;

/// <summary>
/// One thing of a Universe on its own page (Universe contents spec section 4.2): its picture or logo, name and kind, a short
/// description with its source, the Works you own that it appears in, and the things it is connected to. Everything is limited
/// to what the requesting profile may see.
/// </summary>
public sealed class UniverseThingDto
{
    [JsonPropertyName("id")]
    public Guid Id { get; init; }

    [JsonPropertyName("universe_qid")]
    public string UniverseQid { get; init; } = string.Empty;

    [JsonPropertyName("universe_label")]
    public string UniverseLabel { get; init; } = string.Empty;

    [JsonPropertyName("label")]
    public string Label { get; init; } = string.Empty;

    /// <summary>The section key of the kind: <c>characters</c>, <c>factions</c>, <c>places</c>, <c>events</c>, <c>objects</c>, <c>species</c> or <c>concepts</c>.</summary>
    [JsonPropertyName("kind_key")]
    public string KindKey { get; init; } = string.Empty;

    /// <summary>The kind as one thing is called on screen ("Character", "Faction", "Place").</summary>
    [JsonPropertyName("kind_label")]
    public string KindLabel { get; init; } = string.Empty;

    /// <summary>The Wikidata ID; null for a thing added by hand.</summary>
    [JsonPropertyName("qid")]
    public string? Qid { get; init; }

    [JsonPropertyName("added_by_user")]
    public bool AddedByUser { get; init; }

    [JsonPropertyName("picture_url")]
    public string? PictureUrl { get; init; }

    [JsonPropertyName("picture_source")]
    public string? PictureSource { get; init; }

    /// <summary>The logo's address, for the kinds that have a logo (Factions, Places, Objects, Species) when one exists.</summary>
    [JsonPropertyName("logo_url")]
    public string? LogoUrl { get; init; }

    /// <summary>The short description; null when there is none, or when <see cref="DetailsHidden"/> is true.</summary>
    [JsonPropertyName("description")]
    public string? Description { get; init; }

    /// <summary>Where the description comes from: <c>wikidata</c>, <c>fandom</c> or <c>you</c>.</summary>
    [JsonPropertyName("description_source")]
    public string? DescriptionSource { get; init; }

    /// <summary>Other names the person added.</summary>
    [JsonPropertyName("aliases")]
    public IReadOnlyList<string> Aliases { get; init; } = [];

    /// <summary>
    /// True when the Library cannot place the thing within what the profile has watched or read, so its details wait
    /// ("Hidden until you reach it"). Only the name, kind and picture are given.
    /// </summary>
    [JsonPropertyName("details_hidden")]
    public bool DetailsHidden { get; init; }

    /// <summary>The Works the profile owns that the thing appears in, earliest first. Empty while <see cref="DetailsHidden"/>.</summary>
    [JsonPropertyName("appears_in")]
    public IReadOnlyList<UniverseThingWorkDto> AppearsIn { get; init; } = [];

    /// <summary>Connected things in groups ("Member of", "Located in", "Took place at"), only the groups that have something the profile may see.</summary>
    [JsonPropertyName("related")]
    public IReadOnlyList<UniverseThingRelatedGroupDto> Related { get; init; } = [];
}

/// <summary>A Work a thing appears in.</summary>
public sealed class UniverseThingWorkDto
{
    [JsonPropertyName("work_qid")]
    public string WorkQid { get; init; } = string.Empty;

    [JsonPropertyName("label")]
    public string Label { get; init; } = string.Empty;

    /// <summary>The format of the Work ("Books", "Movies", "TV"), when known.</summary>
    [JsonPropertyName("media_type")]
    public string? MediaType { get; init; }

    [JsonPropertyName("cover_url")]
    public string? CoverUrl { get; init; }

    /// <summary>The part the thing plays there ("Protagonist", "Mentioned"), when known.</summary>
    [JsonPropertyName("role")]
    public string? Role { get; init; }
}

/// <summary>One group of connected things.</summary>
public sealed class UniverseThingRelatedGroupDto
{
    /// <summary>A stable key such as <c>member_of</c>, <c>members</c>, <c>located_in</c>, <c>belongs_to</c>.</summary>
    [JsonPropertyName("key")]
    public string Key { get; init; } = string.Empty;

    /// <summary>The group's heading on screen.</summary>
    [JsonPropertyName("label")]
    public string Label { get; init; } = string.Empty;

    [JsonPropertyName("count")]
    public int Count { get; init; }

    [JsonPropertyName("things")]
    public IReadOnlyList<UniverseThingLinkDto> Things { get; init; } = [];
}

/// <summary>A connected thing, as a small tile that opens its own page.</summary>
public sealed class UniverseThingLinkDto
{
    [JsonPropertyName("id")]
    public Guid Id { get; init; }

    [JsonPropertyName("label")]
    public string Label { get; init; } = string.Empty;

    /// <summary>The section key of the thing's kind, so the tile can be shaped and labeled for it.</summary>
    [JsonPropertyName("kind_key")]
    public string KindKey { get; init; } = string.Empty;

    [JsonPropertyName("picture_url")]
    public string? PictureUrl { get; init; }

    [JsonPropertyName("logo_url")]
    public string? LogoUrl { get; init; }

    [JsonPropertyName("added_by_user")]
    public bool AddedByUser { get; init; }
}
