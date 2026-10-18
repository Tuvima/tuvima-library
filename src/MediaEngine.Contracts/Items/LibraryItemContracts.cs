using System.Text.Json.Serialization;

namespace MediaEngine.Contracts.Items;

public sealed class LibraryItemDetailDto
{
    [JsonPropertyName("entity_id")] public Guid EntityId { get; init; }
    [JsonPropertyName("title")] public string Title { get; init; } = string.Empty;
    [JsonPropertyName("year")] public string? Year { get; init; }
    [JsonPropertyName("media_type")] public string MediaType { get; init; } = string.Empty;
    [JsonPropertyName("cover_url")] public string? CoverUrl { get; init; }
    [JsonPropertyName("background_url")] public string? BackgroundUrl { get; init; }
    [JsonPropertyName("banner_url")] public string? BannerUrl { get; init; }
    [JsonPropertyName("hero_url")] public string? HeroUrl { get; init; }
    [JsonPropertyName("confidence")] public double Confidence { get; init; }
    [JsonPropertyName("status")] public string Status { get; init; } = "Identified";
    [JsonPropertyName("match_source")] public string? MatchSource { get; init; }
    [JsonPropertyName("match_method")] public string? MatchMethod { get; init; }
    [JsonPropertyName("retail_provider_name")] public string? RetailProviderName { get; init; }
    [JsonPropertyName("retail_provider_item_id")] public string? RetailProviderItemId { get; init; }
    [JsonPropertyName("author")] public string? Author { get; init; }
    [JsonPropertyName("director")] public string? Director { get; init; }
    [JsonPropertyName("artist")] public string? Artist { get; init; }
    [JsonPropertyName("album")] public string? Album { get; init; }
    [JsonPropertyName("composer")] public string? Composer { get; init; }
    [JsonPropertyName("illustrator")] public string? Illustrator { get; init; }
    [JsonPropertyName("writer")] public string? Writer { get; init; }
    [JsonPropertyName("cast")] public string? Cast { get; init; }
    [JsonPropertyName("language")] public string? Language { get; init; }
    [JsonPropertyName("genre")] public string? Genre { get; init; }
    [JsonPropertyName("runtime")] public string? Runtime { get; init; }
    [JsonPropertyName("description")] public string? Description { get; init; }
    [JsonPropertyName("tagline")] public string? Tagline { get; init; }
    [JsonPropertyName("series")] public string? Series { get; init; }
    [JsonPropertyName("series_position")] public string? SeriesPosition { get; init; }
    [JsonPropertyName("show_name")] public string? ShowName { get; init; }
    [JsonPropertyName("season_number")] public string? SeasonNumber { get; init; }
    [JsonPropertyName("episode_number")] public string? EpisodeNumber { get; init; }
    [JsonPropertyName("episode_title")] public string? EpisodeTitle { get; init; }
    [JsonPropertyName("release_date")] public string? ReleaseDate { get; init; }
    [JsonPropertyName("narrator")] public string? Narrator { get; init; }
    [JsonPropertyName("rating")] public string? Rating { get; init; }
    [JsonPropertyName("wikidata_qid")] public string? WikidataQid { get; init; }
    [JsonPropertyName("playback_summary")] public PlaybackTechnicalSummaryDto? PlaybackSummary { get; init; }
    [JsonPropertyName("wikidata_status")] public string? WikidataStatus { get; init; }
    [JsonPropertyName("missing_universe")] public bool MissingUniverse => WikidataStatus is "missing" or "manual";
    [JsonPropertyName("file_name")] public string? FileName { get; init; }
    [JsonPropertyName("file_path")] public string? FilePath { get; init; }
    [JsonPropertyName("file_size_bytes")] public long? FileSizeBytes { get; init; }
    [JsonPropertyName("content_hash")] public string? ContentHash { get; init; }
    [JsonPropertyName("review_item_id")] public Guid? ReviewItemId { get; init; }
    [JsonPropertyName("review_trigger")] public string? ReviewTrigger { get; init; }
    [JsonPropertyName("review_detail")] public string? ReviewDetail { get; init; }
    [JsonPropertyName("candidates_json")] public string? CandidatesJson { get; init; }
    [JsonPropertyName("has_user_locks")] public bool HasUserLocks { get; init; }
    [JsonPropertyName("match_level")] public string MatchLevel { get; init; } = "work";
    [JsonPropertyName("canonical_values")] public List<LibraryItemCanonicalValueDto> CanonicalValues { get; init; } = [];
    [JsonPropertyName("claim_history")] public List<LibraryItemClaimRecordDto> ClaimHistory { get; init; } = [];
    [JsonPropertyName("bridge_ids")] public Dictionary<string, string> BridgeIds { get; init; } = [];
    [JsonPropertyName("pipeline_step")] public string PipelineStep { get; init; } = "Retail";
    [JsonPropertyName("library_visibility")] public string LibraryVisibility { get; init; } = "hidden";
    [JsonPropertyName("is_ready_for_library")] public bool IsReadyForLibrary { get; init; }
    [JsonPropertyName("artwork_state")] public string ArtworkState { get; init; } = "pending";
    [JsonPropertyName("artwork_source")] public string? ArtworkSource { get; init; }
    [JsonPropertyName("artwork_settled_at")] public DateTimeOffset? ArtworkSettledAt { get; init; }
    [JsonPropertyName("universe_summary")] public LibraryItemUniverseSummaryDto? UniverseSummary { get; init; }
}

