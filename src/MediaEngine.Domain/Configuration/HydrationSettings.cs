using System.Text.Json.Serialization;

namespace MediaEngine.Domain.Configuration;

/// <summary>
/// Configuration for the hydration pipeline.
///
/// Loaded from <c>config/hydration.json</c>. Controls concurrency, timeouts,
/// disambiguation thresholds, and the confidence gate that triggers review
/// queue entries.
///
/// <list type="bullet">
///   <item><b>Stage 1 — Retail Identification:</b> retail providers search catalogues
///     using file metadata. Cover art, descriptions, ratings, and bridge IDs
///     (ISBN, ASIN, TMDB ID) are gathered. <c>RetailMatchScoringService</c> gates
///     acceptance (≥0.85 auto, 0.50–0.85 review, &lt;0.50 discard).</item>
///   <item><b>Stage 2 — Wikidata Bridge Resolution:</b> uses bridge IDs from Stage 1
///     to resolve Wikidata QIDs for canonical identity, universe linkage,
///     and person enrichment. Requires Stage 1 success.</item>
///   <item><b>Stage 3 — Universe Enrichment:</b> background service for deep
///     relationship discovery, fictional entities, and image enrichment.
///     Runs on cron schedule.</item>
/// </list>
/// </summary>
public sealed class HydrationSettings
{
    /// <summary>
    /// Maximum app-wide concurrent retail provider work items across all libraries.
    /// </summary>
    [JsonPropertyName("max_concurrent_retail_provider_jobs")]
    public int MaxConcurrentRetailProviderJobs { get; set; } = 4;

    /// <summary>
    /// Maximum app-wide concurrent Wikidata/Wikipedia work items across all libraries.
    /// </summary>
    [JsonPropertyName("max_concurrent_wikidata_jobs")]
    public int MaxConcurrentWikidataJobs { get; set; } = 2;

    /// <summary>
    /// Maximum app-wide concurrent TMDB artwork enrichment work items across all libraries.
    /// </summary>
    [JsonPropertyName("max_concurrent_tmdb_artwork_jobs")]
    public int MaxConcurrentTmdbArtworkJobs { get; set; } = 1;

    /// <summary>
    /// Maximum app-wide concurrent metadata write-back work items across all libraries.
    /// </summary>
    [JsonPropertyName("max_concurrent_writeback_jobs")]
    public int MaxConcurrentWriteBackJobs { get; set; } = 1;

    /// <summary>Timeout in seconds for Stage 1 (Retail Identification — provider API calls).</summary>
    [JsonPropertyName("stage1_timeout_seconds")]
    public int Stage1TimeoutSeconds { get; set; } = 45;

    /// <summary>Maximum runtime in seconds for one quick-hydration job.</summary>
    [JsonPropertyName("quick_hydration_timeout_seconds")]
    public int QuickHydrationTimeoutSeconds { get; set; } = 1200;

    /// <summary>
    /// Minimum confidence required for a QID match to be accepted automatically
    /// during Stage 1 disambiguation. Below this threshold, multiple candidates
    /// are surfaced to the review queue.
    /// </summary>
    [JsonPropertyName("disambiguation_threshold")]
    public double DisambiguationThreshold { get; set; } = 0.7;

    /// <summary>
    /// After all stages complete, if the entity's overall confidence is below
    /// this threshold, a review queue entry is created for user verification.
    /// </summary>
    [JsonPropertyName("auto_review_confidence_threshold")]
    public double AutoReviewConfidenceThreshold { get; set; } = 0.60;

    /// <summary>
    /// Maximum number of QID candidates to store when disambiguation is needed.
    /// Prevents unbounded JSON payloads in the review queue.
    /// </summary>
    [JsonPropertyName("max_qid_candidates")]
    public int MaxQidCandidates { get; set; } = 5;

