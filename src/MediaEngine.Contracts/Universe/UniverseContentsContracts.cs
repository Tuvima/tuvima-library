using System.Text.Json.Serialization;

namespace MediaEngine.Contracts.Universe;

/// <summary>
/// What a Universe holds besides its Works (Universe contents spec): up to seven kinds of thing, each a rail of picture tiles,
/// and a Timeline. A section is present only when it has something the requesting profile may see.
/// </summary>
public sealed class UniverseContentsDto
{
    /// <summary>The non-empty sections, in the fixed order Characters, Factions, Places, Events, Objects, Species, Concepts.</summary>
    [JsonPropertyName("sections")]
    public IReadOnlyList<UniverseContentsSectionDto> Sections { get; init; } = [];

    /// <summary>The Timeline over Events and dated first appearances; null when fewer than three dated stops exist.</summary>
    [JsonPropertyName("timeline")]
    public UniverseTimelineDto? Timeline { get; init; }

    /// <summary>Things left out so the profile is not spoiled (spoiler level "Up to where I am"); only the number is told.</summary>
    [JsonPropertyName("hidden_count")]
    public int HiddenCount { get; init; }
}

/// <summary>One kind of thing in a Universe, as a rail.</summary>
public sealed class UniverseContentsSectionDto
{
    /// <summary><c>characters</c>, <c>factions</c>, <c>places</c>, <c>events</c>, <c>objects</c>, <c>species</c> or <c>concepts</c>.</summary>
    [JsonPropertyName("key")]
    public string Key { get; init; } = string.Empty;

    /// <summary>The name shown on screen ("Factions", "Places").</summary>
    [JsonPropertyName("label")]
    public string Label { get; init; } = string.Empty;

    /// <summary>How many the profile may see; can exceed <see cref="Tiles"/> when the rail is capped.</summary>
    [JsonPropertyName("count")]
    public int Count { get; init; }

    [JsonPropertyName("tiles")]
    public IReadOnlyList<UniverseContentsTileDto> Tiles { get; init; } = [];
}

/// <summary>One thing in a rail: a picture (or logo) tile.</summary>
public sealed class UniverseContentsTileDto
{
    [JsonPropertyName("id")]
    public Guid Id { get; init; }

    [JsonPropertyName("label")]
    public string Label { get; init; } = string.Empty;

    /// <summary>The Wikidata ID; null for a thing added by hand.</summary>
    [JsonPropertyName("qid")]
    public string? Qid { get; init; }

    /// <summary>The main picture's address, or null when the thing has none yet (the tile shows its initial).</summary>
    [JsonPropertyName("picture_url")]
    public string? PictureUrl { get; init; }

    /// <summary>The logo's address; set for the kinds that have a logo tile (Factions and Objects) when one exists.</summary>
    [JsonPropertyName("logo_url")]
    public string? LogoUrl { get; init; }

    /// <summary>True for a thing a person added by hand ("Added by you").</summary>
    [JsonPropertyName("added_by_user")]
    public bool AddedByUser { get; init; }

    /// <summary>True when the Library cannot place the thing within what the profile has watched or read: shown by name only.</summary>
    [JsonPropertyName("no_plot_details")]
    public bool NoPlotDetails { get; init; }

    /// <summary>Where the main picture came from: <c>wikidata</c>, <c>fandom</c> or <c>you</c>; null when there is no picture.</summary>
    [JsonPropertyName("picture_source")]
    public string? PictureSource { get; init; }
}

/// <summary>The Timeline strip: stops in story order or release order.</summary>
public sealed class UniverseTimelineDto
{
    public const string StoryOrder = "story";
    public const string ReleaseOrder = "release";

    /// <summary><c>story</c> when enough stops carry a story date, otherwise <c>release</c>.</summary>
    [JsonPropertyName("default_order")]
    public string DefaultOrder { get; init; } = ReleaseOrder;

    /// <summary>Whether the story-order toggle can be offered (at least three stops carry a story date).</summary>
    [JsonPropertyName("story_order_available")]
    public bool StoryOrderAvailable { get; init; }

    /// <summary>All dated stops, listed in <see cref="DefaultOrder"/>. Story order shows only the stops that have a <see cref="UniverseTimelineStopDto.StoryYear"/>.</summary>
    [JsonPropertyName("stops")]
    public IReadOnlyList<UniverseTimelineStopDto> Stops { get; init; } = [];
}

/// <summary>One stop of the Timeline.</summary>
public sealed class UniverseTimelineStopDto
{
    [JsonPropertyName("id")]
    public Guid Id { get; init; }

    [JsonPropertyName("label")]
    public string Label { get; init; } = string.Empty;

    /// <summary>The kind key of the thing ("events", "characters", ...).</summary>
    [JsonPropertyName("kind")]
    public string Kind { get; init; } = string.Empty;

    [JsonPropertyName("picture_url")]
    public string? PictureUrl { get; init; }

    /// <summary>The story year when known (negative before year 1); null when only the release order places it.</summary>
    [JsonPropertyName("story_year")]
    public double? StoryYear { get; init; }

    /// <summary>The year of the earliest owned Work the stop appears in; null when unknown.</summary>
    [JsonPropertyName("release_year")]
    public int? ReleaseYear { get; init; }

    /// <summary>Label of the Work the stop is first seen in, when known.</summary>
    [JsonPropertyName("work_label")]
    public string? WorkLabel { get; init; }
}
