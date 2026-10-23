using System.Collections.Concurrent;
using MediaEngine.Api.Realtime;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services.Events;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Events;
using MediaEngine.Storage;
using MediaEngine.TestSupport;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Api.Tests;

public sealed class ApplicationEventDispatcherTests : IDisposable
{
    private readonly string _databasePath;
    private readonly DatabaseConnection _database;
    private readonly ApplicationRepository _applications;
    private readonly ClientAuthorizationRepository _clients;
    private readonly AccountRepository _accounts;
    private readonly Guid _applicationId = Guid.NewGuid();
    private readonly Guid _credentialId = Guid.NewGuid();
    private readonly FakeHubContext _hub = new();
    private ServiceProvider? _services;

    public ApplicationEventDispatcherTests()
    {
        DapperConfiguration.Configure();
        _databasePath = Path.Combine(Path.GetTempPath(), $"tuvima_application_event_dispatch_{Guid.NewGuid():N}.db");
        _database = new DatabaseConnection(_databasePath);
        _database.InitializeSchema();
        _database.RunStartupChecks();
        _applications = new ApplicationRepository(_database);
        _clients = new ClientAuthorizationRepository(_database);
        _accounts = new AccountRepository(_database);
    }

    [Fact]
    public async Task NewSubscription_WatermarksExistingRowsAndIgnoresPollerLag()
    {
        var repository = new MemoryRepository([Event(1, "ingestion.started"), Event(2, "ingestion.started")]);
        var dispatcher = await CreateDispatcherAsync(repository);

        var result = await dispatcher.SubscribeAsync(Subscriber("connection", "ingestion.started"), null, default);
        await dispatcher.DispatchAsync(repository.Values[0]);
        await dispatcher.DispatchAsync(repository.Values[1]);
        var live = Event(3, "ingestion.started");
        await dispatcher.DispatchAsync(live);

        Assert.Equal(0, result.ReplayedCount);
        var sent = await Client("connection").WaitForAsync(ApplicationEventClientMethods.Event, 1);
        Assert.Single(sent);
        Assert.Equal(live.EventId, Assert.IsType<ApplicationEventEnvelope>(sent[0].Arguments[0]).EventId);
    }

    [Fact]
    public async Task Replay_PaginatesAcrossFilteredRowsAndDoesNotDuplicateLiveSequence()
    {
        var values = Enumerable.Range(1, 300)
            .Select(sequence => Event(sequence, sequence == 300 ? "ingestion.started" : "system.health_changed"))
            .ToArray();
        var repository = new MemoryRepository(values);
        var dispatcher = await CreateDispatcherAsync(repository);

        var result = await dispatcher.SubscribeAsync(
            Subscriber("connection", "ingestion.started"), values[0].EventId, default);
        await dispatcher.DispatchAsync(values[^1]);
        await dispatcher.DispatchAsync(Event(301, "ingestion.started"));

        Assert.False(result.GapDetected);
        Assert.Equal(1, result.ReplayedCount);
        var sent = await Client("connection").WaitForAsync(ApplicationEventClientMethods.Event, 2);
        Assert.Equal([values[^1].EventId], sent.Take(1).Select(message =>
            Assert.IsType<ApplicationEventEnvelope>(message.Arguments[0]).EventId));
        Assert.Equal(2, sent.Select(message => Assert.IsType<ApplicationEventEnvelope>(message.Arguments[0]).EventId).Distinct().Count());
    }

    [Fact]
    public async Task Replay_ReportsGapInsteadOfSilentlyDroppingBeyondQueueCapacity()
    {
        var values = Enumerable.Range(1, ApplicationEventDispatcher.QueueCapacity + 2)
            .Select(sequence => Event(sequence, "ingestion.started"))
            .ToArray();
        var repository = new MemoryRepository(values);
        var dispatcher = await CreateDispatcherAsync(repository);

        var result = await dispatcher.SubscribeAsync(
            Subscriber("connection", "ingestion.started"), values[0].EventId, default);

        Assert.True(result.GapDetected);
        Assert.Equal(0, result.ReplayedCount);
        Assert.Empty(Client("connection").Messages);
    }

