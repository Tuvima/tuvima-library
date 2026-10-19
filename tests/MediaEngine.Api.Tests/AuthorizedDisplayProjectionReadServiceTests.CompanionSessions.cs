using Dapper;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services.Playback;
using MediaEngine.Contracts.Playback;
using MediaEngine.Domain.Authorization;
using MediaEngine.Storage;
using MediaEngine.Storage.Playback;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Api.Tests;

/// <summary>
/// Companion sessions: each device's live state is written from its own heartbeats and read back through the companion
/// service, with the caller's own profile and catalogue scope deciding what is visible.
/// </summary>
public sealed partial class AuthorizedDisplayProjectionReadServiceTests
{
    private sealed class AdjustableClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed record CompanionKit(
        PlayerService Player,
        CompanionSessionService Companion,
        PlayerDeviceStateRepository DeviceStates,
        Guid Profile,
        AdjustableClock Clock);

    private async Task<(Guid Account, (Guid WorkId, Guid AssetId) First, (Guid WorkId, Guid AssetId) Second)> SeedCompanionLibraryAsync()
    {
        var account = Guid.NewGuid();
        var library = Guid.NewGuid();
        await CreateHumanAsync(account, new HashSet<AccountFeatureId> { AccountFeatureId.Watch }, new HashSet<Guid> { library });
        var first = await InsertOwnedWorkWithIdAsync(library, "Film one", "Movie");
        var second = await InsertOwnedWorkWithIdAsync(library, "Film two", "Movie");
        return (account, first, second);
    }

    // Called straight from the test body for the same reason as BuildHeartbeatPlayer: the request context is ambient.
    private CompanionKit BuildCompanion(Guid account)
    {
        var profile = MediaEngine.Domain.Aggregates.Profile.SeedProfileId;
        var context = HumanContext(account, profile);
        var services = new ServiceCollection()
            .AddSingleton<IRequestAuthorityResolver>(new RequestAuthorityResolver(_accounts, _applications, new ProfileRepository(_database)))
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
        var deviceStates = new PlayerDeviceStateRepository(_database);
        var player = new PlayerService(
            new PlayerSessionRepository(_database), null!, _database, new MediaAssetRepository(_database),
            new UserStateRepository(_database), null!, null!, null!, null!, scope, telemetry, deviceStates);
        var clock = new AdjustableClock(DateTimeOffset.UtcNow);
        var companion = new CompanionSessionService(
            deviceStates,
            new CompanionSessionRepository(_database),
            new ClientAuthorizationRepository(_database),
            player,
            scope,
            clock);
        return new CompanionKit(player, companion, deviceStates, profile, clock);
    }

    private static PlayerHeartbeatDto DeviceBeat(
        Guid profile,
        string device,
        Guid asset,
        double position,
        Guid? session = null,
        bool ended = false) => new()
        {
            ProfileId = profile,
            DeviceId = device,
            Client = "web",
            SessionId = session,
            AssetId = asset,
            IsPlaying = true,
            HasPlaybackEnded = ended,
            PositionSeconds = position,
            DurationSeconds = 1000,
        };

    [Fact]
    public async Task TwoDevicesOnOneProfile_DoNotOverwriteEachOthersLiveState()
    {
        var (account, first, second) = await SeedCompanionLibraryAsync();
        var kit = BuildCompanion(account);

        // Device A holds the shared player session; device B heartbeats with a different session id and its own asset.
        var held = await kit.Player.HeartbeatAsync(DeviceBeat(kit.Profile, "device-a", first.AssetId, 100));
        await kit.Player.HeartbeatAsync(DeviceBeat(kit.Profile, "device-b", second.AssetId, 250, session: Guid.NewGuid()));
        await kit.Player.HeartbeatAsync(DeviceBeat(kit.Profile, "device-a", first.AssetId, 130, session: held.SessionId));

        var list = await kit.Companion.GetDevicesAsync(kit.Profile);

        Assert.Equal(kit.Profile, list.ProfileId);
        Assert.Equal(2, list.Devices.Count);
        var a = Assert.Single(list.Devices, device => device.DeviceId == "device-a");
        var b = Assert.Single(list.Devices, device => device.DeviceId == "device-b");
        Assert.Equal(130, a.PositionSeconds);
        Assert.Equal(first.AssetId, a.AssetId);
        Assert.Equal(first.WorkId, a.WorkId);
        Assert.Equal(250, b.PositionSeconds);
        Assert.Equal(second.AssetId, b.AssetId);
        Assert.Equal(second.WorkId, b.WorkId);
    }

