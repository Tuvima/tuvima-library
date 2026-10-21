using System.Text.Json;
using MediaEngine.Api.Services.Plugins;
using MediaEngine.Api.Services.ScheduledTasks;
using MediaEngine.Domain.Configuration;
using MediaEngine.Plugins;
using MediaEngine.Storage;
using MediaEngine.Storage.Contracts;
using MediaEngine.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Api.Tests;

/// <summary>A clock the test moves by hand, so "30 minutes later" takes no time.</summary>
internal sealed class MutableClock(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}

/// <summary>A recurring job a test plugin declares. Counts its runs and does whatever the test says.</summary>
internal sealed class TestScheduledJob(
    string key,
    string displayName,
    PluginSchedule schedule,
    Func<IPluginExecutionContext, CancellationToken, Task>? body = null) : IPluginScheduledJob
{
    private int _runs;
    private int _running;
    private int _maxRunning;

    public string Kind => PluginCapabilityKinds.ScheduledJobKind;
    public string Key { get; } = key;
    public string DisplayName { get; } = displayName;
    public PluginSchedule DefaultSchedule { get; } = schedule;
    public int Runs => Volatile.Read(ref _runs);
    public int MaxConcurrent => Volatile.Read(ref _maxRunning);

    public async Task RunAsync(IPluginExecutionContext context, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _runs);
        var running = Interlocked.Increment(ref _running);
        int seen;
        while (running > (seen = Volatile.Read(ref _maxRunning)) && Interlocked.CompareExchange(ref _maxRunning, running, seen) != seen)
        {
        }

        try
        {
            if (body is not null)
            {
                await body(context, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            Interlocked.Decrement(ref _running);
        }
    }
}

/// <summary>An event subscriber a test plugin declares. Keeps every event it was given.</summary>
internal sealed class TestEventSubscriber(IReadOnlyList<string> eventTypes) : IPluginEventSubscriber
{
    private readonly List<PluginEvent> _received = [];

    public string Kind => PluginCapabilityKinds.EventSubscriberKind;
    public IReadOnlyList<string> EventTypes { get; } = eventTypes;

    /// <summary>What the handler does with an event; unset means it just accepts it.</summary>
    public Func<PluginEvent, IPluginExecutionContext, CancellationToken, Task>? Handler { get; set; }

    public IReadOnlyList<PluginEvent> Received
    {
        get
        {
            lock (_received)
            {
                return _received.ToList();
            }
        }
    }

    public async Task HandleAsync(PluginEvent evt, IPluginExecutionContext context, CancellationToken cancellationToken)
    {
        lock (_received)
        {
            _received.Add(evt);
        }

        if (Handler is not null)
        {
            await Handler(evt, context, cancellationToken).ConfigureAwait(false);
        }
    }
}

/// <summary>Always-on work a test plugin declares. Counts its starts and does whatever the test says.</summary>
internal sealed class TestBackgroundService(Func<int, IPluginExecutionContext, CancellationToken, Task> body) : IPluginBackgroundService
{
    private int _starts;
    private int _cancelled;

    public string Kind => PluginCapabilityKinds.BackgroundServiceKind;
    public int Starts => Volatile.Read(ref _starts);
    public int Cancelled => Volatile.Read(ref _cancelled);

    public async Task RunAsync(IPluginExecutionContext context, CancellationToken stoppingToken)
    {
        var start = Interlocked.Increment(ref _starts);
        try
        {
            await body(start, context, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            Interlocked.Increment(ref _cancelled);
            throw;
        }
    }
}

/// <summary>
/// The reusable in-test plugin: it can store records (through the context it is given), declare recurring jobs, subscribe
/// to Library events and run always-on work. Declares all five host-service permissions unless told otherwise.
/// </summary>
internal sealed class HostServicesTestPlugin : ITuvimaPlugin
{
    public const string DefaultId = "test.host-services";

    public static readonly string[] AllHostPermissions =
    [
        PluginPermissionIds.PluginRecords,
        PluginPermissionIds.ScheduledWork,
        PluginPermissionIds.LibraryEvents,
        PluginPermissionIds.WatchActivity,
        PluginPermissionIds.BackgroundWork,
    ];

    private readonly List<IPluginCapability> _capabilities = [];

    public HostServicesTestPlugin(
        string id = DefaultId,
        IReadOnlyList<string>? permissions = null,
        string name = "Test plugin",
        IReadOnlyList<PluginNetworkDestination>? network = null,
        string? pagesJson = null)
    {
        Manifest = new PluginManifest
        {
            Id = id,
            Name = name,
            Version = "1.0.0",
            Permissions = permissions ?? AllHostPermissions,
            Network = network ?? [],
            Pages = pagesJson is null ? null : JsonDocument.Parse(pagesJson).RootElement.Clone(),
        };
    }

    public PluginManifest Manifest { get; }

    public IReadOnlyList<IPluginCapability> CreateCapabilities() => _capabilities.ToList();

    public TestScheduledJob AddJob(
        string key,
        PluginSchedule? schedule = null,
        Func<IPluginExecutionContext, CancellationToken, Task>? body = null,
        string? displayName = null)
    {
        var job = new TestScheduledJob(key, displayName ?? "Refresh the " + key, schedule ?? PluginSchedule.Every(TimeSpan.FromMinutes(30)), body);
        _capabilities.Add(job);
        return job;
    }

    public TestEventSubscriber Subscribe(params string[] eventTypes)
    {
        var subscriber = new TestEventSubscriber(eventTypes);
        _capabilities.Add(subscriber);
        return subscriber;
    }

    public TestBackgroundService RunInBackground(Func<int, IPluginExecutionContext, CancellationToken, Task> body)
    {
        var service = new TestBackgroundService(body);
        _capabilities.Add(service);
        return service;
    }
}

/// <summary>Builds contexts for test plugins: only the records store is real (over the test's own database).</summary>
internal sealed class HostServicesContextFactory(
    PluginCatalog catalog,
    IPluginPermissionGate gate,
    IPluginRecordRepository records,
    PluginHostLimits limits,
    PluginHostStatusRegistry statuses,
    TimeProvider clock,
    string root) : IPluginExecutionContextFactory
{
    public IPluginExecutionContext Create(string pluginId, string purpose)
    {
        var registration = catalog.Get(pluginId)
            ?? throw new UnauthorizedAccessException("The plugin is not registered.");
        if (!registration.Enabled || registration.LoadError is not null)
        {
            throw new UnauthorizedAccessException("The plugin is disabled or unavailable.");
        }

        var storage = new BoundPluginStorage(pluginId, Path.Combine(root, "contexts", Guid.NewGuid().ToString("N")), gate);
        return new PluginExecutionContext(
            pluginId,
            registration.Settings,
            null!,
            null!,
            storage,
            null!,
            null!,
            null!,
            new BoundPluginRecords(pluginId, gate, records, limits, statuses.Get(pluginId), clock));
    }

    public IPluginExecutionContext CreateForMedia(string pluginId, string purpose, PluginMediaAssetContext asset) =>
        throw new NotSupportedException();
}

/// <summary>
/// Everything the host-service tests need over one real temporary SQLite file: the plugin catalog, the per-plugin status,
/// the run-record writer, the event list and the record store. <see cref="Restart"/> builds all of it again over the same
/// file, the way an Engine restart would.
/// </summary>
internal sealed class HostServicesHarness : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"tuvima-plugin-host-{Guid.NewGuid():N}");
    private readonly string _databasePath;
    private readonly HostServicesTestPlugin[] _plugins;
    private DatabaseConnection _database;

    public HostServicesHarness(PluginHostLimits? limits = null, params HostServicesTestPlugin[] plugins)
    {
        DapperConfiguration.Configure();
        Directory.CreateDirectory(_root);
        _databasePath = Path.Combine(_root, "tuvima.db");
        _plugins = plugins.Length == 0 ? [new HostServicesTestPlugin()] : plugins;
        Limits = limits ?? PluginHostLimits.Default;
        // Starts at the real time: run records are stamped with the real clock, so the two stay comparable.
        Clock = new MutableClock(DateTimeOffset.UtcNow);
        _database = new DatabaseConnection(_databasePath);
        _database.InitializeSchema();
        _database.RunStartupChecks();
        Build();
    }

    public PluginHostLimits Limits { get; }
    public MutableClock Clock { get; }
    public HostServicesTestPlugin Plugin => _plugins[0];
    public ConfigurationDirectoryLoader Loader { get; private set; } = null!;
    public PluginCatalog Catalog { get; private set; } = null!;
    public PluginHostStatusRegistry Statuses { get; private set; } = null!;
    public IPluginPermissionGate Gate { get; private set; } = null!;
    public PluginRecordRepository Records { get; private set; } = null!;
    public PluginEventCursorRepository Cursors { get; private set; } = null!;
    public ApplicationEventRepository Events { get; private set; } = null!;
    public ScheduledTaskRunRepository Runs { get; private set; } = null!;
    public ScheduledTaskRunner Runner { get; private set; } = null!;
    public HostServicesContextFactory Contexts { get; private set; } = null!;
    public string LibraryRoot => Path.Combine(_root, "library");

    public PluginJobScheduler NewScheduler() =>
        new(Catalog, Statuses, Contexts, Runner, Runs, Limits, Clock, NullLogger<PluginJobScheduler>.Instance);

    public PluginEventDispatcher NewDispatcher() =>
        new(Catalog, Statuses, Contexts, Events, Cursors, Limits, Clock, NullLogger<PluginEventDispatcher>.Instance);

    public PluginBackgroundHost NewBackgroundHost() =>
        new(Catalog, Statuses, Contexts, Limits, Clock, NullLogger<PluginBackgroundHost>.Instance);

    public BoundPluginRecords RecordStoreFor(string pluginId) =>
        new(pluginId, Gate, Records, Limits, Statuses.Get(pluginId), Clock);

    public void Enable(string? pluginId = null) => Catalog.SetEnabled(pluginId ?? Plugin.Manifest.Id, true);

    public void Disable(string? pluginId = null) => Catalog.SetEnabled(pluginId ?? Plugin.Manifest.Id, false);

    /// <summary>Appends an event to the Library's event list and returns its sequence.</summary>
    public async Task<long> AppendEventAsync(string eventType)
    {
        var stored = await Events.AppendAsync(new MediaEngine.Domain.Events.StoredApplicationEvent(
            0,
            Guid.NewGuid(),
            eventType,
            1,
            Clock.GetUtcNow(),
            "test-server",
            new MediaEngine.Domain.Events.ApplicationEventSubject("work", Guid.NewGuid().ToString()),
            JsonSerializer.Serialize(new { title = "Dune" })));
        return stored.Sequence;
    }

    /// <summary>Starts everything over again on the same database file and the same plugins, like an Engine restart.</summary>
    public void Restart()
    {
        _database.Dispose();
        _database = new DatabaseConnection(_databasePath);
        _database.InitializeSchema();
        _database.RunStartupChecks();
        Loader.Dispose();
        Build();
    }

    private void Build()
    {
        var configDirectory = Path.Combine(_root, "config");
        Directory.CreateDirectory(configDirectory);
        Directory.CreateDirectory(LibraryRoot);
        Loader = new ConfigurationDirectoryLoader(configDirectory);
        Loader.SaveCore(new CoreConfiguration { LibraryRoot = LibraryRoot });
        Catalog = new PluginCatalog(
            _plugins,
            new PluginSettingsService(Loader),
            Loader,
            AllowAllPluginApprovals.Instance,
            NullLogger<PluginCatalog>.Instance);
        Statuses = new PluginHostStatusRegistry(Catalog, Clock);
        Gate = new PluginPermissionGate(Catalog);
        Records = new PluginRecordRepository(_database);
        Cursors = new PluginEventCursorRepository(_database);
        Events = new ApplicationEventRepository(_database);
        Runs = new ScheduledTaskRunRepository(_database);
        Runner = new ScheduledTaskRunner(Runs, NullLogger<ScheduledTaskRunner>.Instance);
        Contexts = new HostServicesContextFactory(Catalog, Gate, Records, Limits, Statuses, Clock, _root);
    }

    public static async Task EventuallyAsync(Func<bool> condition, string because, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new Xunit.Sdk.XunitException("Timed out waiting for: " + because);
            }

            await Task.Delay(20);
        }
    }

    public void Dispose()
    {
        _database.Dispose();
        Loader.Dispose();
        TestTemp.DeleteDatabase(_databasePath);
        if (Directory.Exists(_root))
        {
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (IOException)
            {
                // The temporary folder is test scaffolding; a file still held by the operating system is cleaned up with the temp folder.
            }
        }
    }
}