    [Fact]
    public async Task QueueOverflow_CancelsPumpAndSendsBoundedGapNotification()
    {
        var repository = new MemoryRepository([]);
        var dispatcher = await CreateDispatcherAsync(repository);
        Client("connection").BlockEventMessages = true;
        await dispatcher.SubscribeAsync(Subscriber("connection", "ingestion.started"), null, default);

        for (var sequence = 1; sequence <= ApplicationEventDispatcher.QueueCapacity + 2; sequence++)
        {
            await dispatcher.DispatchAsync(Event(sequence, "ingestion.started"));
        }

        var gaps = await Client("connection").WaitForAsync(ApplicationEventClientMethods.Gap, 1);
        Assert.Single(gaps);
        Assert.IsType<ApplicationEventGapDto>(gaps[0].Arguments[0]);
    }

    [Fact]
    public async Task ConnectedServiceSubscriber_StopsAfterExactCredentialRevocation()
    {
        var repository = new MemoryRepository([]);
        var dispatcher = await CreateDispatcherAsync(repository);
        await dispatcher.SubscribeAsync(Subscriber("connection", "ingestion.started"), null, default);
        Assert.True(await _applications.RevokeCredentialAsync(
            _applicationId, _credentialId, DateTimeOffset.UtcNow));

        await dispatcher.DispatchAsync(Event(1, "ingestion.started"));
        await Task.Delay(150);

        Assert.Empty(Client("connection").Messages);
    }

    [Fact]
    public async Task MixedSubscribers_ReceiveOnlyTheirSelectedEventTypes()
    {
        var repository = new MemoryRepository([]);
        var dispatcher = await CreateDispatcherAsync(repository);
        await dispatcher.SubscribeAsync(Subscriber("ingestion-client", "ingestion.started"), null, default);
        await dispatcher.SubscribeAsync(Subscriber("health-client", "system.health_changed"), null, default);

        var ingestion = Event(1, "ingestion.started");
        var health = Event(2, "system.health_changed");
        await dispatcher.DispatchAsync(ingestion);
        await dispatcher.DispatchAsync(health);

        var ingestionMessages = await Client("ingestion-client").WaitForAsync(ApplicationEventClientMethods.Event, 1);
        var healthMessages = await Client("health-client").WaitForAsync(ApplicationEventClientMethods.Event, 1);
        Assert.Equal(ingestion.EventId, Assert.IsType<ApplicationEventEnvelope>(ingestionMessages[0].Arguments[0]).EventId);
        Assert.Equal(health.EventId, Assert.IsType<ApplicationEventEnvelope>(healthMessages[0].Arguments[0]).EventId);
        Assert.Single(ingestionMessages);
        Assert.Single(healthMessages);
    }

    [Fact]
    public async Task ReplacingConnection_IsNotRemovedWhenOldPumpFailsAfterCancellation()
    {
        var repository = new MemoryRepository([]);
        var dispatcher = await CreateDispatcherAsync(repository);
        var client = Client("connection");
        client.BlockEventMessages = true;
        client.ThrowOnCanceledEvent = true;
        await dispatcher.SubscribeAsync(Subscriber("connection", "ingestion.started"), null, default);
        await dispatcher.DispatchAsync(Event(1, "ingestion.started"));
        await WaitUntilAsync(() => client.BlockedSendStarted, TimeSpan.FromSeconds(3));

        await dispatcher.SubscribeAsync(Subscriber("connection", "ingestion.started"), null, default);
        client.BlockEventMessages = false;
        var live = Event(2, "ingestion.started");
        await dispatcher.DispatchAsync(live);

        var sent = await client.WaitForAsync(ApplicationEventClientMethods.Event, 1);
        Assert.Equal(live.EventId, Assert.IsType<ApplicationEventEnvelope>(sent[0].Arguments[0]).EventId);
    }

