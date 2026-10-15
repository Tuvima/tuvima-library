namespace MediaEngine.Domain.Authorization;

public static class ApplicationPermissionIds
{
    public static readonly ApplicationPermissionId SystemStatusRead = new("system.status.read");
    public static readonly ApplicationPermissionId SystemMetricsRead = new("system.metrics.read");
    public static readonly ApplicationPermissionId SystemActivityRead = new("system.activity.read");
    public static readonly ApplicationPermissionId SystemAuditRead = new("system.audit.read");
    public static readonly ApplicationPermissionId SystemLogsRead = new("system.logs.read");
    public static readonly ApplicationPermissionId LibraryRead = new("library.read");
    public static readonly ApplicationPermissionId ArtworkRead = new("artwork.read");
    public static readonly ApplicationPermissionId LibraryChangesRead = new("library.changes.read");
    public static readonly ApplicationPermissionId LibraryFilesRead = new("library.files.read");
    public static readonly ApplicationPermissionId PlaybackRead = new("playback.read");
    public static readonly ApplicationPermissionId PlaybackWrite = new("playback.write");
    public static readonly ApplicationPermissionId QueueRead = new("queue.read");
    public static readonly ApplicationPermissionId QueueWrite = new("queue.write");
    public static readonly ApplicationPermissionId ProgressRead = new("progress.read");
    public static readonly ApplicationPermissionId ProgressWrite = new("progress.write");
    public static readonly ApplicationPermissionId DownloadsRead = new("downloads.read");
    public static readonly ApplicationPermissionId DownloadsWrite = new("downloads.write");
    public static readonly ApplicationPermissionId PlaybackSessionsRead = new("playback.sessions.read");
    public static readonly ApplicationPermissionId PlaybackSessionsControl = new("playback.sessions.control");
    public static readonly ApplicationPermissionId PlaybackHistoryRead = new("playback.history.read");
    public static readonly ApplicationPermissionId AnalyticsPlaybackRead = new("analytics.playback.read");
    public static readonly ApplicationPermissionId AnalyticsLibraryRead = new("analytics.library.read");
    public static readonly ApplicationPermissionId AnalyticsUsersRead = new("analytics.users.read");
    public static readonly ApplicationPermissionId AnalyticsDevicesRead = new("analytics.devices.read");
    public static readonly ApplicationPermissionId MetadataRead = new("metadata.read");
    public static readonly ApplicationPermissionId MetadataWrite = new("metadata.write");
    public static readonly ApplicationPermissionId MetadataMatch = new("metadata.match");
    public static readonly ApplicationPermissionId MetadataEnrichmentRead = new("metadata.enrichment.read");
    public static readonly ApplicationPermissionId MetadataEnrichmentRun = new("metadata.enrichment.run");
    public static readonly ApplicationPermissionId ProvidersStatusRead = new("providers.status.read");
    public static readonly ApplicationPermissionId ProvidersConfigRead = new("providers.config.read");
    public static readonly ApplicationPermissionId ProvidersConfigWrite = new("providers.config.write");
    public static readonly ApplicationPermissionId IngestionStatusRead = new("ingestion.status.read");
    public static readonly ApplicationPermissionId IngestionHistoryRead = new("ingestion.history.read");
    public static readonly ApplicationPermissionId IngestionRun = new("ingestion.run");
    public static readonly ApplicationPermissionId IngestionRetry = new("ingestion.retry");
    public static readonly ApplicationPermissionId IngestionCancel = new("ingestion.cancel");
    public static readonly ApplicationPermissionId ReviewRead = new("review.read");
    public static readonly ApplicationPermissionId ReviewResolve = new("review.resolve");
    public static readonly ApplicationPermissionId CollectionsRead = new("collections.read");
    public static readonly ApplicationPermissionId CollectionsWrite = new("collections.write");
    public static readonly ApplicationPermissionId ViewSharedRead = new("view.shared.read");
    public static readonly ApplicationPermissionId ViewPersonalRead = new("view.personal.read");
    public static readonly ApplicationPermissionId ViewOriginalsRead = new("view.originals.read");
    public static readonly ApplicationPermissionId ViewUpload = new("view.upload");
    public static readonly ApplicationPermissionId ViewGalleriesRead = new("view.galleries.read");
    public static readonly ApplicationPermissionId ViewGalleriesWrite = new("view.galleries.write");
    public static readonly ApplicationPermissionId IdentityUsersRead = new("identity.users.read");
    public static readonly ApplicationPermissionId IdentitySessionsRead = new("identity.sessions.read");
    public static readonly ApplicationPermissionId IdentityUsersWrite = new("identity.users.write");
    public static readonly ApplicationPermissionId IdentityApplicationsWrite = new("identity.applications.write");
    public static readonly ApplicationPermissionId PluginsRead = new("plugins.read");
    public static readonly ApplicationPermissionId PluginsJobsRead = new("plugins.jobs.read");
    public static readonly ApplicationPermissionId PluginsJobsRun = new("plugins.jobs.run");
    public static readonly ApplicationPermissionId PluginsManage = new("plugins.manage");
    public static readonly ApplicationPermissionId AiStatusRead = new("ai.status.read");
    public static readonly ApplicationPermissionId AiInfer = new("ai.infer");
    public static readonly ApplicationPermissionId AiManage = new("ai.manage");
    public static readonly ApplicationPermissionId NetworkStatusRead = new("network.status.read");
    public static readonly ApplicationPermissionId NetworkConfigWrite = new("network.config.write");
    public static readonly ApplicationPermissionId StorageStatusRead = new("storage.status.read");
    public static readonly ApplicationPermissionId StorageConfigWrite = new("storage.config.write");
    public static readonly ApplicationPermissionId BackupRead = new("backup.read");
    public static readonly ApplicationPermissionId BackupRun = new("backup.run");
    public static readonly ApplicationPermissionId BackupRestore = new("backup.restore");
    public static readonly ApplicationPermissionId EventsSubscribe = new("events.subscribe");

    public static readonly IReadOnlyList<ApplicationPermissionId> NativeClient = Array.AsReadOnly<ApplicationPermissionId>(
        [LibraryRead, ArtworkRead, LibraryChangesRead, ProgressRead, ProgressWrite, QueueRead, QueueWrite,
            PlaybackRead, PlaybackWrite, DownloadsRead, DownloadsWrite, EventsSubscribe]);

    /// <summary>
    /// The three scopes behind the one "Photos and personal videos" consent item. A paired app may request them, but they are
    /// never part of the default scopes or the Media Player preset, and are granted only when the person ticks the separate
    /// photo consent on the pairing screen.
    /// </summary>
    public static readonly IReadOnlyList<ApplicationPermissionId> NativeClientPhotos = Array.AsReadOnly<ApplicationPermissionId>(
        [ViewPersonalRead, ViewOriginalsRead, ViewGalleriesRead]);
}