    [Fact]
    public async Task FollowedDevice_ReturnsItsLatestHeartbeatPosition()
    {
        var (account, first, _) = await SeedCompanionLibraryAsync();
        var kit = BuildCompanion(account);
        await kit.Player.HeartbeatAsync(DeviceBeat(kit.Profile, "tv", first.AssetId, 100));

        var followed = await kit.Companion.FollowAsync(
            new CompanionFollowRequestDto { ProfileId = kit.Profile, CompanionDeviceId = "phone", DeviceId = "tv" });

        Assert.Equal(CompanionModes.Follow, followed.Mode);
        Assert.True(followed.IsFollowedDeviceLive);
        Assert.Equal(100, followed.Followed!.PositionSeconds);

        await kit.Player.HeartbeatAsync(DeviceBeat(kit.Profile, "tv", first.AssetId, 145));
        kit.Clock.Advance(TimeSpan.FromSeconds(10));

        var latest = await kit.Companion.GetSessionAsync(kit.Profile, "phone");
        Assert.Equal("tv", latest.FollowedDeviceId);
        Assert.True(latest.IsFollowedDeviceLive);
        Assert.Equal(145, latest.Followed!.PositionSeconds);
        Assert.Equal(first.WorkId, latest.Followed.WorkId);
    }

    [Fact]
    public async Task StaleDevice_IsNotListedAsLive()
    {
        var (account, first, _) = await SeedCompanionLibraryAsync();
        var kit = BuildCompanion(account);
        await kit.Player.HeartbeatAsync(DeviceBeat(kit.Profile, "tv", first.AssetId, 100));
        await kit.Companion.FollowAsync(
            new CompanionFollowRequestDto { ProfileId = kit.Profile, CompanionDeviceId = "phone", DeviceId = "tv" });
        Assert.Single((await kit.Companion.GetDevicesAsync(kit.Profile)).Devices);

        kit.Clock.Advance(PlayerService.StaleSessionWindow + TimeSpan.FromSeconds(15));

        Assert.Empty((await kit.Companion.GetDevicesAsync(kit.Profile)).Devices);
        var session = await kit.Companion.GetSessionAsync(kit.Profile, "phone");
        Assert.Equal("tv", session.FollowedDeviceId);
        Assert.False(session.IsFollowedDeviceLive);
        Assert.Null(session.Followed);
    }

    [Fact]
    public async Task EndedPlayback_IsNotListedAsLive()
    {
        var (account, first, _) = await SeedCompanionLibraryAsync();
        var kit = BuildCompanion(account);
        await kit.Player.HeartbeatAsync(DeviceBeat(kit.Profile, "tv", first.AssetId, 999, ended: true));

        Assert.Empty((await kit.Companion.GetDevicesAsync(kit.Profile)).Devices);
        Assert.Equal("ended", (await kit.DeviceStates.GetAsync(kit.Profile, "tv"))!.PlaybackState);
    }

    [Fact]
    public async Task OfflineReplayedHeartbeat_DoesNotWriteLiveState()
    {
        var (account, first, _) = await SeedCompanionLibraryAsync();
        var kit = BuildCompanion(account);

        await kit.Player.HeartbeatAsync(
            OfflineBeat(kit.Profile, first.AssetId, offline: true, DateTimeOffset.UtcNow.AddMinutes(-10)) with { DeviceId = "phone" });

        Assert.Empty(await kit.DeviceStates.ListAsync(kit.Profile));
    }

    [Fact]
    public async Task FollowingADeviceOfAnotherProfile_IsNotFound()
    {
        var (account, first, _) = await SeedCompanionLibraryAsync();
        var kit = BuildCompanion(account);
        var other = Guid.NewGuid();
        using (var connection = _database.CreateConnection())
        {
            connection.Execute("""
                INSERT INTO profiles(id,display_name,avatar_color,role,created_at)
                VALUES(@other,'Someone else','#000000','RestrictedProfile',@now);
                """, new { other, now = DateTimeOffset.UtcNow.ToString("O") });
        }

        await kit.DeviceStates.UpsertAsync(new PlayerDeviceStateRecord
        {
            ProfileId = other,
            DeviceId = "their-tv",
            Client = "web",
            AssetId = first.AssetId,
            WorkId = first.WorkId,
            PlaybackState = "playing",
            PositionSeconds = 5,
            UpdatedAt = DateTimeOffset.UtcNow,
        });

        await Assert.ThrowsAsync<PlayerResourceDeniedException>(() => kit.Companion.FollowAsync(
            new CompanionFollowRequestDto { ProfileId = kit.Profile, CompanionDeviceId = "phone", DeviceId = "their-tv" }));
        await Assert.ThrowsAsync<PlayerResourceDeniedException>(() => kit.Companion.GetDevicesAsync(other));
        Assert.Empty((await kit.Companion.GetDevicesAsync(kit.Profile)).Devices);
        Assert.Equal(CompanionModes.None, (await kit.Companion.GetSessionAsync(kit.Profile, "phone")).Mode);
    }

