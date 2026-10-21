using System.Text.Json.Serialization;

namespace MediaEngine.Contracts.Metadata;

/// <summary>One Series shelf as the editor's Matching Series block shows it.</summary>
public sealed class SeriesPlacementShelfDto
{
    [JsonPropertyName("collection_id")]
    public Guid CollectionId { get; set; }

    [JsonPropertyName("label")]
    public string Label { get; set; } = string.Empty;

    [JsonPropertyName("series_qid")]
    public string? SeriesQid { get; set; }

    [JsonPropertyName("media_type")]
    public string MediaType { get; set; } = string.Empty;

    /// <summary>The scope the Series gives the work: <c>MainSequence</c>, <c>Supplementary</c>, <c>CollectedContent</c>, <c>BroaderContext</c> or <c>Unpositioned</c>.</summary>
    [JsonPropertyName("scope")]
    public string Scope { get; set; } = string.Empty;

    /// <summary>The work's position in the Series (for example <c>3</c> or <c>2.5</c>), when the Series gives one.</summary>
    [JsonPropertyName("position_label")]
    public string? PositionLabel { get; set; }
}

/// <summary>What the editor's Matching Series block needs for one work.</summary>
public sealed class SeriesPlacementDto
{
    [JsonPropertyName("work_id")]
    public Guid WorkId { get; set; }

    [JsonPropertyName("media_type")]
    public string MediaType { get; set; } = string.Empty;

    /// <summary>False for Works whose media type has no Series shelves (TV episodes, music tracks); the block is then hidden.</summary>
    [JsonPropertyName("supports_series")]
    public bool SupportsSeries { get; set; }

    /// <summary>The Series the work is on now; null when it is on none.</summary>
    [JsonPropertyName("current")]
    public SeriesPlacementShelfDto? Current { get; set; }

    /// <summary>True when a person chose the placement (including "Not in a Series") and the Engine keeps it.</summary>
    [JsonPropertyName("is_person_choice")]
    public bool IsPersonChoice { get; set; }

    /// <summary>The Series whose lists name this work.</summary>
    [JsonPropertyName("listed")]
    public List<SeriesPlacementShelfDto> Listed { get; set; } = [];

    /// <summary>Other Series of the same media type matching the search, a bounded list.</summary>
    [JsonPropertyName("candidates")]
    public List<SeriesPlacementShelfDto> Candidates { get; set; } = [];
}

/// <summary>Body of <c>PUT /metadata/{entityId}/series-placement</c>.</summary>
public sealed class SeriesPlacementChoiceRequestDto
{
    /// <summary>The Series shelf to put the work on; null means "Not in a Series". Ignored when <see cref="Locked"/> is false.</summary>
    [JsonPropertyName("collection_id")]
    public Guid? CollectionId { get; set; }

    /// <summary>True saves the choice as a lock the Engine always respects; false lets the Library decide again.</summary>
    [JsonPropertyName("locked")]
    public bool Locked { get; set; }
}