public sealed class PlaybackTechnicalSummaryDto
{
    [JsonPropertyName("video_resolution_label")] public string? VideoResolutionLabel { get; init; }
    [JsonPropertyName("video_codec")] public string? VideoCodec { get; init; }
    [JsonPropertyName("audio_language")] public string? AudioLanguage { get; init; }
    [JsonPropertyName("audio_codec")] public string? AudioCodec { get; init; }
    [JsonPropertyName("audio_channels")] public string? AudioChannels { get; init; }
    [JsonPropertyName("subtitle_summary")] public string? SubtitleSummary { get; init; }
    [JsonPropertyName("audio_languages")] public IReadOnlyList<string> AudioLanguages { get; init; } = [];
    [JsonPropertyName("subtitle_languages")] public IReadOnlyList<string> SubtitleLanguages { get; init; } = [];
}

public sealed class LibraryItemUniverseSummaryDto
{
    [JsonPropertyName("universe_status")] public string UniverseStatus { get; init; } = "unlinked";
    [JsonPropertyName("universe_name")] public string? UniverseName { get; init; }
    [JsonPropertyName("universe_qid")] public string? UniverseQid { get; init; }
    [JsonPropertyName("narrative_root_qid")] public string? NarrativeRootQid { get; init; }
    [JsonPropertyName("stage3_status")] public string Stage3Status { get; init; } = "pending";
    [JsonPropertyName("stage3_enriched_at")] public DateTimeOffset? Stage3EnrichedAt { get; init; }
    [JsonPropertyName("entity_count")] public int EntityCount { get; init; }
    [JsonPropertyName("relationship_count")] public int RelationshipCount { get; init; }
    [JsonPropertyName("portrait_count")] public int PortraitCount { get; init; }
}

public sealed class LibraryItemCanonicalValueDto
{
    [JsonPropertyName("key")] public string Key { get; init; } = string.Empty;
    [JsonPropertyName("value")] public string Value { get; init; } = string.Empty;
    [JsonPropertyName("is_conflicted")] public bool IsConflicted { get; init; }
    [JsonPropertyName("winning_provider_id")] public string? WinningProviderId { get; init; }
    [JsonPropertyName("needs_review")] public bool NeedsReview { get; init; }
    [JsonPropertyName("last_scored_at")] public DateTimeOffset LastScoredAt { get; init; }
}

public sealed class LibraryItemClaimRecordDto
{
    [JsonPropertyName("id")] public Guid Id { get; init; }
    [JsonPropertyName("claim_key")] public string ClaimKey { get; init; } = string.Empty;
    [JsonPropertyName("claim_value")] public string ClaimValue { get; init; } = string.Empty;
    [JsonPropertyName("provider_id")] public Guid ProviderId { get; init; }
    [JsonPropertyName("confidence")] public double Confidence { get; init; }
    [JsonPropertyName("is_user_locked")] public bool IsUserLocked { get; init; }
    [JsonPropertyName("claimed_at")] public DateTimeOffset ClaimedAt { get; init; }
}

public sealed class LibraryItemHistoryDto
{
    [JsonPropertyName("id")] public string Id { get; init; } = string.Empty;
    [JsonPropertyName("entity_id")] public Guid EntityId { get; init; }
    [JsonPropertyName("occurred_at")] public DateTimeOffset OccurredAt { get; init; }
    [JsonPropertyName("event_type")] public string EventType { get; init; } = string.Empty;
    [JsonPropertyName("label")] public string Label { get; init; } = string.Empty;
    [JsonPropertyName("detail")] public string? Detail { get; init; }
    [JsonPropertyName("category")] public string Category { get; init; } = "metadata";
    [JsonPropertyName("actor_label")] public string ActorLabel { get; init; } = "System";
}