    [Fact]
    public async Task ManualPositionOnAWorkOutsideTheCallersScope_IsNotFound()
    {
        var (account, _, _) = await SeedCompanionLibraryAsync();
        var kit = BuildCompanion(account);
        var foreign = await InsertOwnedWorkWithIdAsync(Guid.NewGuid(), "Elsewhere", "Movie");

        await Assert.ThrowsAsync<PlayerResourceDeniedException>(() => kit.Companion.SetManualAsync(
            new CompanionManualPositionRequestDto { ProfileId = kit.Profile, CompanionDeviceId = "phone", WorkId = foreign.WorkId }));

        Assert.Equal(CompanionModes.None, (await kit.Companion.GetSessionAsync(kit.Profile, "phone")).Mode);
    }

    [Fact]
    public async Task ManualPosition_IsKeptAndReturned_AndRejectsNegativeNumbers()
    {
        var (account, first, _) = await SeedCompanionLibraryAsync();
        var kit = BuildCompanion(account);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => kit.Companion.SetManualAsync(
            new CompanionManualPositionRequestDto { ProfileId = kit.Profile, CompanionDeviceId = "phone", WorkId = first.WorkId, ChapterIndex = -1 }));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => kit.Companion.SetManualAsync(
            new CompanionManualPositionRequestDto { ProfileId = kit.Profile, CompanionDeviceId = "phone", WorkId = first.WorkId, PositionSeconds = -5 }));

        var saved = await kit.Companion.SetManualAsync(new CompanionManualPositionRequestDto
        {
            ProfileId = kit.Profile,
            CompanionDeviceId = "phone",
            WorkId = first.WorkId,
            ChapterIndex = 12,
            PositionSeconds = 30,
        });

        Assert.Equal(CompanionModes.Manual, saved.Mode);
        Assert.Equal(first.WorkId, saved.Manual!.WorkId);
        Assert.Equal(12, saved.Manual.ChapterIndex);
        Assert.Equal(30, saved.Manual.PositionSeconds);

        await kit.Companion.ClearAsync(kit.Profile, "phone");
        Assert.Equal(CompanionModes.None, (await kit.Companion.GetSessionAsync(kit.Profile, "phone")).Mode);
    }

    [Fact]
    public async Task LiveDeviceWithAnAssetOutsideScope_IsHidden()
    {
        var (account, first, _) = await SeedCompanionLibraryAsync();
        var kit = BuildCompanion(account);
        var foreign = await InsertOwnedWorkWithIdAsync(Guid.NewGuid(), "Elsewhere", "Movie");
        await kit.DeviceStates.UpsertAsync(new PlayerDeviceStateRecord
        {
            ProfileId = kit.Profile,
            DeviceId = "hidden-tv",
            Client = "web",
            AssetId = foreign.AssetId,
            WorkId = foreign.WorkId,
            PlaybackState = "playing",
            PositionSeconds = 5,
            UpdatedAt = kit.Clock.GetUtcNow(),
        });
        await kit.Player.HeartbeatAsync(DeviceBeat(kit.Profile, "tv", first.AssetId, 100));
        await kit.Companion.FollowAsync(
            new CompanionFollowRequestDto { ProfileId = kit.Profile, CompanionDeviceId = "phone", DeviceId = "hidden-tv" });

        var devices = (await kit.Companion.GetDevicesAsync(kit.Profile)).Devices;

        Assert.Equal("tv", Assert.Single(devices).DeviceId);
        var session = await kit.Companion.GetSessionAsync(kit.Profile, "phone");
        Assert.False(session.IsFollowedDeviceLive);
        Assert.Null(session.Followed);
    }

    [Fact]
    public async Task NoCompanionSession_ReturnsModeNone()
    {
        var (account, _, _) = await SeedCompanionLibraryAsync();
        var kit = BuildCompanion(account);

        var session = await kit.Companion.GetSessionAsync(kit.Profile, "phone");

        Assert.Equal(CompanionModes.None, session.Mode);
        Assert.Equal("phone", session.CompanionDeviceId);
        Assert.Null(session.Followed);
        Assert.Null(session.Manual);
        Assert.False(session.IsFollowedDeviceLive);
        await Assert.ThrowsAsync<ArgumentException>(() => kit.Companion.GetSessionAsync(kit.Profile, " "));
    }
}
