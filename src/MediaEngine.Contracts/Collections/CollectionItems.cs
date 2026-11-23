using System.Text.Json.Serialization;

namespace MediaEngine.Contracts.Collections;

public sealed class CollectionItemDto
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    [JsonPropertyName("work_id")]
    public Guid WorkId { get; set; }

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("creator")]
    public string? Creator { get; set; }

    [JsonPropertyName("media_type")]
    public string MediaType { get; set; } = string.Empty;

    [JsonPropertyName("cover_url")]
    public string? CoverUrl { get; set; }

    /// <summary>Where the item sits in the list, counted from zero. The list is already in Series order.</summary>
    [JsonPropertyName("sort_order")]
    public int SortOrder { get; set; }

    [JsonPropertyName("detail_route")]
    public string? DetailRoute { get; set; }

    /// <summary>
    /// How the Series holds the item: <c>MainSequence</c> (numbered, counted), <c>Unpositioned</c> (in the Series after
    /// the numbered run, unnumbered), or <c>Supplementary</c>, <c>CollectedContent</c> and <c>BroaderContext</c>
    /// ("Also in this world", listed after the Series run and never numbered with it).
    /// </summary>
    [JsonPropertyName("membership_scope")]
    public string MembershipScope { get; set; } = "MainSequence";

    /// <summary>The position as the source gives it, decimals intact ("1.5"); null when the source gives none.</summary>
    [JsonPropertyName("position_label")]
    public string? PositionLabel { get; set; }

    /// <summary>
    /// The item's representative media asset, set by the shared item read for the items of every collection. The paired-app
    /// items route sends it only for a playlist's songs (the file that plays the song); a row that stands for a whole album or
    /// show has none. Null when there is none, and left out of the answer when null.
    /// </summary>
    [JsonPropertyName("asset_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? AssetId { get; set; }

    /// <summary>The album a song belongs to. Read only for a playlist's items; left out of the answer when null.</summary>
    [JsonPropertyName("album")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Album { get; set; }

    /// <summary>The stored length of a song in seconds. Read only for a playlist's items; left out of the answer when null.</summary>
    [JsonPropertyName("duration_seconds")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? DurationSeconds { get; set; }
}

public sealed class CollectionResolvedItemDto
{
    [JsonPropertyName("entity_id")]
    public Guid EntityId { get; init; }

    [JsonPropertyName("title")]
    public string Title { get; init; } = "";

    [JsonPropertyName("creator")]
    public string? Creator { get; init; }

    [JsonPropertyName("media_type")]
    public string MediaType { get; init; } = "";

    [JsonPropertyName("cover_url")]
    public string? CoverUrl { get; init; }

    [JsonPropertyName("year")]
    public string? Year { get; init; }
}

public sealed class CollectionMediaLookupDto
{
    [JsonPropertyName("work_id")]
    public Guid WorkId { get; set; }

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("subtitle")]
    public string? Subtitle { get; set; }

    [JsonPropertyName("creator")]
    public string? Creator { get; set; }

    [JsonPropertyName("media_type")]
    public string MediaType { get; set; } = string.Empty;

    [JsonPropertyName("year")]
    public string? Year { get; set; }

    [JsonPropertyName("artwork_url")]
    public string? ArtworkUrl { get; set; }

    [JsonPropertyName("parent_context")]
    public string? ParentContext { get; set; }

    [JsonPropertyName("route")]
    public string? Route { get; set; }

    [JsonPropertyName("already_in_collection")]
    public bool AlreadyInCollection { get; set; }
}
