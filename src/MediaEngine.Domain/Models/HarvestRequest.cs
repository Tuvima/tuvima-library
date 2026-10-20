using MediaEngine.Domain.Enums;

namespace MediaEngine.Domain.Models;

/// <summary>
/// A unit of work placed on the metadata harvest queue.
///
/// Carries enough context for the <c>MetadataHarvestingService</c> to route the
/// request to the correct external provider adapters and build well-formed
/// <see cref="Entities.MetadataClaim"/> rows from the returned claims.
///
/// Spec: Phase 9 – Non-Blocking Harvesting § Queue Item.
/// </summary>
public sealed class HarvestRequest
{
    /// <summary>
    /// The domain entity this request enriches.
    /// Points to a <c>media_assets.id</c>, <c>persons.id</c>, etc., depending on
    /// <see cref="EntityType"/>.
    /// </summary>
    public required Guid EntityId { get; init; }

    /// <summary>The kind of entity being harvested.</summary>
    public required EntityType EntityType { get; init; }

    /// <summary>
    /// The media type of the asset being harvested.
    /// Used by adapters to decide which endpoint / entity type to query.
    /// <see cref="MediaType.Unknown"/> is valid for Person enrichment requests
    /// where a media type is not applicable.
    /// </summary>
    public required MediaType MediaType { get; init; }

    /// <summary>
    /// Contextual hints for the adapter.
    /// Common keys: <c>"title"</c>, <c>"author"</c>, <c>"asin"</c>,
    /// <c>"isbn"</c>, <c>"narrator"</c>, <c>"name"</c>, <c>"role"</c>.
    ///
    /// Adapters must never fail if a hint key is absent; they return an empty
    /// result list instead.
    /// </summary>
    public IReadOnlyDictionary<string, string> Hints { get; init; }
        = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// An already-resolved Wikidata QID to use for Stage 2+3 hydration.
    ///
    /// When set, the pipeline skips QID resolution in Stage 2 and goes straight
    /// to SPARQL deep hydration using this QID. Used when the user resolves a
    /// disambiguation review item by selecting a specific QID candidate.
    /// </summary>
    public string? PreResolvedQid { get; init; }

    /// <summary>
    /// When <c>true</c>, this request is a re-enqueue for cover art download
    /// after auto-organization. The pipeline still runs (claims are appended,
    /// cover is downloaded) but the <c>MediaAdded</c> activity entry is suppressed
    /// to avoid duplicating the original entry.
    /// </summary>
    public bool SuppressActivityEntry { get; init; }

    /// <summary>
    /// When <c>true</c>, the pipeline will not create new review queue entries.
    /// Used during user-triggered review resolution to prevent the pipeline from
    /// re-creating review items for the same entity while the resolution is in progress.
    /// </summary>
    public bool SuppressReviewCreation { get; init; }

    /// <summary>
    /// When <c>true</c>, indicates this request was initiated by user action
    /// (e.g., Fix Match from Dashboard). User-initiated requests may set
    /// <see cref="PreResolvedQid"/> to bypass the retail gate.
    /// </summary>
    public bool IsUserResolution { get; init; }

    /// <summary>
    /// When <c>true</c>, the pipeline skips Stage 1 (retail identification) and
    /// starts directly from Stage 2 (Wikidata bridge resolution). Used when a
    /// curator manually selects a retail match — retail is already done, but
    /// Wikidata resolution should still run automatically.
    /// </summary>
    public bool SkipRetailStage { get; init; }

    /// <summary>
    /// Bridge identifiers from the Ingestion Hint cache (sibling-aware priming).
    /// When present, Stage 1 can use these to skip the bridge lookup SPARQL query.
    /// Keys are claim keys (e.g. "isbn", "tmdb_movie_id").
    /// </summary>
    public Dictionary<string, string>? FolderHintBridgeIds { get; set; }

    /// <summary>
    /// Collection ID suggested by the Ingestion Hint cache (sibling-aware priming).
    /// When present, the Collection Arbiter evaluates this Collection first before running
    /// the full candidate search.
    /// </summary>
    public Guid? HintedCollectionId { get; set; }

    /// <summary>
    /// Which enrichment pass this request belongs to.
    ///
    /// <see cref="HydrationPass.Quick"/> (default) fetches core identity
    /// only — title, author, year, genre, series, bridge IDs, cover art.
    /// <see cref="HydrationPass.Universe"/> runs the full 50+ property
    /// SPARQL deep hydration, fictional entity discovery, and relationship
    /// population.
    ///
    /// Spec: §3.24 — Two-Pass Enrichment Architecture.
    /// </summary>
    public HydrationPass Pass { get; init; } = HydrationPass.Quick;

    /// <summary>
    /// Number of times this request has been re-enqueued after a pipeline failure.
    /// The processing loop increments this on each retry and stops after 3 attempts,
    /// creating a review queue entry on final failure.
    /// </summary>
    public int RetryCount { get; set; }

    /// <summary>
    /// Bridge identifiers accumulated from prior providers in a Sequential pipeline.
    /// When set, the current provider can use these as direct lookup hints instead
    /// of relying solely on file-embedded metadata.
    /// Only populated during Stage 1 execution when the pipeline strategy is
    /// <see cref="MediaEngine.Domain.Enums.ProviderStrategy.Sequential"/>.
    /// </summary>
    public IReadOnlyDictionary<string, string>? PriorProviderBridgeIds { get; init; }
}
