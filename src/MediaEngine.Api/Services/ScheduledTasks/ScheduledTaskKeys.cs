namespace MediaEngine.Api.Services.ScheduledTasks;

/// <summary>
/// The key of every recurring task. Run records, the Scheduled tasks list and the services themselves all use these.
/// Keys that name a maintenance schedule are also the key in <c>config/maintenance.json</c> <c>schedules</c>.
/// </summary>
public static class ScheduledTaskKeys
{
    public const string ActivityPruning = "activity_pruning";
    public const string LibraryReconciliation = "library_reconciliation";
    public const string MissingUniverseSweep = "missing_universe_sweep";
    public const string RejectedFileCleanup = "rejected_file_cleanup";
    public const string UniverseEnrichment = "universe_enrichment";
    public const string RetagSweep = "retag_sweep";
    public const string StorageMaintenance = "storage_maintenance";
    public const string NightlyEnrichmentSweep = "pass2_nightly_sweep";
    public const string PluginSegmentDetection = "plugin_segment_detection";
    public const string VibeTags = "vibe_tags";
    public const string SeriesOrder = "series_order";
    public const string DescriptionInsights = "description_insights";
    public const string WebhookDelivery = "webhook_delivery";
}
