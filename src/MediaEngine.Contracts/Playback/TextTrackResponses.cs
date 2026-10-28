namespace MediaEngine.Contracts.Playback;

/// <summary>A lyrics or subtitle track available for a media asset.</summary>
public sealed class TextTrackDto
{
    [global::System.Text.Json.Serialization.JsonPropertyName("id")]
    public Guid Id { get; set; }

    [global::System.Text.Json.Serialization.JsonPropertyName("kind")]
    public string Kind { get; set; } = string.Empty;

    [global::System.Text.Json.Serialization.JsonPropertyName("language")]
    public string Language { get; set; } = "und";

    [global::System.Text.Json.Serialization.JsonPropertyName("provider")]
    public string Provider { get; set; } = string.Empty;

    [global::System.Text.Json.Serialization.JsonPropertyName("confidence")]
    public double Confidence { get; set; }

    [global::System.Text.Json.Serialization.JsonPropertyName("sourceFormat")]
    public string SourceFormat { get; set; } = string.Empty;

    [global::System.Text.Json.Serialization.JsonPropertyName("normalizedFormat")]
    public string NormalizedFormat { get; set; } = string.Empty;

    [global::System.Text.Json.Serialization.JsonPropertyName("timingMode")]
    public string TimingMode { get; set; } = string.Empty;

    [global::System.Text.Json.Serialization.JsonPropertyName("isHearingImpaired")]
    public bool IsHearingImpaired { get; set; }

    [global::System.Text.Json.Serialization.JsonPropertyName("isPreferred")]
    public bool IsPreferred { get; set; }

    [global::System.Text.Json.Serialization.JsonPropertyName("isUserOwned")]
    public bool IsUserOwned { get; set; }

    [global::System.Text.Json.Serialization.JsonPropertyName("isLocallyExported")]
    public bool IsLocallyExported { get; set; }

    [global::System.Text.Json.Serialization.JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;
}

public sealed record PreferredTextTrackResponse
{
    public Guid asset_id { get; init; }
    public Guid track_id { get; init; }
    public bool preferred { get; init; }
}

/// <summary>
/// Response for <c>POST /stream/{assetId}/text-tracks/refresh</c>. Property names (including
/// snake_case spelling) retain the original fields and add an explicit, truthful outcome
/// so editor clients can distinguish an update from an empty provider result.
/// </summary>
public sealed record RefreshTextTracksResponse
{
    public Guid? operation_id { get; init; }
    public Guid asset_id { get; init; }
    public string enrichment_type { get; init; } = string.Empty;
    public bool refreshed { get; init; }
    public string status { get; init; } = string.Empty;
    public int before_count { get; init; }
    public int after_count { get; init; }
    public Guid? track_id { get; init; }
    public string message { get; init; } = string.Empty;
}

/// <summary>One result a source offers for an item's lyrics or subtitles, for "Not right? Find another".</summary>
public sealed record TextTrackAlternativeDto
{
    public string provider { get; init; } = string.Empty;
    public string provider_name { get; init; } = string.Empty;
    public string source_id { get; init; } = string.Empty;
    public string language { get; init; } = "und";
    public bool is_timed { get; init; }
    public double confidence { get; init; }
    public double? duration_match_score { get; init; }
    public bool is_hearing_impaired { get; init; }
    public string? release_name { get; init; }
    public string? preview { get; init; }
    public bool is_current { get; init; }
}

/// <summary>Response for <c>GET /stream/{assetId}/text-tracks/alternatives</c>. The list is asked of the sources live and is never stored.</summary>
public sealed record TextTrackAlternativesResponse
{
    public Guid asset_id { get; init; }
    public string kind { get; init; } = string.Empty;

    /// <summary><c>Found</c>, <c>NoneFound</c>, <c>NoSourceOn</c>, <c>NotAllowed</c>, <c>Blocked</c>, <c>Unavailable</c>, <c>AssetMissing</c> or <c>Unsupported</c>.</summary>
    public string status { get; init; } = string.Empty;
    public string message { get; init; } = string.Empty;
    public IReadOnlyList<TextTrackAlternativeDto> items { get; init; } = [];
}

/// <summary>Body of <c>POST /stream/{assetId}/text-tracks/alternatives/choose</c>.</summary>
public sealed record ChooseTextTrackAlternativeRequest
{
    public string kind { get; init; } = string.Empty;
    public string provider { get; init; } = string.Empty;
    public string source_id { get; init; } = string.Empty;
}

/// <summary>Response for choosing a result or marking one wrong.</summary>
public sealed record TextTrackChoiceResponse
{
    public Guid asset_id { get; init; }
    public string status { get; init; } = string.Empty;
    public string message { get; init; } = string.Empty;

    /// <summary>The track now in use, or null when none is.</summary>
    public Guid? track_id { get; init; }
}

/// <summary>One action a switched-on plugin or source offers on the player for an item, to the person asking.</summary>
public sealed record PlayerPluginActionDto
{
    public string plugin_id { get; init; } = string.Empty;
    public string plugin_name { get; init; } = string.Empty;
    public string action_id { get; init; } = string.Empty;

    /// <summary>What it does: <c>find_lyrics</c>, <c>find_subtitles</c> or <c>rescan_skip_markers</c>.</summary>
    public string kind { get; init; } = string.Empty;
    public string label { get; init; } = string.Empty;

    /// <summary><c>player_overflow</c>, <c>player_core</c> or <c>panel</c>.</summary>
    public string slot { get; init; } = string.Empty;
    public int priority { get; init; }

    /// <summary>True when it changes metadata or files, which makes it desktop-only.</summary>
    public bool edits { get; init; }
}
