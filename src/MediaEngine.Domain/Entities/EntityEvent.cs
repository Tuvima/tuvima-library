namespace MediaEngine.Domain.Entities;

/// <summary>
/// A single event in an entity's life history — pipeline stage completion,
/// refresh, user action, sync writeback, or enrichment update.
/// Stored in the <c>entity_events</c> table.
/// </summary>
public sealed class EntityEvent
{
    /// <summary>Row primary key.</summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>The entity this event belongs to (work, person, collection, parent_collection).</summary>
    public Guid EntityId { get; set; }

    /// <summary>Entity type discriminator: "Work", "Person", "Collection", "Universe".</summary>
    public string EntityType { get; set; } = "";

    /// <summary>
    /// What happened. Pipeline events: file_scanned, retail_matched, retail_no_match,
    /// retail_manual_approved, retail_manual_dismissed, retail_ai_classified, retail_rematched, owned_book_matched,
    /// wikidata_bridge_resolved, wikidata_title_resolved, wikidata_ai_disambiguated,
    /// wikidata_pre_resolved, wikidata_manual_resolved, wikidata_no_match,
    /// universe_grouped, universe_pending.
    /// Refresh: stage1_refresh, stage2_refresh, stage3_refresh.
    /// User: user_field_edit, user_lock.
    /// Sync: sync_writeback, sync_reverted.
    /// Person: person_enriched, person_refreshed, person_pseudonym_linked.
    /// Universe/Collection: universe_created, universe_refreshed, collection_created, collection_refreshed,
    /// series_linked, series_unlinked.
    /// </summary>
    public string EventType { get; set; } = "";

    /// <summary>
    /// Pipeline stage number: 0=File, 1=Retail, 2=Wikidata, 3=Universe.
    /// Null for non-pipeline events (person enrichment, user edits, etc.).
    /// </summary>
    public int? Stage { get; set; }

    /// <summary>
    /// What caused this event: ingestion, 30_day_refresh, user_rematch, user_manual,
    /// ai_disambiguation, api_request, group_refresh, person_enrichment,
    /// universe_enrichment, lore_delta.
    /// </summary>
    public string Trigger { get; set; } = "";

    /// <summary>UUID of the provider that contributed (nullable).</summary>
    public string? ProviderId { get; set; }

    /// <summary>Human-readable provider name (e.g. "apple_api", "tmdb").</summary>
    public string? ProviderName { get; set; }

    /// <summary>Bridge ID type used for resolution (e.g. "isbn_13", "tmdb_id"). Stage 2 only.</summary>
    public string? BridgeIdType { get; set; }

    /// <summary>Actual bridge ID value (e.g. "978-0441013593"). Stage 2 only.</summary>
    public string? BridgeIdValue { get; set; }

    /// <summary>Wikidata QID resolved by this event (e.g. "Q83471").</summary>
    public string? ResolvedQid { get; set; }

    /// <summary>Confidence score at this point in the pipeline.</summary>
    public double? Confidence { get; set; }

    // ── Stage 1 retail match scores (nullable, only for retail events) ──

    /// <summary>Title similarity score (0.0–1.0).</summary>
    public double? ScoreTitle { get; set; }

    /// <summary>Author/creator similarity score (0.0–1.0).</summary>
    public double? ScoreAuthor { get; set; }

    /// <summary>Year match score (1.0 exact, 0.8 off-by-1, 0.3 off-by-2+).</summary>
    public double? ScoreYear { get; set; }

    /// <summary>Format match score (1.0 exact match).</summary>
    public double? ScoreFormat { get; set; }

    /// <summary>Cross-field boost (narrator-in-description, genre overlap, etc.).</summary>
    public double? ScoreCrossField { get; set; }

    /// <summary>Cover art pHash similarity boost (+0.10 strong, +0.05 moderate).</summary>
    public double? ScoreCoverArt { get; set; }

    /// <summary>Weighted composite score from all field scores.</summary>
    public double? ScoreComposite { get; set; }

    /// <summary>When this event occurred.</summary>
    public DateTimeOffset OccurredAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Human-readable summary (e.g. "ISBN-13 → Q83471", "Auto-accepted: Apple API at 0.94").</summary>
    public string? Detail { get; set; }
}
