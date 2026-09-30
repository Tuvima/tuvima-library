namespace MediaEngine.Domain.Constants;

/// <summary>
/// String constants for the <c>action_type</c> column in <c>system_activity</c>.
///
/// Stored as TEXT in SQLite for readability and extensibility — future phases
/// can introduce new action types without a schema migration.
/// </summary>
public static class SystemActionType
{
    /// <summary>A file was ingested into the library.</summary>
    public const string FileIngested = "FileIngested";

    /// <summary>An entity was deeply enriched via Wikidata SPARQL.</summary>
    public const string MetadataHydrated = "MetadataHydrated";

    /// <summary>A remote artwork hash was verified against the local copy.</summary>
    public const string HashVerified = "HashVerified";

    /// <summary>A media file was moved to a new location in the library.</summary>
    public const string PathUpdated = "PathUpdated";

    /// <summary>A weekly (or manual) metadata sync completed.</summary>
    public const string SyncCompleted = "SyncCompleted";

    /// <summary>A library crawl (Great Inhale) was started.</summary>
    public const string CrawlStarted = "CrawlStarted";

    /// <summary>A library crawl (Great Inhale) finished.</summary>
    public const string CrawlFinished = "CrawlFinished";

    /// <summary>Metadata was refreshed for one or more entities.</summary>
    public const string MetadataRefreshed = "MetadataRefreshed";

    /// <summary>A library.xml sidecar was updated on disk.</summary>
    public const string SidecarUpdated = "SidecarUpdated";

    /// <summary>Old activity entries were pruned from the ledger.</summary>
    public const string ActivityPruned = "ActivityPruned";

    /// <summary>An external bridge identifier was synced or updated from Wikidata.</summary>
    public const string BridgeSyncUpdated = "BridgeSyncUpdated";

    /// <summary>A person entity was deeply enriched with social links and metadata.</summary>
    public const string PersonHydrated = "PersonHydrated";

    /// <summary>A weekly metadata sync cycle was started.</summary>
    public const string WeeklySyncStarted = "WeeklySyncStarted";

    /// <summary>An affiliate link was generated from a bridge identifier.</summary>
    public const string AffiliateGenerated = "AffiliateGenerated";

    // -- Hydration Pipeline (Three-Stage) -------------------------------------

    /// <summary>A Universe QID was recovered for an item previously matched only via retail providers.</summary>
    public const string UniverseMatchRecovered = "UniverseMatchRecovered";

    /// <summary>Hydration Stage 1 (Authority Match — Wikidata) completed for an entity.</summary>
    public const string HydrationStage1Completed = "HydrationStage1Completed";

    /// <summary>Hydration Stage 2 (Context Match — Wikipedia) completed for an entity.</summary>
    public const string HydrationStage2Completed = "HydrationStage2Completed";

    /// <summary>Hydration Stage 3 (Retail Match — waterfall) completed for an entity.</summary>
    public const string HydrationStage3Completed = "HydrationStage3Completed";

    /// <summary>A review queue item was created (disambiguation, low confidence, etc.).</summary>
    public const string ReviewItemCreated = "ReviewItemCreated";

    /// <summary>A review queue item was resolved or dismissed by a user.</summary>
    public const string ReviewItemResolved = "ReviewItemResolved";

    /// <summary>A user manually overrode metadata fields via the Edit Metadata dialog.</summary>
    public const string MetadataManualOverride = "MetadataManualOverride";

    /// <summary>Resolved metadata was written back into the physical media file's embedded tags.</summary>
    public const string MetadataWrittenToFile = "MetadataWrittenToFile";

    /// <summary>Preferred artwork was embedded and verified in a physical media file.</summary>
    public const string ArtworkWrittenToFile = "ArtworkWrittenToFile";

    /// <summary>Embedding preferred artwork in a physical media file failed verification.</summary>
    public const string ArtworkWritebackFailed = "ArtworkWritebackFailed";

    // -- Ingestion Pipeline Lifecycle -------------------------------------

    /// <summary>The ingestion engine started and began watching a directory.</summary>
    public const string ServerStarted = "ServerStarted";

    /// <summary>The ingestion engine was stopped.</summary>
    public const string ServerStopped = "ServerStopped";

    /// <summary>A new file was detected in the watch folder and queued for processing.</summary>
    public const string FileDetected = "FileDetected";

    /// <summary>The SHA-256 content hash was computed for a file.</summary>
    public const string FileHashed = "FileHashed";

    /// <summary>A file was skipped because it is a duplicate of an existing asset.</summary>
    public const string DuplicateSkipped = "DuplicateSkipped";

    /// <summary>A file was processed by the processor libraryItem (metadata extracted).</summary>
    public const string FileProcessed = "FileProcessed";

    /// <summary>The scoring engine assigned a confidence score to a file's metadata.</summary>
    public const string FileScored = "FileScored";

    /// <summary>A Collection ? Work ? Edition entity chain was created or linked for a file.</summary>
    public const string EntityChainCreated = "EntityChainCreated";

    /// <summary>Cover art was saved to disk alongside the organized file.</summary>
    public const string CoverArtSaved = "CoverArtSaved";

    /// <summary>A cinematic hero banner was generated from a cover image.</summary>
    public const string HeroBannerGenerated = "HeroBannerGenerated";

    /// <summary>Metadata tags were written back into the media file's embedded tags.</summary>
    public const string MetadataTagsWritten = "MetadataTagsWritten";

    /// <summary>A file was enqueued for external metadata enrichment via the hydration pipeline.</summary>
    public const string HydrationEnqueued = "HydrationEnqueued";

