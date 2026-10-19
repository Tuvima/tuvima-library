namespace MediaEngine.Domain;

/// <summary>
/// Single source of truth for SignalR collection paths and event names.
/// Both Engine (publishers) and Dashboard (subscribers) must reference
/// these constants instead of using raw string literals.
/// </summary>
public static class SignalREvents
{
    /// <summary>SignalR collection endpoint path.</summary>
    public const string IntercomPath = "/intercom";

    // ── Ingestion lifecycle ──────────────────────────────────────────
    public const string IngestionStarted = "IngestionStarted";
    public const string IngestionHashed = "IngestionHashed";
    public const string IngestionCompleted = "IngestionCompleted";
    public const string IngestionFailed = "IngestionFailed";
    public const string IngestionProgress = "IngestionProgress";
    public const string IngestionItemProgress = "IngestionItemProgress";
    public const string BatchProgress = "BatchProgress";
    public const string MediaOperationChanged = "MediaOperationChanged";
    public const string CapabilityStateChanged = "CapabilityStateChanged";

    // ── Media management ─────────────────────────────────────────────
    public const string MediaAdded = "MediaAdded";
    public const string MediaRemoved = "MediaRemoved";

    // ── Metadata & enrichment ────────────────────────────────────────
    public const string MetadataHarvested = "MetadataHarvested";
    public const string HydrationStageCompleted = "HydrationStageCompleted";
    public const string PersonEnriched = "PersonEnriched";
    public const string FictionalEntityEnriched = "FictionalEntityEnriched";

    // ── Review queue ─────────────────────────────────────────────────
    public const string ReviewItemCreated = "ReviewItemCreated";
    public const string ReviewItemResolved = "ReviewItemResolved";

    // ── Provider health ──────────────────────────────────────────────
    public const string ProviderStatusChanged = "ProviderStatusChanged";
    public const string ProviderRecoveryFlush = "ProviderRecoveryFlush";
    public const string ProviderActivity = "ProviderActivity";

    // ── AI model lifecycle ───────────────────────────────────────────
    public const string ModelStateChanged = "ModelStateChanged";
    public const string ModelDownloadProgress = "ModelDownloadProgress";

    // ── Library health ───────────────────────────────────────────────
    public const string FolderHealthChanged = "FolderHealthChanged";
    public const string WatchFolderActive = "WatchFolderActive";

    // ── Universe enrichment ──────────────────────────────────────────
    public const string UniverseEnrichmentProgress = "UniverseEnrichmentProgress";

    // ── Auto re-tag sweep ────────────────────────────────────────────
    public const string WritebackConfigChanged = "WritebackConfigChanged";
    public const string RetagSweepProgress = "RetagSweepProgress";
    public const string RetagSweepCompleted = "RetagSweepCompleted";

    // ── Initial sweep (side-by-side-with-Plex plan §M) ───────────────
    public const string InitialSweepStarted = "InitialSweepStarted";
    public const string InitialSweepProgress = "InitialSweepProgress";
    public const string InitialSweepCompleted = "InitialSweepCompleted";
}
