using Dapper;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Playback;
using MediaEngine.Storage.Playback;
using MediaEngine.TestSupport;

namespace MediaEngine.Storage.Tests;

/// <summary>
/// Watching a Vault video leaves no trace outside Vault mode (Videos spec D33). The places that remember what was watched, feed the
/// taste summary, or tell apps, webhooks and plugins about playback must hold nothing for a Vault file, while an ordinary file in the
/// same situation is remembered as before. Real temp SQLite.
/// </summary>
public sealed class VaultPlaybackIsolationTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"tuvima-vault-playback-{Guid.NewGuid():N}.db");
    private readonly DatabaseConnection _database;
    private readonly VaultLibraryRepository _vaultLibraries;
    private readonly PlaybackTelemetryRepository _telemetry;
    private readonly ApplicationEventRepository _events;
    private readonly DateTimeOffset _now = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);
    private readonly Guid _vaultLibrary = Guid.NewGuid();
    private readonly Guid _normalLibrary = Guid.NewGuid();
    private readonly Guid _account = Guid.NewGuid();
    private readonly Guid _profile = Guid.NewGuid();

    public VaultPlaybackIsolationTests()
    {
        DapperConfiguration.Configure();
        _database = new DatabaseConnection(_path);
        _database.InitializeSchema();
        _database.RunStartupChecks();
        _vaultLibraries = new VaultLibraryRepository(_database);
        _telemetry = new PlaybackTelemetryRepository(_database, new ApplicationEventOutboxWriter());
        _events = new ApplicationEventRepository(_database);
        _vaultLibraries.AddAsync(new VaultLibrary(_vaultLibrary.ToString("D"), VaultLookupSources.None, null, _now, null, _now, _now))
            .GetAwaiter().GetResult();
        using var connection = _database.CreateConnection();
        connection.Execute(
            """
            INSERT INTO profiles(id,display_name,avatar_color,role,created_at) VALUES(@profile,'Viewer','#000000','StandardUser',@now);
            INSERT INTO accounts(id,email,normalized_email,is_enabled,is_administrator,authorization_version,created_at,updated_at)
            VALUES(@account,@email,@normalized,1,0,1,@now,@now);
            """,
            new { profile = _profile, account = _account, email = $"{_account:N}@example.test", normalized = $"{_account:N}@EXAMPLE.TEST", now = _now.ToString("O") });
    }

    public void Dispose()
    {
        _database.Dispose();
        TestTemp.ReleaseSqlitePool(_path);
        foreach (var file in new[] { _path, _path + "-wal", _path + "-shm" })
        {
            if (File.Exists(file))
            {
                File.Delete(file);
            }
        }
    }

    // ── Playback records: who is watching, Now playing, analytics, history ──────────────────────

    [Fact]
    public async Task PlayingAVaultVideo_IsNeverRecorded_ButPlayingAnOrdinaryOneIs()
    {
        var vault = AddVideo(_vaultLibrary);
        var normal = AddVideo(_normalLibrary);

        Assert.Empty(await Observe(vault));
        var ordinary = Assert.Single(await Observe(normal));
        Assert.True(ordinary.WasCreated);

        using var connection = _database.CreateConnection();
        Assert.Equal(1, connection.ExecuteScalar<long>("SELECT COUNT(*) FROM playback_telemetry_sessions;"));
        Assert.Equal(normal.AssetId, Assert.Single(await ActiveForAdministrator()).AssetId);
    }

    [Fact]
    public async Task AnAdministratorsActivityViews_ShowNothingForAVaultVideo_Sessions_Analytics_AndGroups()
    {
        var vault = AddVideo(_vaultLibrary);
        await Observe(vault);
        await Observe(vault, state: PlaybackTelemetryStates.Paused, at: _now.AddSeconds(30), position: 30);

        Assert.Empty(await ActiveForAdministrator());
        var aggregate = await _telemetry.GetPlaybackAggregateAsync(AllLibrariesScope(), null, null);
        Assert.Equal(0, aggregate.SessionCount);
        Assert.Equal(0, aggregate.UniqueAssets);
    }

    [Fact]
    public async Task APlaybackEventForAVaultVideo_IsNeverEmittedToWebhooksOrPlugins_ButAnOrdinaryOneIs()
    {
        var vault = AddVideo(_vaultLibrary);
        var normal = AddVideo(_normalLibrary);

        await Observe(vault);
        await Observe(vault, state: PlaybackTelemetryStates.Paused, at: _now.AddSeconds(30), position: 30);
        Assert.Empty(await _events.ReadAfterAsync(0, 50));

        await Observe(normal);
        // Webhooks and plugins both read this one list, so an event that is not here reaches neither.
        Assert.Equal(["playback.started"], (await _events.ReadAfterAsync(0, 50)).Select(value => value.EventType));
    }

    [Fact]
    public async Task ADeviceStateForAVaultVideo_IsNeverKept_SoNoOtherDeviceOrCompanionCanReadWhatIsPlaying()
    {
        var vault = AddVideo(_vaultLibrary);
        var normal = AddVideo(_normalLibrary);
        var deviceStates = new PlayerDeviceStateRepository(_database);

        await deviceStates.UpsertAsync(DeviceState(vault.AssetId, "phone"));
        await deviceStates.UpsertAsync(DeviceState(normal.AssetId, "television"));

        using var connection = _database.CreateConnection();
        Assert.Equal(["television"], connection.Query<string>("SELECT device_id FROM player_device_states;").ToList());
    }

    // ── Taste summary, recommendations and "previously on" sources ──────────────────────────────

    [Fact]
    public async Task TheTasteSignals_NeverIncludeAVaultVideo_ButIncludeOrdinaryOnes()
    {
        var vault = AddVideo(_vaultLibrary);
        var normal = AddVideo(_normalLibrary);
        Play(vault.AssetId, "2026-10-21T10:00:00Z");
        Play(normal.AssetId, "2026-10-21T09:00:00Z");

        var signals = await new TasteProfileRepository(_database).GetSignalsAsync(_profile, 50);

        Assert.Equal(normal.AssetId, Assert.Single(signals).AssetId);
    }

    [Fact]
    public async Task TheRecentProgressWithoutAnAuthorizedList_NeverIncludesAVaultVideo()
    {
        var vault = AddVideo(_vaultLibrary);
        var normal = AddVideo(_normalLibrary);
        Play(vault.AssetId, "2026-10-21T10:00:00Z");
        Play(normal.AssetId, "2026-10-21T09:00:00Z");
        var states = new UserStateRepository(_database);

        var unrestricted = await states.GetRecentAsync(_profile, 50);
        Assert.Equal(normal.AssetId, Assert.Single(unrestricted).AssetId);

        // With a list the caller was authorized for (an unlocked session gets its Vault in it), the list decides.
        var unlocked = await states.GetRecentAuthorizedAsync(_profile, new HashSet<Guid> { vault.AssetId }, 50);
        Assert.Equal(vault.AssetId, Assert.Single(unlocked).AssetId);
    }

    [Fact]
    public async Task TheSpoilerRuleProgress_NeverIncludesAVaultVideo()
    {
        var vault = AddVideo(_vaultLibrary);
        var normal = AddVideo(_normalLibrary);
        Play(vault.AssetId, "2026-10-21T10:00:00Z");
        Play(normal.AssetId, "2026-10-21T09:00:00Z");

        var started = await new ViewerProgressRepository(_database).GetStartedWorksAsync(_profile);

        Assert.Equal(normal.WorkId, Assert.Single(started).WorkId);
    }

    // ── The Engine's own activity list ──────────────────────────────────────────────────────────

    [Fact]
    public async Task AnActivityEntryAboutAVaultFile_IsNeverKept_ButOrdinaryAndUnattachedEntriesAre()
    {
        var vault = AddVideo(_vaultLibrary);
        var normal = AddVideo(_normalLibrary);
        var activity = new SystemActivityRepository(_database);

        await activity.LogAsync(Entry("vault-asset", vault.AssetId));
        await activity.LogAsync(Entry("vault-work", vault.WorkId));
        await activity.LogAsync(Entry("normal-asset", normal.AssetId));
        await activity.LogAsync(Entry("no-entity", null));

        var kept = (await activity.GetRecentAsync(50)).Select(entry => entry.Detail).Order().ToList();
        Assert.Equal(["no-entity", "normal-asset"], kept);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────────

    private sealed record Video(Guid WorkId, Guid AssetId, Guid LibraryId);

    private Video AddVideo(Guid library)
    {
        var work = Guid.NewGuid();
        var edition = Guid.NewGuid();
        var asset = Guid.NewGuid();
        using var connection = _database.CreateConnection();
        connection.Execute(
            """
            INSERT INTO works(id,media_type,work_kind,is_catalog_only,ownership) VALUES(@work,'Videos','standalone',0,'Owned');
            INSERT INTO editions(id,work_id) VALUES(@edition,@work);
            INSERT INTO media_assets(id,edition_id,content_hash,file_path_root,presented_at,library_id,status)
            VALUES(@asset,@edition,@hash,@path,CURRENT_TIMESTAMP,@library,'Normal');
            """,
            new { work, edition, asset, hash = asset.ToString("N"), path = $"C:/videos/{asset:N}.mkv", library = library.ToString("D") });
        return new Video(work, asset, library);
    }

    private void Play(Guid asset, string lastAccessed)
    {
        using var connection = _database.CreateConnection();
        connection.Execute(
            "INSERT INTO user_states(user_id,asset_id,progress_pct,last_accessed) VALUES(@profile,@asset,40,@lastAccessed);",
            new { profile = _profile, asset, lastAccessed });
    }

    private Task<IReadOnlyList<PlaybackTelemetryTransition>> Observe(
        Video video, string state = PlaybackTelemetryStates.Playing, DateTimeOffset? at = null, double position = 0) =>
        _telemetry.ObserveAsync(new PlaybackTelemetryObservation(
            SessionOf(video), _account, _profile, null, null, Guid.NewGuid(), video.AssetId, video.LibraryId,
            AccountFeatureId.Watch, "Videos", state, at ?? _now, position, null, null, 1d, false, false, null,
            new(PlaybackTelemetryDeliveryModes.DirectPlay), new("web")));

    private readonly Dictionary<Guid, Guid> _sessions = [];

    private Guid SessionOf(Video video)
    {
        if (!_sessions.TryGetValue(video.AssetId, out var session))
        {
            _sessions[video.AssetId] = session = Guid.NewGuid();
        }

        return session;
    }

    private PlaybackTelemetryReadScope AllLibrariesScope() =>
        new(true, true, null, null, true, new HashSet<Guid>(), new HashSet<AccountFeatureId> { AccountFeatureId.Watch });

    private async Task<IReadOnlyList<PlaybackTelemetrySession>> ActiveForAdministrator() =>
        (await _telemetry.GetActiveAsync(AllLibrariesScope(), 50, null)).Items;

    private PlayerDeviceStateRecord DeviceState(Guid asset, string device) => new()
    {
        ProfileId = _profile,
        DeviceId = device,
        Client = "web",
        AssetId = asset,
        PlaybackState = "playing",
        PositionSeconds = 12,
        UpdatedAt = _now,
    };

    private SystemActivityEntry Entry(string detail, Guid? entity) => new()
    {
        ActionType = "Test",
        EntityId = entity,
        ProfileId = _profile,
        Detail = detail,
    };
}
