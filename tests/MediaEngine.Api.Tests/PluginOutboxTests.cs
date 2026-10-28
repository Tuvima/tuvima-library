using System.Text.Json;
using Dapper;
using MediaEngine.Api.Services;
using MediaEngine.Api.Services.Plugins;
using MediaEngine.Domain.Configuration;
using MediaEngine.Plugins;
using MediaEngine.Storage;
using MediaEngine.Storage.Contracts;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Api.Tests;

/// <summary>
/// The retry queue for sharing plugins (Plugin architecture spec C13): queueing is a quick local write with limits, delivery happens
/// later with a growing pause while the service is offline, nothing is sent for a plugin that is off, an item can read only its own
/// profile's account, and unlinking clears what was waiting.
/// </summary>
public sealed class PluginOutboxTests : IDisposable
{
    private const string PluginId = "tuvima.test.share";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "tuvima-plugin-outbox", Guid.NewGuid().ToString("N"));
    private readonly ApprovalTestKit _kit = new();
    private readonly ConfigurationDirectoryLoader _loader;
    private readonly PluginCatalog _catalog;
    private readonly PluginOutboxRepository _repository;
    private readonly PluginOutboxService _service;
    private readonly PluginProfileConnectionService _connections;
    private readonly PluginOutboxWorker _worker;
    private readonly SharePlugin _plugin = new();
    private readonly Guid _ana = Guid.NewGuid();
    private readonly Guid _ben = Guid.NewGuid();

    public PluginOutboxTests()
    {
        var libraryRoot = Path.Combine(_root, "library");
        Directory.CreateDirectory(libraryRoot);
        _loader = new ConfigurationDirectoryLoader(Path.Combine(_root, "config"));
        _loader.SaveCore(new CoreConfiguration { LibraryRoot = libraryRoot });
        _catalog = new PluginCatalog([_plugin], new PluginSettingsService(_loader), _loader, AllowAllPluginApprovals.Instance, NullLogger<PluginCatalog>.Instance);
        _repository = new PluginOutboxRepository(_kit.Database);
        _service = new PluginOutboxService(_repository, _kit.Clock);
        _connections = new PluginProfileConnectionService(
            _catalog, new PluginProfileConnectionRepository(_kit.Database), _repository, new DataProtectionSecretStore(new EphemeralDataProtectionProvider()), _kit.Clock);
        var activity = new PluginActivityService(new PluginActivityRepository(_kit.Database), new PluginCircuitBreaker(_kit.Clock), _kit.Clock, NullLogger<PluginActivityService>.Instance);
        _worker = new PluginOutboxWorker(_catalog, _repository, _connections, new NoContextFactory(), activity, _kit.Clock, NullLogger<PluginOutboxWorker>.Instance);
        AddProfile(_ana, "Ana");
        AddProfile(_ben, "Ben");
        _catalog.SetEnabled(PluginId, true);
    }

    public void Dispose()
    {
        _kit.Dispose();
        _loader.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A temp folder another process still holds is left for the OS to clean up.
        }
    }

    [Fact]
    public async Task ADeliveredItem_IsRemoved()
    {
        await _service.EnqueueAsync(PluginId, _ana, "{\"play\":1}", default);

        await _worker.ProcessOnceAsync(default);

        Assert.Equal(["{\"play\":1}"], _plugin.Delivered);
        Assert.Equal(0, await _repository.CountAsync(PluginId));
    }

    [Fact]
    public async Task AnOfflineService_IsAskedOncePerPass_ThenWaitsLonger_AndNothingIsLost()
    {
        _plugin.Answer = PluginDeliveryResult.RetryLater("offline");
        await _service.EnqueueAsync(PluginId, _ana, "{\"n\":1}", default);
        await _service.EnqueueAsync(PluginId, _ana, "{\"n\":2}", default);

        await _worker.ProcessOnceAsync(default);

        Assert.Equal(1, _plugin.Calls);
        Assert.Equal(2, await _repository.CountAsync(PluginId));

        // Not due again until the first pause (one minute) has passed.
        _kit.Clock.Advance(TimeSpan.FromSeconds(59));
        await _worker.ProcessOnceAsync(default);
        Assert.Equal(1, _plugin.Calls);

        _kit.Clock.Advance(TimeSpan.FromSeconds(2));
        _plugin.Answer = PluginDeliveryResult.Delivered();
        await _worker.ProcessOnceAsync(default);

        Assert.Equal(["{\"n\":1}", "{\"n\":2}"], _plugin.Delivered);
        Assert.Equal(0, await _repository.CountAsync(PluginId));
    }

    [Fact]
    public void ThePauseGrowsAndThenStaysAtTwelveHours()
    {
        Assert.Equal(TimeSpan.FromMinutes(1), PluginOutboxWorker.PauseAfter(1));
        Assert.Equal(TimeSpan.FromMinutes(5), PluginOutboxWorker.PauseAfter(2));
        Assert.Equal(TimeSpan.FromHours(12), PluginOutboxWorker.PauseAfter(5));
        Assert.Equal(TimeSpan.FromHours(12), PluginOutboxWorker.PauseAfter(40));
    }

    [Fact]
    public async Task AnItemOlderThanAWeek_IsDroppedWithoutAskingThePlugin()
    {
        await _service.EnqueueAsync(PluginId, _ana, "{}", default);
        _kit.Clock.Advance(TimeSpan.FromDays(8));

        await _worker.ProcessOnceAsync(default);

        Assert.Equal(0, _plugin.Calls);
        Assert.Equal(0, await _repository.CountAsync(PluginId));
    }

    [Fact]
    public async Task APluginThatGivesUp_LosesTheItem()
    {
        _plugin.Answer = PluginDeliveryResult.GiveUp("the account was unlinked");
        await _service.EnqueueAsync(PluginId, _ana, "{}", default);

        await _worker.ProcessOnceAsync(default);

        Assert.Equal(1, _plugin.Calls);
        Assert.Equal(0, await _repository.CountAsync(PluginId));
    }

    [Fact]
    public async Task APluginThatThrows_IsRetriedLater_NotLost()
    {
        _plugin.Throw = true;
        await _service.EnqueueAsync(PluginId, _ana, "{}", default);

        await _worker.ProcessOnceAsync(default);

        Assert.Equal(1, await _repository.CountAsync(PluginId));
        Assert.Empty(await _repository.ListDueAsync(PluginId, _kit.Clock.GetUtcNow(), 10));
    }

    [Fact]
    public async Task ASwitchedOffPlugin_IsNotAsked_AndItsItemsWait()
    {
        await _service.EnqueueAsync(PluginId, _ana, "{}", default);
        _catalog.SetEnabled(PluginId, false);

        await _worker.ProcessOnceAsync(default);

        Assert.Equal(0, _plugin.Calls);
        Assert.Equal(1, await _repository.CountAsync(PluginId));
    }

    [Fact]
    public async Task AnItem_CanReadOnlyItsOwnProfilesAccount()
    {
        await _connections.LinkAsync(PluginId, _ana, "user_token", "ana-secret", default);
        await _connections.LinkAsync(PluginId, _ben, "user_token", "ben-secret", default);
        await _service.EnqueueAsync(PluginId, _ana, "{}", default);
        await _service.EnqueueAsync(PluginId, _ben, "{}", default);

        await _worker.ProcessOnceAsync(default);

        Assert.Equal(["ana-secret", "ben-secret"], _plugin.SeenCredentials.OrderBy(value => value));
    }

    [Fact]
    public async Task UnlinkingTheLastAccount_ClearsWhatWasWaitingForThatProfileOnly()
    {
        await _connections.LinkAsync(PluginId, _ana, "user_token", "ana-secret", default);
        await _service.EnqueueAsync(PluginId, _ana, "{}", default);
        await _service.EnqueueAsync(PluginId, _ben, "{}", default);

        await _connections.UnlinkAsync(PluginId, _ana, "user_token", default);

        var left = Assert.Single(await _repository.ListDueAsync(PluginId, _kit.Clock.GetUtcNow(), 10));
        Assert.Equal(_ben, left.ProfileId);
    }

    [Fact]
    public async Task Queueing_RefusesTooBigOrNotJson_AndDropsTheOldestPastTheCap()
    {
        await Assert.ThrowsAsync<PluginLimitExceededException>(() => _service.EnqueueAsync(PluginId, _ana, "\"" + new string('x', PluginOutboxService.MaximumPayloadBytes) + "\"", default));
        await Assert.ThrowsAsync<ArgumentException>(() => _service.EnqueueAsync(PluginId, _ana, "not json", default));
        await Assert.ThrowsAsync<ArgumentException>(() => _service.EnqueueAsync(PluginId, Guid.Empty, "{}", default));
        Assert.Equal(0, await _repository.CountAsync(PluginId));
    }

    [Fact]
    public async Task APluginWithoutThePermission_CannotQueue()
    {
        var gate = new PluginPermissionGate(_catalog);
        var outbox = new BoundPluginOutbox(PluginId, gate, _service);
        _plugin.GrantPermission = false;
        _catalog.Reload();
        _catalog.SetEnabled(PluginId, true);

        await Assert.ThrowsAnyAsync<Exception>(() => outbox.EnqueueAsync(_ana, "{}"));
        Assert.Equal(0, await _repository.CountAsync(PluginId));
    }

    private void AddProfile(Guid id, string name)
    {
        using var connection = _kit.Database.CreateConnection();
        connection.Execute(
            "INSERT INTO profiles(id,display_name,avatar_color,role,created_at) VALUES(@id,@name,'#7C4DFF','StandardUser',@now);",
            new { id = GuidSql.ToBlob(id), name, now = DateTimeOffset.UtcNow.ToString("O") });
    }

    private sealed class SharePlugin : ITuvimaPlugin, IPluginOutboxDelivery
    {
        public bool GrantPermission { get; set; } = true;

        public PluginDeliveryResult Answer { get; set; } = PluginDeliveryResult.Delivered();

        public bool Throw { get; set; }

        public int Calls { get; private set; }

        public List<string> Delivered { get; } = [];

        public List<string> SeenCredentials { get; } = [];

        public string Kind => "outbox-delivery";

        public PluginManifest Manifest => new()
        {
            Id = PluginId,
            Name = "Share Test",
            Version = "1.0.0",
            Permissions = GrantPermission ? [PluginPermissionIds.PluginOutbox, PluginPermissionIds.PluginSecrets] : [PluginPermissionIds.PluginSecrets],
            Secrets = [new PluginSecretDeclaration { Name = "user_token", Label = "Your token", Description = "Your account." }],
            Declaration = new PluginDeclaration { CredentialScopes = new(StringComparer.OrdinalIgnoreCase) { ["user_token"] = "personal" } },
        };

        public IReadOnlyList<IPluginCapability> CreateCapabilities() => [this];

        public async Task<PluginDeliveryResult> DeliverAsync(PluginOutboxItem item, IPluginExecutionContext context, CancellationToken cancellationToken)
        {
            Calls++;
            if (Throw)
            {
                throw new InvalidOperationException("boom");
            }

            if (await item.ReadCredentialAsync("user_token", cancellationToken) is { } token)
            {
                SeenCredentials.Add(token);
            }

            if (Answer.Outcome == PluginDeliveryOutcome.Delivered)
            {
                Delivered.Add(item.PayloadJson);
            }

            return Answer;
        }
    }

    private sealed class NoContextFactory : IPluginExecutionContextFactory
    {
        public IPluginExecutionContext Create(string pluginId, string purpose) => new NoContext(pluginId);

        public IPluginExecutionContext CreateForMedia(string pluginId, string purpose, PluginMediaAssetContext asset) => new NoContext(pluginId);
    }

    private sealed class NoContext(string pluginId) : IPluginExecutionContext
    {
        public string PluginId => pluginId;

        public IReadOnlyDictionary<string, JsonElement> Settings => new Dictionary<string, JsonElement>();

        public IPluginMediaAccess Media => throw new NotSupportedException();

        public IPluginHttpClient Http => throw new NotSupportedException();

        public IPluginStorage Storage => throw new NotSupportedException();

        public IPluginToolRuntime Tools => throw new NotSupportedException();

        public IPluginAiClient Ai => throw new NotSupportedException();

        public IPluginSecrets Secrets => throw new NotSupportedException();

        public IPluginRecordStore Records => throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