    /// <summary>
    /// When <c>true</c>, Stage 2 (Wikipedia) is skipped if Stage 1 did not
    /// resolve a Wikidata QID. Wikipedia requires a QID to look up the article
    /// via sitelinks, so skipping is the default behaviour.
    /// </summary>
    [JsonPropertyName("skip_wikipedia_without_qid")]
    public bool SkipWikipediaWithoutQid { get; set; } = true;

    /// <summary>
    /// Maximum character length for Wikipedia description extracts.
    /// Longer extracts are truncated with an ellipsis.
    /// </summary>
    [JsonPropertyName("wikipedia_description_max_chars")]
    public int WikipediaDescriptionMaxChars { get; set; } = 1000;

    /// <summary>
    /// Maximum depth for recursive fictional entity relationship population.
    /// Target entities are created but NOT enriched beyond this depth.
    /// </summary>
    [JsonPropertyName("fictional_entity_enrichment_depth")]
    public int FictionalEntityEnrichmentDepth { get; set; } = 2;

    /// <summary>
    /// When a Wikidata QID is confirmed during hydration, the auto-organize
    /// confidence gate is lowered from <see cref="MediaEngine.Intelligence.ScoringConfiguration.AutoLinkThreshold"/>
    /// (0.85) to this value.  This allows audiobooks and other media types
    /// with conservative processor confidence to be organized once their
    /// identity is positively confirmed.
    /// </summary>
    [JsonPropertyName("post_hydration_organize_threshold")]
    public double PostHydrationOrganizeThreshold { get; set; } = 0.70;

    /// <summary>
    /// Collection relationship types that can promote lane shelves into a
    /// broader parent collection.
    /// </summary>
    [JsonPropertyName("collection_rollup_relationship_types")]
    public List<string> CollectionRollupRelationshipTypes { get; set; } =
        ["series", "franchise", "fictional_universe", "based_on"];

    // ── Two-Pass Enrichment Architecture ──────────────────────────────────────────

    /// <summary>
    /// When <c>true</c>, the hydration pipeline splits into two passes:
    /// Pass 1 (Quick Match) fetches core identity + cover art immediately,
    /// and Pass 2 (Universe Lookup) runs later for deep enrichment.
    /// When <c>false</c>, all requests run the full pipeline (backward compat).
    /// </summary>
    [JsonPropertyName("two_pass_enabled")]
    public bool TwoPassEnabled { get; set; } = true;

    /// <summary>
    /// When <c>true</c> and two-pass is enabled, Pass 1 uses a reduced SPARQL
    /// query fetching only core properties (title, author, year, genre, series,
    /// series_position) plus bridge IDs. When <c>false</c>, Pass 1 uses the
    /// full 50+ property query.
    /// </summary>
    [JsonPropertyName("pass1_core_properties_only")]
    public bool Pass1CorePropertiesOnly { get; set; } = true;

    /// <summary>
    /// Seconds to wait between idle checks before processing Pass 2 items.
    /// The service queries <c>IIdentityJobRepository.CountActiveAsync</c> on
    /// this interval and only processes when the active job count is zero.
    /// </summary>
    [JsonPropertyName("pass2_idle_delay_seconds")]
    public int Pass2IdleDelaySeconds { get; set; } = 10;

    /// <summary>
    /// Milliseconds to delay between each Pass 2 item to respect rate limits.
    /// Prevents overwhelming external APIs during bulk enrichment.
    /// </summary>
    [JsonPropertyName("pass2_rate_limit_ms")]
    public int Pass2RateLimitMs { get; set; } = 2000;

    /// <summary>
    /// Hours after which a pending Pass 2 item is considered stale.
    /// The nightly sweep prioritises items older than this threshold.
    /// </summary>
    [JsonPropertyName("pass2_stale_threshold_hours")]
    public int Pass2StaleThresholdHours { get; set; } = 24;

    /// <summary>
    /// Maximum number of Pass 2 items to process in a single batch.
    /// Limits processing duration per cycle.
    /// </summary>
    [JsonPropertyName("pass2_batch_size")]
    public int Pass2BatchSize { get; set; } = 50;

