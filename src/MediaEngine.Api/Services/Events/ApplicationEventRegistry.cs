using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Events;

namespace MediaEngine.Api.Services.Events;

public sealed class ApplicationEventRegistry(IPermissionRegistry permissions)
{
    private static readonly ApplicationEventDefinition[] Values =
    [
        Definition("playback.started", ApplicationPermissionIds.PlaybackSessionsRead, true, true),
        Definition("playback.paused", ApplicationPermissionIds.PlaybackSessionsRead, true, true),
        Definition("playback.stopped", ApplicationPermissionIds.PlaybackSessionsRead, true, true),
        Definition("playback.completed", ApplicationPermissionIds.PlaybackSessionsRead, true, true),
        Definition("library.item_added", ApplicationPermissionIds.LibraryChangesRead, true),
        Definition("library.item_removed", ApplicationPermissionIds.LibraryChangesRead, true),
        Definition("library.item_updated", ApplicationPermissionIds.LibraryChangesRead, true),
        Definition("ingestion.started", ApplicationPermissionIds.IngestionStatusRead, false),
        // ingestion.progress is sent while the live queue is working, ingestion.completed once when it returns to idle;
        // both are subject "ingestion/current" with payload { files_found, files_done, files_waiting, progress, state }.
        Definition("ingestion.progress", ApplicationPermissionIds.IngestionStatusRead, false),
        Definition("ingestion.completed", ApplicationPermissionIds.IngestionStatusRead, false),
        Definition("ingestion.failed", ApplicationPermissionIds.IngestionStatusRead, false),
        Definition("metadata.updated", ApplicationPermissionIds.MetadataRead, true),
        Definition("metadata.review_required", ApplicationPermissionIds.ReviewRead, true),
        Definition("provider.status_changed", ApplicationPermissionIds.ProvidersStatusRead, false),
        Definition("plugin.job_started", ApplicationPermissionIds.PluginsJobsRead, false),
        Definition("plugin.job_completed", ApplicationPermissionIds.PluginsJobsRead, false),
        Definition("system.health_changed", ApplicationPermissionIds.SystemStatusRead, false),
    ];

    private readonly IReadOnlyDictionary<string, ApplicationEventDefinition> _byType = Values.ToDictionary(value => value.EventType, StringComparer.Ordinal);

    public IReadOnlyList<ApplicationEventDefinition> GetAll() => Values;

    public IReadOnlyList<string> ListAvailableTypes() =>
        Values.Where(value =>
                permissions.TryGet(ApplicationPermissionIds.EventsSubscribe, out var subscribe) && subscribe.IsAvailable &&
                permissions.TryGet(value.ReadPermission, out var read) && read.IsAvailable &&
                (!value.IsLibraryScoped || permissions.TryGet(ApplicationPermissionIds.LibraryRead, out var library) && library.IsAvailable))
            .Select(value => value.EventType).Order(StringComparer.Ordinal).ToArray();

    public bool TryGet(string eventType, out ApplicationEventDefinition definition) =>
        _byType.TryGetValue(eventType, out definition!);

    private static ApplicationEventDefinition Definition(
        string eventType,
        ApplicationPermissionId permission,
        bool library,
        bool profile = false) => new(eventType, permission, library, profile);
}
