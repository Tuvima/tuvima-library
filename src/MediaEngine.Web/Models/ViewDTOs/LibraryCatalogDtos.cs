using System.Text.Json.Serialization;

namespace MediaEngine.Web.Models.ViewDTOs;

public sealed class UniverseSummaryViewModel
{
    [JsonPropertyName("universe_status")]
    public string UniverseStatus { get; set; } = "unlinked";

    [JsonPropertyName("universe_name")]
    public string? UniverseName { get; set; }

    [JsonPropertyName("universe_qid")]
    public string? UniverseQid { get; set; }

    [JsonPropertyName("narrative_root_qid")]
    public string? NarrativeRootQid { get; set; }

    [JsonPropertyName("stage3_status")]
    public string Stage3Status { get; set; } = "pending";

    [JsonPropertyName("stage3_enriched_at")]
    public DateTimeOffset? Stage3EnrichedAt { get; set; }

    [JsonPropertyName("entity_count")]
    public int EntityCount { get; set; }

    [JsonPropertyName("relationship_count")]
    public int RelationshipCount { get; set; }

    [JsonPropertyName("portrait_count")]
    public int PortraitCount { get; set; }
}

/// <summary>Full detail for expanded row.</summary>
public sealed class LibraryItemDetailViewModel
{
    [JsonPropertyName("entity_id")]
    public Guid EntityId { get; set; }

    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    [JsonPropertyName("year")]
    public string? Year { get; set; }

    [JsonPropertyName("media_type")]
    public string MediaType { get; set; } = "";

    [JsonPropertyName("cover_url")]
    public string? CoverUrl { get; set; }

    [JsonPropertyName("background_url")]
    public string? BackgroundUrl { get; set; }

    [JsonPropertyName("banner_url")]
    public string? BannerUrl { get; set; }

    [JsonPropertyName("hero_url")]
    public string? HeroUrl { get; set; }

    [JsonPropertyName("confidence")]
    public double Confidence { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = "Auto";

    [JsonPropertyName("match_source")]
    public string? MatchSource { get; set; }

    [JsonPropertyName("match_method")]
    public string? MatchMethod { get; set; }

    [JsonPropertyName("author")]
    public string? Author { get; set; }

    [JsonPropertyName("album")]
    public string? Album { get; set; }

    [JsonPropertyName("artist")]
    public string? Artist { get; set; }

    [JsonPropertyName("director")]
    public string? Director { get; set; }

    [JsonPropertyName("writer")]
    public string? Writer { get; set; }

    [JsonPropertyName("cast")]
    public string? Cast { get; set; }

    [JsonPropertyName("language")]
    public string? Language { get; set; }

    [JsonPropertyName("genre")]
    public string? Genre { get; set; }

    [JsonPropertyName("runtime")]
    public string? Runtime { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("tagline")]
    public string? Tagline { get; set; }

    [JsonPropertyName("series")]
    public string? Series { get; set; }

    [JsonPropertyName("series_position")]
    public string? SeriesPosition { get; set; }

    [JsonPropertyName("show_name")]
    public string? ShowName { get; set; }

    [JsonPropertyName("season_number")]
    public string? SeasonNumber { get; set; }

    [JsonPropertyName("episode_number")]
    public string? EpisodeNumber { get; set; }

    [JsonPropertyName("episode_title")]
    public string? EpisodeTitle { get; set; }

    [JsonPropertyName("release_date")]
    public string? ReleaseDate { get; set; }

    [JsonPropertyName("narrator")]
    public string? Narrator { get; set; }

    [JsonPropertyName("rating")]
    public string? Rating { get; set; }

    [JsonPropertyName("wikidata_qid")]
    public string? WikidataQid { get; set; }

    [JsonPropertyName("wikidata_status")]
    public string? WikidataStatus { get; set; }

    [JsonPropertyName("missing_universe")]
    public bool MissingUniverse => WikidataStatus is "missing" or "manual";

    [JsonPropertyName("file_name")]
    public string? FileName { get; set; }

    [JsonPropertyName("file_path")]
    public string? FilePath { get; set; }

    [JsonPropertyName("file_size_bytes")]
    public long? FileSizeBytes { get; set; }

    [JsonPropertyName("content_hash")]
    public string? ContentHash { get; set; }

    [JsonPropertyName("review_item_id")]
    public Guid? ReviewItemId { get; set; }

    [JsonPropertyName("review_trigger")]
    public string? ReviewTrigger { get; set; }

    [JsonPropertyName("review_detail")]
    public string? ReviewDetail { get; set; }

    [JsonPropertyName("candidates_json")]
    public string? CandidatesJson { get; set; }

    [JsonPropertyName("has_user_locks")]
    public bool HasUserLocks { get; set; }

    [JsonPropertyName("match_level")]
    public string MatchLevel { get; set; } = "work";

    [JsonPropertyName("canonical_values")]
    public List<LibraryItemCanonicalValueDto> CanonicalValues { get; set; } = [];

    [JsonPropertyName("claim_history")]
    public List<LibraryItemClaimRecordDto> ClaimHistory { get; set; } = [];

    [JsonPropertyName("bridge_ids")]
    public Dictionary<string, string> BridgeIds { get; set; } = [];

    [JsonPropertyName("pipeline_step")]
    public string PipelineStep { get; set; } = "Retail";

    [JsonPropertyName("library_visibility")]
    public string LibraryVisibility { get; set; } = "hidden";

    [JsonPropertyName("is_ready_for_library")]
    public bool IsReadyForLibrary { get; set; }

    [JsonPropertyName("artwork_state")]
    public string ArtworkState { get; set; } = "pending";

    [JsonPropertyName("artwork_source")]
    public string? ArtworkSource { get; set; }

    [JsonPropertyName("artwork_settled_at")]
    public DateTimeOffset? ArtworkSettledAt { get; set; }

    [JsonPropertyName("universe_summary")]
    public UniverseSummaryViewModel? UniverseSummary { get; set; }

    [JsonPropertyName("playback_summary")]
    public PlaybackTechnicalSummaryViewModel? PlaybackSummary { get; set; }
}

/// <summary>Canonical value with conflict info.</summary>
public sealed class LibraryItemCanonicalValueDto
{
    [JsonPropertyName("key")]
    public string Key { get; set; } = "";