    // ── Batch Reconciliation ──────────────────────────────────────────

    // ── Retail-First Pipeline ──────────────────────────────────────────

    /// <summary>
    /// When <c>true</c>, Stage 0 (Local Match) checks the bridge_ids table and
    /// canonical_values for existing matches before making any external API calls.
    /// Dramatically reduces external calls for episodic content (TV, music).
    /// </summary>
    [JsonPropertyName("local_match_enabled")]
    public bool LocalMatchEnabled { get; set; } = true;

    /// <summary>
    /// Minimum fuzzy match confidence for a title+author match against existing
    /// canonical values. Very high threshold (0.95) to prevent false positives.
    /// Only used when no exact ID match is found.
    /// </summary>
    [JsonPropertyName("local_match_fuzzy_threshold")]
    public double LocalMatchFuzzyThreshold { get; set; } = 0.95;

    /// <summary>
    /// Minimum composite confidence for a retail match to be auto-accepted
    /// during Stage 1 (Retail Identification). Below this threshold, the
    /// match goes to review queue as <see cref="Domain.Constants.ReviewTrigger.RetailMatchAmbiguous"/>.
    /// </summary>
    [JsonPropertyName("retail_auto_accept_threshold")]
    public double RetailAutoAcceptThreshold { get; set; } = 0.90;

    /// <summary>
    /// Below this threshold, a retail match is treated as too weak to accept
    /// even provisionally. The item is flagged as <see cref="Domain.Constants.ReviewTrigger.RetailMatchFailed"/>.
    /// Between this and <see cref="RetailAutoAcceptThreshold"/>, the match is
    /// provisionally accepted and sent to review.
    /// </summary>
    [JsonPropertyName("retail_ambiguous_threshold")]
    public double RetailAmbiguousThreshold { get; set; } = 0.65;

    /// <summary>
    /// Media types for which the pipeline resolves Wikidata edition entities
    /// (via P629 edition_or_translation_of) in addition to work entities.
    /// Media types NOT in this list resolve directly to work QIDs.
    /// Configurable to allow adding/removing edition awareness without code changes.
    /// </summary>
    [JsonPropertyName("edition_aware_media_types")]
    public List<string> EditionAwareMediaTypes { get; set; } =
        ["Books", "Audiobooks", "Movies", "Comics", "Music"];

    /// <summary>
    /// Weights for fuzzy field matching during Stage 1 retail scoring.
    /// Keys: "title", "author", "year", "format". Values: 0.0–1.0.
    /// Must sum to 1.0. Read by <c>RetailMatchScoringService</c>.
    /// </summary>
    [JsonPropertyName("fuzzy_match_weights")]
    public Dictionary<string, double> FuzzyMatchWeights { get; set; } = new()
    {
        ["title"] = 0.45,
        ["author"] = 0.35,
        ["year"] = 0.10,
        ["format"] = 0.10,
    };

    /// <summary>
    /// Maximum entities per Wikidata batch API call during Stage 2
    /// (Wikidata Bridge Resolution). The Data Extension API supports up to 50.
    /// </summary>
    [JsonPropertyName("wikidata_batch_size")]
    public int WikidataBatchSize { get; set; } = 50;

    /// <summary>
    /// Maximum processing attempts for a durable identity job before it is
    /// moved to the failed/dead-letter state.
    /// </summary>
    [JsonPropertyName("identity_retry_max_attempts")]
    public int IdentityRetryMaxAttempts { get; set; } = 5;

    /// <summary>
    /// Base delay, in seconds, for exponential identity job retry backoff.
    /// </summary>
    [JsonPropertyName("identity_retry_base_delay_seconds")]
    public int IdentityRetryBaseDelaySeconds { get; set; } = 10;

    /// <summary>
    /// Maximum identity job retry delay, in seconds, before jitter is applied.
    /// </summary>
    [JsonPropertyName("identity_retry_max_delay_seconds")]
    public int IdentityRetryMaxDelaySeconds { get; set; } = 300;

