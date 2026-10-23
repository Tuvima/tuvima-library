using Dapper;
using MediaEngine.Api.Services.Plugins;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Playback;
using MediaEngine.Plugins;
using MediaEngine.Storage;
using MediaEngine.Storage.Playback;

namespace MediaEngine.Api.Tests;

/// <summary>
/// Watching a Vault video must not reach a plugin (Videos spec D33). A test plugin that is switched on, approved and subscribed to
/// playback events gets the event for an ordinary video and nothing for a Vault one, through the real playback recording and the
/// real event dispatcher.
/// </summary>
public sealed class PluginVaultPlaybackIsolationTests : IDisposable
{
    private readonly List<IDisposable> _disposables = [];
    private readonly DateTimeOffset _now = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);
    private readonly Guid _account = Guid.NewGuid();
    private readonly Guid _profile = Guid.NewGuid();

    public void Dispose()
    {
        foreach (var disposable in _disposables)
        {
            disposable.Dispose();
        }
    }

    [Fact]
    public async Task APlaybackEventForAVaultVideo_NeverReachesATestPlugin_ButOneForAnOrdinaryVideoDoes()
    {
        var plugin = new HostServicesTestPlugin();
        var subscriber = plugin.Subscribe("playback.started", "playback.paused", "playback.stopped", "playback.completed");
        var host = new HostServicesHarness(plugins: plugin);
        _disposables.Add(host);
        host.Enable();
        var dispatcher = host.NewDispatcher();
        await dispatcher.DispatchOnceAsync(CancellationToken.None);

        var vaultLibrary = Guid.NewGuid();
        var normalLibrary = Guid.NewGuid();
        await new VaultLibraryRepository(host.Database).AddAsync(
            new VaultLibrary(vaultLibrary.ToString("D"), VaultLookupSources.None, null, _now, null, _now, _now));
        SeedProfile(host.Database);
        var vault = SeedVideo(host.Database, vaultLibrary);
        var normal = SeedVideo(host.Database, normalLibrary);
        var telemetry = new PlaybackTelemetryRepository(host.Database, new ApplicationEventOutboxWriter());

        await Observe(telemetry, vault, vaultLibrary, PlaybackTelemetryStates.Playing, 0);
        await Observe(telemetry, vault, vaultLibrary, PlaybackTelemetryStates.Paused, 30);
        await Observe(telemetry, vault, vaultLibrary, PlaybackTelemetryStates.Stopped, 60);
        await dispatcher.DispatchOnceAsync(CancellationToken.None);
        Assert.Empty(subscriber.Received);

        await Observe(telemetry, normal, normalLibrary, PlaybackTelemetryStates.Playing, 0);
        await dispatcher.DispatchOnceAsync(CancellationToken.None);
        Assert.Equal(["playback.started"], subscriber.Received.Select(received => received.Type));
        Assert.DoesNotContain(vault.ToString("D"), subscriber.Received.Single().PayloadJson, StringComparison.OrdinalIgnoreCase);
    }

    private Task<IReadOnlyList<PlaybackTelemetryTransition>> Observe(
        PlaybackTelemetryRepository telemetry, Guid asset, Guid library, string state, double position) =>
        telemetry.ObserveAsync(new PlaybackTelemetryObservation(
            SessionOf(asset), _account, _profile, null, null, Guid.NewGuid(), asset, library,
            AccountFeatureId.Watch, "Videos", state, _now.AddSeconds(position), position, null, null, 1d, false, false, null,
            new(PlaybackTelemetryDeliveryModes.DirectPlay), new("web")));

    private readonly Dictionary<Guid, Guid> _sessions = [];

    private Guid SessionOf(Guid asset)
    {
        if (!_sessions.TryGetValue(asset, out var session))
        {
            _sessions[asset] = session = Guid.NewGuid();
        }

        return session;
    }

    private void SeedProfile(DatabaseConnection database)
    {
        using var connection = database.CreateConnection();
        connection.Execute(
            """
            INSERT INTO profiles(id,display_name,avatar_color,role,created_at) VALUES(@profile,'Viewer','#000000','StandardUser',@now);
            INSERT INTO accounts(id,email,normalized_email,is_enabled,authorization_version,created_at,updated_at,profile_id)
            VALUES(@account,@email,@normalized,1,1,@now,@now,@profile);
            """,
            new { profile = _profile, account = _account, email = $"{_account:N}@example.test", normalized = $"{_account:N}@EXAMPLE.TEST", now = _now.ToString("O") });
    }

    private static Guid SeedVideo(DatabaseConnection database, Guid library)
    {
        var work = Guid.NewGuid();
        var edition = Guid.NewGuid();
        var asset = Guid.NewGuid();
        using var connection = database.CreateConnection();
        connection.Execute(
            """
            INSERT INTO works(id,media_type,work_kind,is_catalog_only,ownership) VALUES(@work,'Videos','standalone',0,'Owned');
            INSERT INTO editions(id,work_id) VALUES(@edition,@work);
            INSERT INTO media_assets(id,edition_id,content_hash,file_path_root,presented_at,library_id,status)
            VALUES(@asset,@edition,@hash,@path,CURRENT_TIMESTAMP,@library,'Normal');
            """,
            new { work, edition, asset, hash = asset.ToString("N"), path = $"C:/videos/{asset:N}.mkv", library = library.ToString("D") });
        return asset;
    }
}