    [Fact]
    public async Task Poller_RetriesTransientStartupFailure()
    {
        var repository = new MemoryRepository([]) { BoundsFailuresRemaining = 1 };
        var dispatcher = await CreateDispatcherAsync(repository);
        var worker = new ApplicationEventDispatchWorker(
            repository, dispatcher, NullLogger<ApplicationEventDispatchWorker>.Instance);

        await worker.StartAsync(default);
        await WaitUntilAsync(() => repository.BoundsCalls >= 2, TimeSpan.FromSeconds(3));
        await worker.StopAsync(default);

        Assert.True(repository.BoundsCalls >= 2);
    }

    private async Task<ApplicationEventDispatcher> CreateDispatcherAsync(IApplicationEventRepository repository)
    {
        var now = DateTimeOffset.UtcNow;
        await _applications.InsertApplicationAsync(new MediaEngine.Domain.Entities.Application
        {
            Id = _applicationId,
            Name = "Event integration",
            ApplicationType = ApplicationType.ServerIntegration,
            IsEnabled = true,
            IsAdministrator = true,
            AuthorizationVersion = 1,
            CreatedAt = now,
            UpdatedAt = now,
        }, new HashSet<ApplicationPermissionId>());
        await _applications.InsertCredentialAsync(new ApplicationCredential
        {
            Id = _credentialId,
            ApplicationId = _applicationId,
            Name = "Event stream",
            CredentialHash = "test",
            CreatedAt = now,
        });
        var permissions = new PermissionRegistry();
        var registry = new ApplicationEventRegistry(permissions);
        var authorizer = new ApplicationEventSubscriptionAuthorizer(
            new AllowAuthorizationEvaluator(), _accounts, _applications, _clients, permissions, registry,
            new ApplicationEventDeliveryAuthorizer(_applications, permissions, registry), TimeProvider.System,
            new LibraryVisibilityRule(_accounts, new VaultLibraryRepository(_database)));
        _services = new ServiceCollection().AddSingleton(authorizer).BuildServiceProvider();
        return new ApplicationEventDispatcher(
            _hub, repository, _services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<ApplicationEventDispatcher>.Instance);
    }

    private FakeClientProxy Client(string connectionId) => _hub.GetClient(connectionId);

    private ApplicationEventSubscriber Subscriber(string connectionId, params string[] eventTypes) => new(
        connectionId,
        _applicationId,
        _credentialId,
        null,
        new(PrincipalKind.ServiceApplication, true, ApplicationId: _applicationId,
            ApplicationEnabled: true, ApplicationAuthorizationVersion: 1, ApplicationIsAdministrator: true),
        new HashSet<ApplicationPermissionId>(),
        eventTypes.ToHashSet(StringComparer.Ordinal),
        new HashSet<Guid>());

    private static StoredApplicationEvent Event(long sequence, string type) => new(
        sequence, Guid.NewGuid(), type, 1, DateTimeOffset.UtcNow, Guid.NewGuid().ToString("D"),
        new("server", "local"), "{}");

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var stop = DateTimeOffset.UtcNow + timeout;
        while (!condition() && DateTimeOffset.UtcNow < stop)
        {
            await Task.Delay(20);
        }

