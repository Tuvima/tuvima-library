using Dapper;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services.Playback;
using MediaEngine.Contracts.Playback;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Entities;
using MediaEngine.Storage;
using MediaEngine.Storage.Playback;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Api.Tests;

/// <summary>
/// The heartbeat route's half of the offline rule (the progress route's half is in <see cref="OfflineProgressSyncTests"/>):
/// run through the real player service, session, progress and telemetry stores.
/// </summary>
public sealed partial class AuthorizedDisplayProjectionReadServiceTests
{
    private async Task<(Guid Account, Guid AssetId)> SeedHeartbeatAssetAsync()
    {
        var account = Guid.NewGuid();
        var library = Guid.NewGuid();
        await CreateHumanAsync(account, new HashSet<AccountFeatureId> { AccountFeatureId.Watch }, new HashSet<Guid>());
        var film = await InsertOwnedWorkWithIdAsync(library, "Film", "Movie");
        return (account, film.AssetId);
    }

    // Called straight from the test body: the request context lives in an ambient accessor, which an awaited helper
    // would not hand back to its caller.
    private (PlayerService Player, Guid ProfileId, UserStateRepository States) BuildHeartbeatPlayer(Guid account)
    {
        var profile = MediaEngine.Domain.Aggregates.Profile.SeedProfileId;
        var context = HumanContext(account, profile);
        var services = new ServiceCollection()
            .AddSingleton<IRequestAuthorityResolver>(new RequestAuthorityResolver(_accounts, _applications))
            .AddSingleton(CreateService(context, new StubRawProjection([], [])))
            .BuildServiceProvider();
        context.RequestServices = services;
        var accessor = new FixedContextAccessor(context);
        var scope = new PlayerCatalogueScope(accessor);
        var telemetry = new PlaybackTelemetryLifecycleService(
            accessor,
            scope,
            new PlaybackTelemetryRepository(_database, new ApplicationEventOutboxWriter()),
            new ClientAuthorizationRepository(_database),
            new IdentityRepository(_database),
            PersonalActivityTestKit.CreateRecorder(_database),
            TimeProvider.System);
        var states = new UserStateRepository(_database);
        var player = new PlayerService(
            new PlayerSessionRepository(_database), null!, _database, new MediaAssetRepository(_database), states,
            null!, null!, null!, null!, scope, telemetry, new PlayerDeviceStateRepository(_database));
        return (player, profile, states);
    }