    [JsonPropertyName("value")]
    public string Value { get; set; } = "";

    [JsonPropertyName("is_conflicted")]
    public bool IsConflicted { get; set; }

    [JsonPropertyName("winning_provider_id")]
    public string? WinningProviderId { get; set; }

    [JsonPropertyName("needs_review")]
    public bool NeedsReview { get; set; }

    [JsonPropertyName("last_scored_at")]
    public DateTimeOffset LastScoredAt { get; set; }
}

/// <summary>A single claim from voting history.</summary>
public sealed class LibraryItemClaimRecordDto
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    [JsonPropertyName("claim_key")]
    public string ClaimKey { get; set; } = "";

    [JsonPropertyName("claim_value")]
    public string ClaimValue { get; set; } = "";

    [JsonPropertyName("provider_id")]
    public Guid ProviderId { get; set; }

    [JsonPropertyName("confidence")]
    public double Confidence { get; set; }

    [JsonPropertyName("is_user_locked")]
    public bool IsUserLocked { get; set; }

    [JsonPropertyName("claimed_at")]
    public DateTimeOffset ClaimedAt { get; set; }
}

/// <summary>Response from batch libraryItem operations.</summary>
public sealed class BatchLibraryItemResponse
{
    [JsonPropertyName("processed_count")]
    public int ProcessedCount { get; set; }

    [JsonPropertyName("total_requested")]
    public int TotalRequested { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = "";
}

/// <summary>A single entry in the processing history timeline for a libraryItem item.</summary>
public sealed class LibraryItemHistoryDto
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("entity_id")]
    public Guid EntityId { get; set; }

    [JsonPropertyName("occurred_at")]
    public DateTimeOffset OccurredAt { get; set; }

    [JsonPropertyName("event_type")]
    public string EventType { get; set; } = "";

    [JsonPropertyName("label")]
    public string Label { get; set; } = "";

    [JsonPropertyName("detail")]
    public string? Detail { get; set; }

    [JsonPropertyName("category")]
    public string Category { get; set; } = "metadata";

    [JsonPropertyName("actor_label")]
    public string ActorLabel { get; set; } = "System";
}

/// <summary>A group member or parent group for person-detail projections.</summary>
public sealed class GroupMemberView
{
    public Guid Id { get; init; }
    public string Name { get; init; } = "";
    public string? DateRange { get; init; }

    public GroupMemberView(Guid id, string name, string? dateRange = null)
    {
        Id = id;
        Name = name;
        DateRange = dateRange;
    }
}

/// <summary>Media type presence counts for a single person.</summary>
public sealed class PersonPresenceDto
{
    [JsonPropertyName("person_id")]
    public string PersonId { get; set; } = "";

    [JsonPropertyName("counts")]
    public Dictionary<string, int> Counts { get; set; } = new();

    /// <summary>Total media items this person appears in.</summary>
    public int Total => Counts.Values.Sum();
}
