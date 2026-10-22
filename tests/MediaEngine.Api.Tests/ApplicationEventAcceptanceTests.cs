using MediaEngine.Api.Realtime;
using MediaEngine.Api.Services;
using MediaEngine.Api.Services.Events;
using MediaEngine.Contracts.Ingestion;
using MediaEngine.Contracts.Realtime;
using MediaEngine.Domain;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Events;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Api.Tests;

public sealed class ApplicationEventAcceptanceTests
{
    [Fact]
    public async Task DashboardFailure_DoesNotLoseDurableEventOrOtherRecipients()
    {
        var producer = new CaptureProducer();
        var projection = new ApplicationEventProjectionPublisher(producer,
            new MissingResourceResolver(), NullLogger<ApplicationEventProjectionPublisher>.Instance, new NoVaultPaths());
        var audiences = new IntercomAudienceRegistry();
        audiences.Add(new("failed", Guid.NewGuid(), Guid.NewGuid()));
        audiences.Add(new("healthy", Guid.NewGuid(), Guid.NewGuid()));
        var clients = new RecordingHubClients(() => Assert.Single(producer.Values));
        var publisher = new SignalREventPublisher(new RecordingHub(clients), audiences,
            new AllowedAudience(), projection, NullLogger<SignalREventPublisher>.Instance);

        await publisher.PublishAsync(SignalREvents.IngestionStarted,
            new IngestionStartedEvent("private-path", DateTimeOffset.UtcNow));

        Assert.Equal("ingestion.started", Assert.Single(producer.Values).EventType);
        Assert.Equal(new[] { "failed", "healthy" }, clients.Attempts.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Registry_UsesExactSourceTypesAndOnlyListsBackedPermissions()
    {
        var registry = new ApplicationEventRegistry(new PermissionRegistry());
        var expected = new[]
        {
            "ingestion.completed", "ingestion.failed", "ingestion.progress", "ingestion.started",
            "library.item_added", "library.item_removed", "library.item_updated",
            "metadata.review_required", "metadata.updated", "plugin.job_completed", "plugin.job_started",
            "provider.status_changed", "system.health_changed",
            // Shared with notification routes (one event list): raised by the notification service.
            "account.invited", "backup.completed", "backup.failed", "disk.low", "plugin.broken", "plugin.update_available",
            "review.waiting", "tool.missing",
        };

        Assert.Equal(expected.Concat(new[] { "playback.started", "playback.paused", "playback.stopped", "playback.completed" })
            .Order(StringComparer.Ordinal), registry.ListAvailableTypes());
        Assert.Equal(4, registry.ListAvailableTypes().Count(value => value.StartsWith("playback.", StringComparison.Ordinal)));
        Assert.Equal(25, registry.GetAll().Count);
    }

    [Fact]
    public async Task Projection_EmitsEveryBackedCoreFactWithoutPrivatePathsOrHashes()
    {
        var producer = new CaptureProducer();
        var libraryId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        var projection = new ApplicationEventProjectionPublisher(
            producer, new FixedResourceResolver(libraryId), NullLogger<ApplicationEventProjectionPublisher>.Instance, new NoVaultPaths());

        await projection.ProjectAsync(SignalREvents.IngestionStarted, new IngestionStartedEvent("C:/secret/file.mkv", DateTimeOffset.UtcNow), default);
        await projection.ProjectAsync(SignalREvents.IngestionCompleted, new IngestionCompletedEvent("C:/secret/file.mkv", "Movies", DateTimeOffset.UtcNow), default);
        await projection.ProjectAsync(SignalREvents.IngestionQueueProgress, new IngestionQueueProgressDto { State = "working", FilesFound = 2, FilesDone = 1 }, default);
        await projection.ProjectAsync(SignalREvents.IngestionQueueProgress, new IngestionQueueProgressDto { State = "idle", FilesFound = 2, FilesDone = 2, Progress = 1 }, default);
        await projection.ProjectAsync(SignalREvents.IngestionFailed, new IngestionFailedEvent("C:/secret/file.mkv", "secret error", DateTimeOffset.UtcNow), default);
        await projection.ProjectAsync(SignalREvents.MediaAdded, new MediaAddedEvent(assetId, null, "Movie", "Private title"), default);
        await projection.ProjectAsync(SignalREvents.MediaRemoved, new MediaRemovedEvent(assetId, "C:/secret/file.mkv", "Orphaned"), default);
        await projection.ProjectAsync(SignalREvents.MetadataHarvested, new MetadataHarvestedEvent(assetId, "provider", ["title"]), default);
        await projection.ProjectAsync(SignalREvents.ReviewItemCreated, new ReviewItemCreatedEvent(Guid.NewGuid(), assetId, "uncertain", "Private title"), default);
        await projection.ProjectAsync(SignalREvents.ProviderStatusChanged, new ProviderStatusChangedEvent("tmdb", "Down", "secret error"), default);
        await projection.ProjectAsync(SignalREvents.FolderHealthChanged, new FolderHealthChangedEvent("C:/secret", false, false, false, DateTimeOffset.UtcNow), default);

        Assert.Equal(new[]
        {
            "ingestion.completed", "ingestion.failed", "ingestion.progress", "ingestion.started",
            "library.item_added", "library.item_removed", "library.item_updated",
            "metadata.review_required", "metadata.updated", "provider.status_changed", "system.health_changed",
        }, producer.Values.Select(value => value.EventType).Distinct().Order(StringComparer.Ordinal));
        var serialized = string.Join('\n', producer.Values.Select(value => value.Payload.GetRawText()));
        Assert.DoesNotContain("C:/secret", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret error", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Private title", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.All(producer.Values.Where(value => value.EventType.StartsWith("library.") || value.EventType.StartsWith("metadata.")),
            value => Assert.Equal(libraryId, value.Subject.LibraryId));
        Assert.All(producer.Values.Where(value => value.Subject.LibraryId is not null),
            value => Assert.Equal(AccountFeatureId.Watch, value.Subject.FeatureId));
        Assert.Single(producer.Values, value => value.EventType == "ingestion.completed");
    }

    [Fact]
    public async Task QueueProgress_ProjectsProgressWhileWorkingAndCompletedOnceWhenTheQueueGoesIdle()
    {
        var producer = new CaptureProducer();
        var projection = new ApplicationEventProjectionPublisher(
            producer, new MissingResourceResolver(), NullLogger<ApplicationEventProjectionPublisher>.Instance, new NoVaultPaths());

        await projection.ProjectAsync(SignalREvents.IngestionQueueProgress, Queue("idle"), default);
        await projection.ProjectAsync(SignalREvents.IngestionQueueProgress, Queue("working", found: 4, done: 1, progress: 0.25), default);
        await projection.ProjectAsync(SignalREvents.IngestionQueueProgress, Queue("working", found: 4, done: 3, progress: 0.75), default);
        await projection.ProjectAsync(SignalREvents.IngestionQueueProgress, Queue("idle", found: 4, done: 4, progress: 1), default);
        await projection.ProjectAsync(SignalREvents.IngestionQueueProgress, Queue("idle", found: 4, done: 4, progress: 1), default);

        Assert.Equal(
            ["ingestion.progress", "ingestion.progress", "ingestion.completed"],
            producer.Values.Select(value => value.EventType));
        Assert.All(producer.Values, value =>
        {
            Assert.Equal("ingestion", value.Subject.Type);
            Assert.Equal("current", value.Subject.Id);
        });
        var progress = producer.Values[1].Payload;
        Assert.Equal(4, progress.GetProperty("files_found").GetInt32());
        Assert.Equal(3, progress.GetProperty("files_done").GetInt32());
        Assert.Equal(0, progress.GetProperty("files_waiting").GetInt32());
        Assert.Equal(0.75, progress.GetProperty("progress").GetDouble());
        Assert.Equal("working", progress.GetProperty("state").GetString());
        var completed = producer.Values[2].Payload;
        Assert.Equal(4, completed.GetProperty("files_done").GetInt32());
        Assert.Equal("idle", completed.GetProperty("state").GetString());
    }

    [Fact]
    public async Task QueueProgress_StartingIdleOrWaitingOnATool_PublishesNothing()
    {
        var producer = new CaptureProducer();
        var projection = new ApplicationEventProjectionPublisher(
            producer, new MissingResourceResolver(), NullLogger<ApplicationEventProjectionPublisher>.Instance, new NoVaultPaths());

        await projection.ProjectAsync(SignalREvents.IngestionQueueProgress, Queue("idle"), default);
        await projection.ProjectAsync(SignalREvents.IngestionQueueProgress, Queue("waiting", found: 2, done: 0), default);
        await projection.ProjectAsync(SignalREvents.IngestionQueueProgress, Queue("idle", found: 2, done: 0), default);

        Assert.Empty(producer.Values);
    }

    [Fact]
    public async Task QueueProgress_ARunThatWaitsOnAToolBeforeFinishing_CompletesOnce()
    {
        var producer = new CaptureProducer();
        var projection = new ApplicationEventProjectionPublisher(
            producer, new MissingResourceResolver(), NullLogger<ApplicationEventProjectionPublisher>.Instance, new NoVaultPaths());

        await projection.ProjectAsync(SignalREvents.IngestionQueueProgress, Queue("working", found: 3, done: 1), default);
        await projection.ProjectAsync(SignalREvents.IngestionQueueProgress, Queue("waiting", found: 3, done: 1), default);
        await projection.ProjectAsync(SignalREvents.IngestionQueueProgress, Queue("working", found: 3, done: 2), default);
        await projection.ProjectAsync(SignalREvents.IngestionQueueProgress, Queue("waiting", found: 3, done: 2), default);
        await projection.ProjectAsync(SignalREvents.IngestionQueueProgress, Queue("idle", found: 3, done: 3, progress: 1), default);

        Assert.Equal(
            ["ingestion.progress", "ingestion.progress", "ingestion.completed"],
            producer.Values.Select(value => value.EventType));
    }

    private static IngestionQueueProgressDto Queue(string state, int found = 0, int done = 0, double progress = 0) => new()
    {
        State = state,
        FilesFound = found,
        FilesDone = done,
        Progress = progress,
    };

    [Fact]
    public async Task Projection_UsesConcreteAssetRatherThanLogicalWorkForDurableProvenance()
    {
        var producer = new CaptureProducer();
        var assetId = Guid.NewGuid();
        var workId = Guid.NewGuid();
        var resources = new RecordingResourceResolver(Guid.NewGuid());
        var projection = new ApplicationEventProjectionPublisher(
            producer, resources, NullLogger<ApplicationEventProjectionPublisher>.Instance, new NoVaultPaths());

        await projection.ProjectAsync(SignalREvents.MediaAdded,
            new MediaAddedEvent(workId, null, "Movies", "Title", assetId), default);

        Assert.Equal(assetId, Assert.Single(resources.AssetIds));
        var draft = Assert.Single(producer.Values);
        Assert.Equal(assetId.ToString("D"), draft.Subject.Id);
        Assert.Equal(AccountFeatureId.Watch, draft.Subject.FeatureId);
    }

    [Fact]
    public async Task RemovedProjection_UsesCapturedProvenanceWhenDeletedAssetNoLongerResolves()
    {
        var producer = new CaptureProducer();
        var assetId = Guid.NewGuid();
        var libraryId = Guid.NewGuid();
        var projection = new ApplicationEventProjectionPublisher(
            producer, new MissingResourceResolver(), NullLogger<ApplicationEventProjectionPublisher>.Instance, new NoVaultPaths());

        await projection.ProjectAsync(SignalREvents.MediaRemoved,
            new MediaRemovedEvent(assetId, "C:/removed.mkv", "Deleted", libraryId, AccountFeatureId.Watch.Value), default);

        var draft = Assert.Single(producer.Values);
        Assert.Equal("library.item_removed", draft.EventType);
        Assert.Equal(assetId.ToString("D"), draft.Subject.Id);
        Assert.Equal(libraryId, draft.Subject.LibraryId);
        Assert.Equal(AccountFeatureId.Watch, draft.Subject.FeatureId);
    }

    private sealed class CaptureProducer : IApplicationEventProducer
    {
        public List<ApplicationEventDraft> Values { get; } = [];
        public Task PublishAsync(ApplicationEventDraft value, CancellationToken ct = default)
        {
            Values.Add(value);
            return Task.CompletedTask;
        }
    }

    private sealed class AllowedAudience : IIntercomAudienceAuthorizer
    {
        public ValueTask<bool> CanReceiveAsync<T>(IntercomAudienceConnection connection, string eventName,
            T payload, CancellationToken ct = default) where T : notnull => ValueTask.FromResult(true);
    }

    private sealed class RecordingHub(IHubClients clients) : IHubContext<Intercom>
    {
        public IHubClients Clients => clients;
        public IGroupManager Groups => throw new NotSupportedException();
    }

    private sealed class RecordingHubClients(Action beforeSend) : IHubClients
    {
        public List<string> Attempts { get; } = [];
        public IClientProxy Client(string connectionId) => new RecordingClient(() =>
        {
            beforeSend();
            Attempts.Add(connectionId);
            if (connectionId == "failed")
            {
                throw new IOException("Disconnected test client");
            }
        });
        public IClientProxy All => throw new NotSupportedException("Unfiltered broadcasts are forbidden.");
        public IClientProxy AllExcept(IReadOnlyList<string> excluded) => throw new NotSupportedException();
        public IClientProxy Clients(IReadOnlyList<string> ids) => throw new NotSupportedException();
        public IClientProxy Group(string name) => throw new NotSupportedException();
        public IClientProxy GroupExcept(string name, IReadOnlyList<string> excluded) => throw new NotSupportedException();
        public IClientProxy Groups(IReadOnlyList<string> names) => throw new NotSupportedException();
        public IClientProxy User(string id) => throw new NotSupportedException();
        public IClientProxy Users(IReadOnlyList<string> ids) => throw new NotSupportedException();
    }

    private sealed class RecordingClient(Action send) : IClientProxy
    {
        public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default)
        {
            send();
            return Task.CompletedTask;
        }
    }

    private sealed class FixedResourceResolver(Guid libraryId) : IApplicationEventResourceResolver
    {
        public Task<ApplicationEventResourceProvenance?> ResolveAsync(Guid assetId, CancellationToken ct = default) =>
            Task.FromResult<ApplicationEventResourceProvenance?>(new(libraryId, AccountFeatureId.Watch));
    }

    private sealed class RecordingResourceResolver(Guid libraryId) : IApplicationEventResourceResolver
    {
        public List<Guid> AssetIds { get; } = [];
        public Task<ApplicationEventResourceProvenance?> ResolveAsync(Guid assetId, CancellationToken ct = default)
        {
            AssetIds.Add(assetId);
            return Task.FromResult<ApplicationEventResourceProvenance?>(new(libraryId, AccountFeatureId.Watch));
        }
    }

    private sealed class MissingResourceResolver : IApplicationEventResourceResolver
    {
        public Task<ApplicationEventResourceProvenance?> ResolveAsync(Guid assetId, CancellationToken ct = default) =>
            Task.FromResult<ApplicationEventResourceProvenance?>(null);
    }
}