    private sealed class FixedContextAccessor(HttpContext context) : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; } = context;
    }

    private static Task SeedProgressAsync(UserStateRepository states, Guid profile, Guid asset, double progress, DateTimeOffset updatedAt) =>
        states.SaveAsync(new UserState
        {
            Revision = 0,
            UserId = profile,
            AssetId = asset,
            ContentHash = "hash",
            ProgressPct = progress,
            LastAccessed = updatedAt,
            ExtendedProperties = new Dictionary<string, string> { ["position_seconds"] = (progress * 10).ToString(System.Globalization.CultureInfo.InvariantCulture) },
        });

    private static PlayerHeartbeatDto OfflineBeat(Guid profile, Guid asset, bool offline, DateTimeOffset? at) => new()
    {
        ProfileId = profile,
        AssetId = asset,
        IsPlaying = false,
        PositionSeconds = 10,
        DurationSeconds = 100,
        Offline = offline,
        ClientTimestamp = at,
    };

    [Fact]
    public async Task OfflineHeartbeat_ALaterClientTimestamp_ReplacesTheServerRecord()
    {
        var (account, asset) = await SeedHeartbeatAssetAsync();
        var (player, profile, states) = BuildHeartbeatPlayer(account);
        var recordedAt = DateTimeOffset.UtcNow.AddMinutes(-10);
        await SeedProgressAsync(states, profile, asset, 70, DateTimeOffset.UtcNow.AddHours(-2));

        await player.HeartbeatAsync(OfflineBeat(profile, asset, offline: true, recordedAt));

        var stored = (await states.GetAsync(profile, asset))!;
        Assert.Equal(10, stored.ProgressPct);
        Assert.Equal(recordedAt, stored.LastAccessed);
        Assert.Equal(2, stored.Revision);
    }

    [Fact]
    public async Task OfflineHeartbeat_ALaterServerRecord_IsIgnored()
    {
        var (account, asset) = await SeedHeartbeatAssetAsync();
        var (player, profile, states) = BuildHeartbeatPlayer(account);
        var serverAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        await SeedProgressAsync(states, profile, asset, 70, serverAt);

        await player.HeartbeatAsync(OfflineBeat(profile, asset, offline: true, DateTimeOffset.UtcNow.AddHours(-3)));

        var stored = (await states.GetAsync(profile, asset))!;
        Assert.Equal(70, stored.ProgressPct);
        Assert.Equal(serverAt, stored.LastAccessed);
        Assert.Equal(1, stored.Revision);
    }

    [Fact]
    public async Task Heartbeat_NotMarkedOffline_IgnoresTheClientTimestamp_AndAppliesAsBefore()
    {
        var (account, asset) = await SeedHeartbeatAssetAsync();
        var (player, profile, states) = BuildHeartbeatPlayer(account);
        await SeedProgressAsync(states, profile, asset, 70, DateTimeOffset.UtcNow.AddMinutes(-5));
        var before = DateTimeOffset.UtcNow;

        // An old client timestamp on an ordinary heartbeat does not hold it back, and is not recorded as the update time.
        await player.HeartbeatAsync(OfflineBeat(profile, asset, offline: false, DateTimeOffset.UtcNow.AddDays(-9)));

        var stored = (await states.GetAsync(profile, asset))!;
        Assert.Equal(10, stored.ProgressPct);
        Assert.InRange(stored.LastAccessed, before, DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task OfflineHeartbeat_AFutureClientTimestamp_IsClampedToTheServerClock()
    {
        var (account, asset) = await SeedHeartbeatAssetAsync();
        var (player, profile, states) = BuildHeartbeatPlayer(account);
        await SeedProgressAsync(states, profile, asset, 70, DateTimeOffset.UtcNow.AddHours(-2));
        var before = DateTimeOffset.UtcNow;

        await player.HeartbeatAsync(OfflineBeat(profile, asset, offline: true, DateTimeOffset.UtcNow.AddDays(30)));

        var stored = (await states.GetAsync(profile, asset))!;
        Assert.Equal(10, stored.ProgressPct);
        Assert.InRange(stored.LastAccessed, before, DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task OfflineHeartbeat_ForAnAssetWithNoRecord_CreatesItAtTheDevicesTime()
    {
        var (account, asset) = await SeedHeartbeatAssetAsync();
        var (player, profile, states) = BuildHeartbeatPlayer(account);
        var recordedAt = DateTimeOffset.UtcNow.AddHours(-1);

        await player.HeartbeatAsync(OfflineBeat(profile, asset, offline: true, recordedAt));

        var stored = (await states.GetAsync(profile, asset))!;
        Assert.Equal(10, stored.ProgressPct);
        Assert.Equal(recordedAt, stored.LastAccessed);
    }

    [Fact]
    public async Task OfflineHeartbeat_ThatWins_LeavesTheLiveSessionAndTelemetryAlone()
    {
        var (account, asset) = await SeedHeartbeatAssetAsync();
        var (player, profile, states) = BuildHeartbeatPlayer(account);
        var sessions = new PlayerSessionRepository(_database);
        var live = Guid.NewGuid();
        await sessions.EnsureSessionAsync(profile, live, "tv", "living-room");
        await sessions.UpdateTransportAsync(profile, playbackState: PlayerPlaybackStates.Playing, positionSeconds: 500, durationSeconds: 1000, progressPct: 50);
        await SeedProgressAsync(states, profile, asset, 70, DateTimeOffset.UtcNow.AddHours(-2));
        var recordedAt = DateTimeOffset.UtcNow.AddMinutes(-10);

        await player.HeartbeatAsync(OfflineBeat(profile, asset, offline: true, recordedAt));

        // The saved resume position took the replay...
        var stored = (await states.GetAsync(profile, asset))!;
        Assert.Equal(10, stored.ProgressPct);
        Assert.Equal(recordedAt, stored.LastAccessed);
        // ...but the live session still shows what the TV is doing right now, and nothing was recorded as happening now.
        var session = (await sessions.GetStateAsync(profile, TimeSpan.FromMinutes(5)))!;
        Assert.Equal(live, session.SessionId);
        Assert.Equal(PlayerPlaybackStates.Playing, session.PlaybackState);
        Assert.Equal(500, session.PositionSeconds);
        using var connection = _database.CreateConnection();
        Assert.Equal(0, connection.ExecuteScalar<int>("SELECT COUNT(*) FROM playback_telemetry_sessions;"));
        // The replay is history at the client's time (one entry), but it is not a live session.
        Assert.Equal(1, connection.ExecuteScalar<int>("SELECT COUNT(*) FROM consumption_history WHERE asset_id = @asset;", new { asset }));
        Assert.DoesNotContain("player_session_id", stored.ExtendedProperties.Keys);
    }

    private List<(string Key, string UpdatedAt)> HistoryOf(Guid asset)
    {
        using var connection = _database.CreateConnection();
        return connection.Query<(string, string)>(
            "SELECT session_key, updated_at FROM consumption_history WHERE asset_id = @asset;", new { asset }).AsList();
    }

    [Fact]
    public async Task OfflineHeartbeat_Applied_WritesOneHistoryEntryAtTheClientTime_AndReplaysNeverDoubleIt()
    {
        var (account, asset) = await SeedHeartbeatAssetAsync();
        var (player, profile, states) = BuildHeartbeatPlayer(account);
        await SeedProgressAsync(states, profile, asset, 70, DateTimeOffset.UtcNow.AddHours(-2));
        var recordedAt = DateTimeOffset.UtcNow.AddMinutes(-10);
        var beat = OfflineBeat(profile, asset, offline: true, recordedAt) with { DeviceId = "Phone-1" };

        await player.HeartbeatAsync(beat);
        await player.HeartbeatAsync(beat);
        await player.HeartbeatAsync(beat);

        var entry = Assert.Single(HistoryOf(asset));
        Assert.StartsWith("offline:phone-1:", entry.Key);
        var stamped = DateTimeOffset.Parse(entry.UpdatedAt);
        Assert.Equal(recordedAt, stamped);
        Assert.Equal(TimeSpan.Zero, stamped.Offset);
        // The two later copies were ignored as not newer, so the resume record was written once by the replay.
        Assert.Equal(2, (await states.GetAsync(profile, asset))!.Revision);
    }

    [Fact]
    public async Task OfflineHeartbeat_Ignored_WritesNoHistory()
    {
        var (account, asset) = await SeedHeartbeatAssetAsync();
        var (player, profile, states) = BuildHeartbeatPlayer(account);
        await SeedProgressAsync(states, profile, asset, 70, DateTimeOffset.UtcNow.AddMinutes(-5));

        await player.HeartbeatAsync(OfflineBeat(profile, asset, offline: true, DateTimeOffset.UtcNow.AddHours(-3)));

        Assert.Empty(HistoryOf(asset));
    }

    [Fact]
    public async Task OfflineHeartbeat_FutureTimestamp_IsHistoryAtTheServerClock_NotInTheFuture()
    {
        var (account, asset) = await SeedHeartbeatAssetAsync();
        var (player, profile, _) = BuildHeartbeatPlayer(account);
        var before = DateTimeOffset.UtcNow;

        await player.HeartbeatAsync(OfflineBeat(profile, asset, offline: true, DateTimeOffset.UtcNow.AddDays(30)));

        var entry = Assert.Single(HistoryOf(asset));
        Assert.InRange(DateTimeOffset.Parse(entry.UpdatedAt), before, DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task OfflineHeartbeat_ForAnAssetOverTheProfilesContentLimit_WritesNothing()
    {
        var (account, asset) = await SeedHeartbeatAssetAsync();
        var (player, profile, states) = BuildHeartbeatPlayer(account);
        SetAssetRating(asset, "R");
        var beat = OfflineBeat(profile, asset, offline: true, DateTimeOffset.UtcNow.AddMinutes(-10));

        SetContentLimit("PG");
        await Assert.ThrowsAsync<PlayerResourceDeniedException>(() => player.HeartbeatAsync(beat));
        Assert.Empty(HistoryOf(asset));
        Assert.Null(await states.GetAsync(profile, asset));

        // The same replay is applied once the limit allows the asset (a new request, so a new catalogue scope).
        SetContentLimit("R");
        var (allowedPlayer, _, _) = BuildHeartbeatPlayer(account);
        await allowedPlayer.HeartbeatAsync(beat);
        Assert.Single(HistoryOf(asset));
    }

    [Fact]
    public async Task OfflineHeartbeat_ForAnotherLibrarysAsset_WritesNothing()
    {
        var (account, _) = await SeedHeartbeatAssetAsync();
        var (player, profile, states) = BuildHeartbeatPlayer(account);
        var foreignLibrary = Guid.NewGuid();
        await HideLibraryFromAsync(account, foreignLibrary);
        var foreign = await InsertOwnedWorkWithIdAsync(foreignLibrary, "Elsewhere", "Movie");

        await Assert.ThrowsAsync<PlayerResourceDeniedException>(() => player.HeartbeatAsync(
            OfflineBeat(profile, foreign.AssetId, offline: true, DateTimeOffset.UtcNow.AddMinutes(-10))));

        Assert.Empty(HistoryOf(foreign.AssetId));
        Assert.Null(await states.GetAsync(profile, foreign.AssetId));
    }

    [Fact]
    public async Task OfflinePutProgress_ForAnAssetOverTheContentLimit_IsDeniedByTheRouteBeforeAnyHistoryCanBeWritten()
    {
        var (account, asset) = await SeedHeartbeatAssetAsync();
        var context = HumanContext(account, MediaEngine.Domain.Aggregates.Profile.SeedProfileId);
        SetAssetRating(asset, "R");

        SetContentLimit("PG");
        Assert.Equal(CatalogueResourceAccess.Denied,
            await CreateResourceService(context).EvaluateAssetAsync(context, asset, ApplicationPermissionIds.ProgressWrite));
        Assert.Empty(HistoryOf(asset));

        SetContentLimit("R");
        Assert.Equal(CatalogueResourceAccess.Allowed,
            await CreateResourceService(context).EvaluateAssetAsync(context, asset, ApplicationPermissionIds.ProgressWrite));
    }

    [Fact]
    public async Task Heartbeat_Online_IsKeyedByItsPlayerSession_AsBefore()
    {
        var (account, asset) = await SeedHeartbeatAssetAsync();
        var (player, profile, states) = BuildHeartbeatPlayer(account);
        var before = DateTimeOffset.UtcNow;

        await player.HeartbeatAsync(OfflineBeat(profile, asset, offline: false, null));

        var stored = (await states.GetAsync(profile, asset))!;
        Assert.Equal(10, stored.ProgressPct);
        Assert.InRange(stored.LastAccessed, before, DateTimeOffset.UtcNow);
        var entry = Assert.Single(HistoryOf(asset));
        Assert.True(Guid.TryParse(entry.Key, out _));
        Assert.Equal(stored.ExtendedProperties["player_session_id"], entry.Key);
    }

    private void SetAssetRating(Guid asset, string rating)
    {
        using var connection = _database.CreateConnection();
        var work = connection.ExecuteScalar<Guid>(
            "SELECT e.work_id FROM media_assets ma JOIN editions e ON e.id = ma.edition_id WHERE ma.id = @asset;", new { asset });
        connection.Execute(
            "INSERT INTO canonical_values (entity_id, key, value, last_scored_at) VALUES (@work, 'content_rating', @rating, CURRENT_TIMESTAMP);",
            new { work, rating });
    }

    [Fact]
    public async Task OfflineHeartbeat_WithNoSessionAtAll_CreatesNoSession()
    {
        var (account, asset) = await SeedHeartbeatAssetAsync();
        var (player, profile, _) = BuildHeartbeatPlayer(account);

        await player.HeartbeatAsync(OfflineBeat(profile, asset, offline: true, DateTimeOffset.UtcNow.AddMinutes(-3)));

        Assert.Null(await new PlayerSessionRepository(_database).GetStateAsync(profile, TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public async Task OfflineHeartbeat_WithAnOldSessionAndQueueItem_IsStillApplied_ButALiveOneIsNot()
    {
        var (account, asset) = await SeedHeartbeatAssetAsync();
        var (player, profile, states) = BuildHeartbeatPlayer(account);
        var sessions = new PlayerSessionRepository(_database);
        await sessions.EnsureSessionAsync(profile, Guid.NewGuid(), "tv", "living-room");
        await SeedProgressAsync(states, profile, asset, 70, DateTimeOffset.UtcNow.AddHours(-3));
        var staleIds = OfflineBeat(profile, asset, offline: true, DateTimeOffset.UtcNow.AddMinutes(-10))
            with
        { SessionId = Guid.NewGuid(), QueueItemId = Guid.NewGuid() };

        // A replay names the session and queue item of a past run; neither exists now, and that must not drop or refuse it.
        await player.HeartbeatAsync(staleIds);
        Assert.Equal(10, (await states.GetAsync(profile, asset))!.ProgressPct);

        // An ordinary heartbeat naming that queue item is still refused: the check is unchanged.
        await Assert.ThrowsAsync<PlayerResourceDeniedException>(() => player.HeartbeatAsync(staleIds with { Offline = false, ClientTimestamp = null, SessionId = null }));
    }
}