    /// <summary>A corrupt file was quarantined and excluded from further processing.</summary>
    public const string FileQuarantined = "FileQuarantined";

    /// <summary>A file was moved to the staging directory (.staging/) for hydration and review.</summary>
    public const string MovedToStaging = "MovedToStaging";

    // -- Staging & Reconciliation -------------------------------------------

    /// <summary>A staged asset was cleaned up (file missing, DB record deleted).</summary>
    public const string StagedFileCleaned = "StagedFileCleaned";

    /// <summary>The reconciliation service found a missing file.</summary>
    public const string ReconciliationMissing = "ReconciliationMissing";

    /// <summary>A library reconciliation scan completed.</summary>
    public const string ReconciliationCompleted = "ReconciliationCompleted";

    // -- Collection Intelligence -----------------------------------------------

    /// <summary>A new Collection was created from Wikidata relationship data.</summary>
    public const string CollectionCreated = "CollectionCreated";

    /// <summary>A Work was assigned to a Collection (firm or provisional link).</summary>
    public const string CollectionAssigned = "CollectionAssigned";

    /// <summary>Two Collections were merged when a shared relationship was discovered.</summary>
    public const string CollectionMerged = "CollectionMerged";

    // -- Consolidated Pipeline Events ----------------------------------

    /// <summary>A media file was successfully ingested and enriched (end-to-end summary).</summary>
    public const string MediaAdded = "MediaAdded";

    /// <summary>Metadata was updated for an existing media item (re-hydration or re-score).</summary>
    public const string MediaUpdated = "MediaUpdated";

    /// <summary>A media file failed to ingest (corrupt, quarantined, or unprocessable).</summary>
    public const string MediaFailed = "MediaFailed";

    /// <summary>A media file was removed from the library by user action.</summary>
    public const string MediaRemoved = "MediaRemoved";

    // -- Folder Maintenance --------------------------------------------

    /// <summary>An empty folder was cleaned up during reconciliation.</summary>
    public const string FolderCleaned = "FolderCleaned";

    /// <summary>A person folder was renamed following a metadata update (e.g. name change).</summary>
    public const string PersonFolderRenamed = "PersonFolderRenamed";

    /// <summary>Two person records were merged after QID deduplication.</summary>
    public const string PersonMerged = "PersonMerged";

    // -- Universe Graph --------------------------------------------------

    /// <summary>A fictional character was enriched from Wikidata SPARQL.</summary>
    public const string CharacterEnriched = "CharacterEnriched";

    /// <summary>A fictional location was enriched from Wikidata SPARQL.</summary>
    public const string LocationEnriched = "LocationEnriched";

    /// <summary>A fictional organization was enriched from Wikidata SPARQL.</summary>
    public const string OrganizationEnriched = "OrganizationEnriched";

    /// <summary>A relationship edge was discovered between two entities.</summary>
    public const string RelationshipDiscovered = "RelationshipDiscovered";

    /// <summary>A universe.xml sidecar was updated on disk.</summary>
    public const string UniverseXmlUpdated = "UniverseXmlUpdated";

    /// <summary>A narrative root was resolved for a work.</summary>
    public const string NarrativeRootResolved = "NarrativeRootResolved";

    // -- Chronicle Engine -------------------------------------------------

    /// <summary>A Lore Delta check was performed against Wikidata revision IDs.</summary>
    public const string LoreDeltaChecked = "LoreDeltaChecked";

    /// <summary>A canon discrepancy was detected between edition and master work.</summary>
    public const string CanonDiscrepancyDetected = "CanonDiscrepancyDetected";

    // -- Reject Workflow ------------------------------------------------------

    /// <summary>
    /// A file was explicitly rejected by the user and moved to
    /// <c>.staging/rejected/</c>. The file remains on disk and in the database
    /// until the retention period expires.
    /// </summary>
    public const string FileRejected = "FileRejected";

    /// <summary>
    /// A rejected file exceeded the configured <c>rejected_retention_days</c>
    /// and was permanently deleted from disk and from the database by the
    /// <c>RejectedFileCleanupService</c>.
    /// </summary>
    public const string FileExpired = "FileExpired";

    /// <summary>
    /// A curator marked an item as provisional with manually-entered metadata.
    /// The item is visible in the Library and periodically re-checked against Wikidata.
    /// </summary>
    public const string ItemProvisional = "ItemProvisional";

    /// <summary>
    /// A rejected item was automatically purged after exceeding the configured
    /// retention period. Both the file on disk and database records were deleted.
    /// </summary>
    public const string AutoPurge = "AutoPurge";

    /// <summary>
    /// A previously rejected item was un-rejected by a curator and returned
    /// to the review queue for re-evaluation.
    /// </summary>
    public const string ItemUnrejected = "ItemUnrejected";

    // -- Ingestion Batches --------------------------------------------------

    /// <summary>An ingestion batch was created when the engine began processing a queue of files.</summary>
    public const string BatchCreated = "BatchCreated";

    /// <summary>An ingestion batch completed — all queued files reached a terminal state.</summary>
    public const string BatchCompleted = "BatchCompleted";

    /// <summary>An ingestion batch failed — processing was interrupted or could not complete.</summary>
    public const string BatchFailed = "BatchFailed";

    // -- User Problem Reports -----------------------------------------------

    /// <summary>A user submitted a problem report on a Library item.</summary>
    public const string UserReportSubmitted = "UserReportSubmitted";

    /// <summary>A curator resolved a user problem report.</summary>
    public const string UserReportResolved = "UserReportResolved";

    /// <summary>A curator dismissed a user problem report without action.</summary>
    public const string UserReportDismissed = "UserReportDismissed";
}