    /// <summary>
    /// Minimum random retry jitter in milliseconds.
    /// </summary>
    [JsonPropertyName("identity_retry_jitter_min_ms")]
    public int IdentityRetryJitterMinMilliseconds { get; set; } = 250;

    /// <summary>
    /// Maximum random retry jitter in milliseconds.
    /// </summary>
    [JsonPropertyName("identity_retry_jitter_max_ms")]
    public int IdentityRetryJitterMaxMilliseconds { get; set; } = 1750;


    // ── Chronicle Engine ──────────────────────────────────────────────

    /// <summary>
    /// When <c>true</c>, the qualified statement SPARQL syntax (p:/ps:/pq:)
    /// is used for relationship properties to capture P580 (start time) and
    /// P582 (end time) temporal qualifiers.
    /// </summary>
    [JsonPropertyName("fetch_temporal_qualifiers")]
    public bool FetchTemporalQualifiers { get; set; } = true;

    /// <summary>Maximum entities per batch SPARQL query using VALUES clause.</summary>
    [JsonPropertyName("batch_sparql_size")]
    public int BatchSparqlSize { get; set; } = 50;

    /// <summary>Maximum depth for lineage traversal (2 = grandparents/grandchildren).</summary>
    [JsonPropertyName("lineage_depth")]
    public int LineageDepth { get; set; } = 2;

    // ── Stage 3: Universe Enrichment (Background Service) ──────────────

    /// <summary>
    /// When <c>true</c>, the Universe Enrichment background service runs on
    /// its cron schedule. When <c>false</c>, Stage 3 is skipped entirely.
    /// </summary>
    [JsonPropertyName("stage3_enabled")]
    public bool Stage3Enabled { get; set; } = true;

    /// <summary>
    /// Milliseconds to delay between each work during a Stage 3 sweep.
    /// Prevents overwhelming external APIs (Wikidata and TMDB).
    /// </summary>
    [JsonPropertyName("stage3_rate_limit_ms")]
    public int Stage3RateLimitMs { get; set; } = 3000;

    /// <summary>
    /// Maximum number of works to process in a single Stage 3 sweep.
    /// Limits processing duration per cycle.
    /// </summary>
    [JsonPropertyName("stage3_max_items_per_sweep")]
    public int Stage3MaxItemsPerSweep { get; set; } = 50;

    /// <summary>
    /// Number of days after which a previously enriched work is considered stale
    /// and eligible for re-enrichment during a Stage 3 sweep.
    /// </summary>
    [JsonPropertyName("stage3_refresh_days")]
    public int Stage3RefreshDays { get; set; } = 30;

    /// <summary>
    /// Number of days after which a person or musical-group profile is refreshed.
    /// Kept separate from work enrichment so provider policy is data-driven.
    /// </summary>
    [JsonPropertyName("person_refresh_days")]
    public int PersonRefreshDays { get; set; } = 30;

    /// <summary>
    /// Minimum age, in days, before a persisted Wikidata series manifest is
    /// refreshed. Newly ingested sibling files first link against the cached
    /// named manifest to avoid repeated full series downloads.
    /// </summary>
    [JsonPropertyName("series_manifest_refresh_days")]
    public int SeriesManifestRefreshDays { get; set; } = 30;

    /// <summary>
    /// Maximum depth for recursive fictional entity discovery during Stage 3.
    /// Overrides <see cref="FictionalEntityEnrichmentDepth"/> within the
    /// background service context.
    /// </summary>
    [JsonPropertyName("stage3_max_depth")]
    public int Stage3MaxDepth { get; set; } = 2;

    /// <summary>
    /// Number of days to retain entity timeline events before automatic culling.
    /// The most recent event per entity per stage is always preserved regardless
    /// of age. Set to 0 to disable culling. Default: 365 days.
    /// </summary>
    [JsonPropertyName("timeline_retention_days")]
    public int TimelineRetentionDays { get; set; } = 365;

}