        Assert.True(condition());
    }

    public void Dispose()
    {
        _services?.Dispose();
        _database.Dispose();
        TestTemp.ReleaseSqlitePool(_databasePath);
        if (File.Exists(_databasePath))
        {
            File.Delete(_databasePath);
        }
    }

    private sealed class AllowAuthorizationEvaluator : IAuthorizationEvaluator
    {
        public ValueTask<AuthorizationDecision> EvaluateAsync(RequestAuthority authority, AuthorizationRequirement requirement,
            ResourceAuthorizationContext? resource, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(AuthorizationDecision.Allow());
    }

    private sealed class MemoryRepository(IEnumerable<StoredApplicationEvent> values) : IApplicationEventRepository
    {
        public List<StoredApplicationEvent> Values { get; } = values.OrderBy(value => value.Sequence).ToList();
        public int BoundsFailuresRemaining { get; set; }
        public int BoundsCalls { get; private set; }

        public Task<StoredApplicationEvent> AppendAsync(StoredApplicationEvent value, CancellationToken ct = default)
        {
            Values.Add(value);
            return Task.FromResult(value);
        }

        public Task<IReadOnlyList<StoredApplicationEvent>> ReadAfterAsync(long sequence, int limit, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<StoredApplicationEvent>>(Values.Where(value => value.Sequence > sequence).Take(limit).ToArray());

        public Task<long?> FindSequenceAsync(Guid eventId, CancellationToken ct = default) =>
            Task.FromResult<long?>(Values.SingleOrDefault(value => value.EventId == eventId)?.Sequence);

        public Task<ApplicationEventBounds> GetBoundsAsync(CancellationToken ct = default)
        {
            BoundsCalls++;
            if (BoundsFailuresRemaining-- > 0)
            {
                throw new SqliteException("transient", 5);
            }

            return Task.FromResult(new ApplicationEventBounds(
                Values.FirstOrDefault()?.Sequence, Values.FirstOrDefault()?.EventId,
                Values.LastOrDefault()?.Sequence, Values.LastOrDefault()?.EventId));
        }

        public Task<int> PruneAsync(DateTimeOffset olderThan, int retainNewest, CancellationToken ct = default) => Task.FromResult(0);
    }

    private sealed class FakeHubContext : IHubContext<ApplicationEventsHub>
    {
        private readonly ConcurrentDictionary<string, FakeClientProxy> _clients = new(StringComparer.Ordinal);
        public FakeHubContext() => Clients = new FakeHubClients(GetClient);
        public FakeClientProxy GetClient(string connectionId) => _clients.GetOrAdd(connectionId, _ => new());
        public IHubClients Clients { get; }
        public IGroupManager Groups { get; } = new FakeGroupManager();
    }

    private sealed class FakeHubClients(Func<string, FakeClientProxy> client) : IHubClients
    {
        public IClientProxy All => client("all");
        public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => client("all");
        public IClientProxy Client(string connectionId) => client(connectionId);
        public IClientProxy Clients(IReadOnlyList<string> connectionIds) => client(connectionIds.FirstOrDefault() ?? "all");
        public IClientProxy Group(string groupName) => client("all");
        public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => client("all");
        public IClientProxy Groups(IReadOnlyList<string> groupNames) => client("all");
        public IClientProxy User(string userId) => client("all");
        public IClientProxy Users(IReadOnlyList<string> userIds) => client("all");
    }

    private sealed class FakeGroupManager : IGroupManager
    {
        public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeClientProxy : IClientProxy
    {
        public ConcurrentQueue<Message> Messages { get; } = new();
        public bool BlockEventMessages { get; set; }
        public bool ThrowOnCanceledEvent { get; set; }
        public bool BlockedSendStarted { get; private set; }

        public async Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default)
        {
            if (BlockEventMessages && method == ApplicationEventClientMethods.Event)
            {
                BlockedSendStarted = true;
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException) when (ThrowOnCanceledEvent)
                {
                    throw new InvalidOperationException("Simulated transport failure after replacement.");
                }
            }
            Messages.Enqueue(new(method, args));
        }

        public async Task<IReadOnlyList<Message>> WaitForAsync(string method, int count)
        {
            await WaitUntilAsync(() => Messages.Count(message => message.Method == method) >= count, TimeSpan.FromSeconds(3));
            return Messages.Where(message => message.Method == method).ToArray();
        }
    }

    private sealed record Message(string Method, object?[] Arguments);
}
