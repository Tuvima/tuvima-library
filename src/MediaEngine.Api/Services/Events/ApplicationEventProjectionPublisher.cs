using System.Text.Json;
using MediaEngine.Api.Realtime;
using MediaEngine.Contracts.Ingestion;
using MediaEngine.Contracts.Realtime;
using MediaEngine.Domain;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Events;

namespace MediaEngine.Api.Services.Events;

public sealed class ApplicationEventProjectionPublisher(
    IApplicationEventProducer producer,
    IApplicationEventResourceResolver resources,
    ILogger<ApplicationEventProjectionPublisher> logger,
    IVaultPathClassifier vaultPaths)
{
    private const string WorkingState = "working";
    private const string IdleState = "idle";

    private readonly object _queueStateLock = new();
    private bool _runOpen;

    public async Task ProjectAsync<T>(string internalName, T payload, CancellationToken ct) where T : notnull
    {
        try
        {
            var draft = await MapAsync(internalName, payload, ct).ConfigureAwait(false);
            if (draft is not null)
            {
                await producer.PublishAsync(draft, ct).ConfigureAwait(false);
                if (draft.EventType == "metadata.updated")
                {
                    await producer.PublishAsync(draft with
                    {
                        EventType = "library.item_updated",
                        Payload = JsonSerializer.SerializeToElement(new { action = "metadata_updated" }),
                    }, ct).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "External application event projection failed for {EventName}", internalName);
        }
    }

    private async Task<ApplicationEventDraft?> MapAsync<T>(string name, T value, CancellationToken ct) where T : notnull
    {
        var now = DateTimeOffset.UtcNow;

        // A file or folder in a Vault library is never reported to an app or a webhook, not even as "ingestion started" or a folder
        // health change (Videos spec D17, D33). Events about an item are stopped by id further down and by the data structure.
        if (EventPath(value) is { } path && await vaultPaths.IsVaultPathAsync(path, ct).ConfigureAwait(false))
        {
            return null;
        }

        return (name, value) switch
        {
            (SignalREvents.IngestionStarted, IngestionStartedEvent e) => Draft("ingestion.started", "ingestion", "current", e.StartedAt, new { status = "started" }),
            (SignalREvents.IngestionQueueProgress, IngestionQueueProgressDto e) => QueueProgressDraft(e, now),
            (SignalREvents.IngestionCompleted, IngestionCompletedEvent) => null,
            (SignalREvents.IngestionFailed, IngestionFailedEvent e) => Draft("ingestion.failed", "ingestion", "current", e.FailedAt, new { status = "failed" }),
            (SignalREvents.ProviderStatusChanged, ProviderStatusChangedEvent e) => Draft("provider.status_changed", "provider", e.ProviderId, now, new { e.Status }),
            (SignalREvents.FolderHealthChanged, FolderHealthChangedEvent e) => Draft("system.health_changed", "system-component", "storage", e.CheckedAt,
                new { component = "storage", status = e.IsAccessible && e.HasRead ? "healthy" : "degraded", e.IsAccessible, e.HasRead, e.HasWrite }),
            (SignalREvents.MediaAdded, MediaAddedEvent e) => await LibraryDraftAsync("library.item_added", e.AssetId ?? e.WorkId, e.MediaType, "added", now, ct, e.WorkId).ConfigureAwait(false),
            (SignalREvents.MediaRemoved, MediaRemovedEvent e) => await LibraryDraftAsync(
                "library.item_removed", e.AssetId, null, "removed", now, ct, provenance: EmbeddedProvenance(e)).ConfigureAwait(false),
            (SignalREvents.MetadataHarvested, MetadataHarvestedEvent e) => await MetadataDraftAsync(e.EntityId, e.ProviderName, e.UpdatedFields, now, ct).ConfigureAwait(false),
            (SignalREvents.HydrationStageCompleted, HydrationStageCompletedEvent e) => await MetadataDraftAsync(e.EntityId, e.ProviderName, [], now, ct, e.Stage).ConfigureAwait(false),
            (SignalREvents.ReviewItemCreated, ReviewItemCreatedEvent e) => await ReviewDraftAsync(e.ReviewItemId, e.EntityId, e.Trigger, now, ct).ConfigureAwait(false),
            _ => null,
        };
    }

    private static string? EventPath<T>(T value) where T : notnull => value switch
    {
        IngestionStartedEvent e => e.FilePath,
        IngestionHashedEvent e => e.FilePath,
        IngestionCompletedEvent e => e.FilePath,
        AutoOrganizedIngestionCompletedEvent e => e.Path,
        IngestionFailedEvent e => e.FilePath,
        FolderHealthChangedEvent e => e.Path,
        _ => null,
    };

    // The live ingestion numbers are the external progress: "ingestion.progress" while files are being worked on, and
    // "ingestion.completed" once when a run returns to idle. Waiting for a missing tool neither completes a run nor starts
    // a new one, so working, waiting, idle completes once; a repeated idle snapshot says nothing new.
    private ApplicationEventDraft? QueueProgressDraft(IngestionQueueProgressDto value, DateTimeOffset at)
    {
        bool completed;
        lock (_queueStateLock)
        {
            completed = _runOpen && value.State == IdleState;
            if (value.State == WorkingState)
            {
                _runOpen = true;
            }
            else if (value.State == IdleState)
            {
                _runOpen = false;
            }
        }

        var payload = new
        {
            files_found = value.FilesFound,
            files_done = value.FilesDone,
            files_waiting = value.FilesWaiting,
            progress = value.Progress,
            state = value.State,
        };
        if (value.State == WorkingState)
        {
            return Draft("ingestion.progress", "ingestion", "current", at, payload);
        }

        return completed ? Draft("ingestion.completed", "ingestion", "current", at, payload) : null;
    }

    private async Task<ApplicationEventDraft?> LibraryDraftAsync(string type, Guid assetId, string? mediaType, string action, DateTimeOffset at, CancellationToken ct, Guid? workId = null,
        ApplicationEventResourceProvenance? provenance = null)
    {
        provenance ??= await ResolveAsync(assetId, ct).ConfigureAwait(false);
        return provenance is null ? null : Draft(type, "media-asset", assetId.ToString("D"), at,
            new { action, media_type = mediaType, work_id = workId }, provenance);
    }

    private async Task<ApplicationEventDraft?> MetadataDraftAsync(Guid entityId, string provider, IReadOnlyList<string> fields, DateTimeOffset at, CancellationToken ct, int? stage = null)
    {
        var provenance = await ResolveAsync(entityId, ct).ConfigureAwait(false);
        return provenance is null ? null : Draft("metadata.updated", "media-asset", entityId.ToString("D"), at, new { provider, updated_fields = fields, stage }, provenance);
    }

    private async Task<ApplicationEventDraft?> ReviewDraftAsync(Guid reviewId, Guid entityId, string trigger, DateTimeOffset at, CancellationToken ct)
    {
        var provenance = await ResolveAsync(entityId, ct).ConfigureAwait(false);
        return provenance is null ? null : Draft("metadata.review_required", "review-item", reviewId.ToString("D"), at, new { entity_id = entityId, trigger }, provenance);
    }

    private Task<ApplicationEventResourceProvenance?> ResolveAsync(Guid entityId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return resources.ResolveAsync(entityId, ct);
    }

    private static ApplicationEventResourceProvenance? EmbeddedProvenance(MediaRemovedEvent value)
    {
        if (value.LibraryId is not { } libraryId || string.IsNullOrWhiteSpace(value.FeatureId))
        {
            return null;
        }

        var feature = new AccountFeatureId(value.FeatureId);
        return feature == AccountFeatureId.Read || feature == AccountFeatureId.Watch || feature == AccountFeatureId.Listen
            ? new(libraryId, feature)
            : null;
    }

    private static ApplicationEventDraft Draft(string type, string subjectType, string subjectId, DateTimeOffset at, object payload, ApplicationEventResourceProvenance? provenance = null) =>
        new(type, 1, at, new(subjectType, subjectId, provenance?.LibraryId, FeatureId: provenance?.FeatureId), JsonSerializer.SerializeToElement(payload));
}
