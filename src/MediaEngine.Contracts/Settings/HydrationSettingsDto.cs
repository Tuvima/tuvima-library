using System.Text.Json.Serialization;

namespace MediaEngine.Contracts.Settings;

public sealed class HydrationSettingsDto
{
    [JsonPropertyName("max_concurrent_retail_provider_jobs")]
    public int MaxConcurrentRetailProviderJobs { get; set; } = 4;

    [JsonPropertyName("max_concurrent_wikidata_jobs")]
    public int MaxConcurrentWikidataJobs { get; set; } = 2;

    [JsonPropertyName("max_concurrent_tmdb_artwork_jobs")]
    public int MaxConcurrentTmdbArtworkJobs { get; set; } = 1;

    [JsonPropertyName("max_concurrent_writeback_jobs")]
    public int MaxConcurrentWriteBackJobs { get; set; } = 1;

    [JsonPropertyName("stage1_timeout_seconds")]
    public int Stage1TimeoutSeconds { get; set; } = 45;

    [JsonPropertyName("quick_hydration_timeout_seconds")]
    public int QuickHydrationTimeoutSeconds { get; set; } = 1200;

    [JsonPropertyName("disambiguation_threshold")]
    public double DisambiguationThreshold { get; set; } = 0.7;

    [JsonPropertyName("auto_review_confidence_threshold")]
    public double AutoReviewConfidenceThreshold { get; set; } = 0.60;

    [JsonPropertyName("max_qid_candidates")]
    public int MaxQidCandidates { get; set; } = 5;

    [JsonPropertyName("skip_wikipedia_without_qid")]
    public bool SkipWikipediaWithoutQid { get; set; } = true;

    [JsonPropertyName("wikipedia_description_max_chars")]
    public int WikipediaDescriptionMaxChars { get; set; } = 1000;

    [JsonPropertyName("fictional_entity_enrichment_depth")]
    public int FictionalEntityEnrichmentDepth { get; set; } = 2;

    [JsonPropertyName("post_hydration_organize_threshold")]
    public double PostHydrationOrganizeThreshold { get; set; } = 0.70;

    [JsonPropertyName("collection_rollup_relationship_types")]
    public List<string> CollectionRollupRelationshipTypes { get; set; } =
        ["series", "franchise", "fictional_universe", "based_on"];

    [JsonPropertyName("two_pass_enabled")]
    public bool TwoPassEnabled { get; set; } = true;

    [JsonPropertyName("pass1_core_properties_only")]
    public bool Pass1CorePropertiesOnly { get; set; } = true;

    [JsonPropertyName("pass2_idle_delay_seconds")]
    public int Pass2IdleDelaySeconds { get; set; } = 10;

    [JsonPropertyName("pass2_rate_limit_ms")]
    public int Pass2RateLimitMs { get; set; } = 2000;

    [JsonPropertyName("pass2_stale_threshold_hours")]
    public int Pass2StaleThresholdHours { get; set; } = 24;

    [JsonPropertyName("pass2_batch_size")]
    public int Pass2BatchSize { get; set; } = 50;

    [JsonPropertyName("local_match_enabled")]
    public bool LocalMatchEnabled { get; set; } = true;

    [JsonPropertyName("local_match_fuzzy_threshold")]
    public double LocalMatchFuzzyThreshold { get; set; } = 0.95;

    [JsonPropertyName("retail_auto_accept_threshold")]
    public double RetailAutoAcceptThreshold { get; set; } = 0.90;

    [JsonPropertyName("retail_ambiguous_threshold")]
    public double RetailAmbiguousThreshold { get; set; } = 0.65;

    [JsonPropertyName("edition_aware_media_types")]
    public List<string> EditionAwareMediaTypes { get; set; } =
        ["Books", "Audiobooks", "Movies", "Comics", "Music"];

    [JsonPropertyName("fuzzy_match_weights")]
    public Dictionary<string, double> FuzzyMatchWeights { get; set; } = new()
    {
        ["title"] = 0.45,
        ["author"] = 0.35,
        ["year"] = 0.10,
        ["format"] = 0.10,
    };

    [JsonPropertyName("wikidata_batch_size")]
    public int WikidataBatchSize { get; set; } = 50;

    [JsonPropertyName("identity_retry_max_attempts")]
    public int IdentityRetryMaxAttempts { get; set; } = 5;

    [JsonPropertyName("identity_retry_base_delay_seconds")]
    public int IdentityRetryBaseDelaySeconds { get; set; } = 10;

    [JsonPropertyName("identity_retry_max_delay_seconds")]
    public int IdentityRetryMaxDelaySeconds { get; set; } = 300;

    [JsonPropertyName("identity_retry_jitter_min_ms")]
    public int IdentityRetryJitterMinMilliseconds { get; set; } = 250;

    [JsonPropertyName("identity_retry_jitter_max_ms")]
    public int IdentityRetryJitterMaxMilliseconds { get; set; } = 1750;

    [JsonPropertyName("fetch_temporal_qualifiers")]
    public bool FetchTemporalQualifiers { get; set; } = true;

    [JsonPropertyName("batch_sparql_size")]
    public int BatchSparqlSize { get; set; } = 50;

    [JsonPropertyName("lineage_depth")]
    public int LineageDepth { get; set; } = 2;

    [JsonPropertyName("stage3_enabled")]
    public bool Stage3Enabled { get; set; } = true;

    [JsonPropertyName("stage3_rate_limit_ms")]
    public int Stage3RateLimitMs { get; set; } = 3000;

    [JsonPropertyName("stage3_max_items_per_sweep")]
    public int Stage3MaxItemsPerSweep { get; set; } = 50;

    [JsonPropertyName("stage3_refresh_days")]
    public int Stage3RefreshDays { get; set; } = 90;

    [JsonPropertyName("person_refresh_days")]
    public int PersonRefreshDays { get; set; } = 90;

    [JsonPropertyName("series_manifest_refresh_days")]
    public int SeriesManifestRefreshDays { get; set; } = 90;

    [JsonPropertyName("stage3_max_depth")]
    public int Stage3MaxDepth { get; set; } = 2;

    [JsonPropertyName("timeline_retention_days")]
    public int TimelineRetentionDays { get; set; } = 365;
}
