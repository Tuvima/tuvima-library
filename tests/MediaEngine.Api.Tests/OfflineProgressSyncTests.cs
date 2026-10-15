using Dapper;
using MediaEngine.Api.Endpoints;
using MediaEngine.Api.Services.Playback;
using MediaEngine.Contracts.Progress;
using MediaEngine.Domain.Entities;
using MediaEngine.Storage;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;

namespace MediaEngine.Api.Tests;

/// <summary>
/// Progress a paired app recorded offline: the later of the server's last update and the device's timestamp wins per
/// asset, a stale revision from a normal request is still refused, and a device clock in the future cannot win forever.
/// The heartbeat route's half of the rule is in <see cref="AuthorizedDisplayProjectionReadServiceTests"/>.
/// </summary>
public sealed class OfflineProgressSyncTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2030, 6, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid ProfileId = MediaEngine.Domain.Aggregates.Profile.SeedProfileId;

    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"tuvima_offline_progress_{Guid.NewGuid():N}.db");
    private readonly DatabaseConnection _database;
    private readonly UserStateRepository _states;
    private readonly MediaAssetRepository _assets;
    private readonly Guid _assetId;

    public OfflineProgressSyncTests()
    {
        DapperConfiguration.Configure();
        _database = new DatabaseConnection(_databasePath);
        _database.InitializeSchema();
        _database.RunStartupChecks();
        _states = new UserStateRepository(_database);
        _assets = new MediaAssetRepository(_database);
        _assetId = SeedAsset();
    }

    [Fact]
    public void Policy_UsesTheRuleOnlyForAnOfflineReplayWithATimestamp_AndClampsTheFuture()
    {
        var past = Now.AddHours(-3);

        Assert.Null(OfflineProgressSyncPolicy.ResolveClientTimestamp(false, past, Now));
        Assert.Null(OfflineProgressSyncPolicy.ResolveClientTimestamp(true, null, Now));
        Assert.Equal(past, OfflineProgressSyncPolicy.ResolveClientTimestamp(true, past, Now));
        Assert.Equal(Now, OfflineProgressSyncPolicy.ResolveClientTimestamp(true, Now, Now));
        Assert.Equal(Now, OfflineProgressSyncPolicy.ResolveClientTimestamp(true, Now.AddMinutes(1), Now));
        Assert.Equal(Now, OfflineProgressSyncPolicy.ResolveClientTimestamp(true, Now.AddYears(5), Now));
        // Always UTC, so a client's offset never reaches storage.
        Assert.Equal(TimeSpan.Zero, OfflineProgressSyncPolicy.ResolveClientTimestamp(true, new DateTimeOffset(2030, 6, 1, 9, 0, 0, TimeSpan.FromHours(5)), Now)!.Value.Offset);
        Assert.Equal(TimeSpan.Zero, OfflineProgressSyncPolicy.ResolveClientTimestamp(true, Now.ToOffset(TimeSpan.FromHours(-7)).AddYears(1), Now)!.Value.Offset);

        Assert.True(OfflineProgressSyncPolicy.ClientWins(Now.AddMinutes(-1), Now));
        Assert.False(OfflineProgressSyncPolicy.ClientWins(Now, Now));
        Assert.False(OfflineProgressSyncPolicy.ClientWins(Now.AddMinutes(1), Now));
    }

    [Fact]
    public async Task PutProgress_ALaterClientTimestamp_ReplacesTheServerRecord_DespiteAStaleRevision()
    {
        await SeedServerStateAsync(progress: 40, updatedAt: Now.AddHours(-1));
        var recordedAt = Now.AddMinutes(-10);

        var (status, state) = Unpack(await PutAsync(Request(80, expectedRevision: 0, offline: true, recordedAt)));

        Assert.Equal(StatusCodes.Status200OK, status);
        Assert.Equal(80, state!.ProgressPct);
        Assert.Equal(2, state.Revision);
        Assert.Equal(recordedAt.UtcDateTime, state.LastAccessed);
        var stored = (await _states.GetAsync(ProfileId, _assetId))!;
        Assert.Equal(80, stored.ProgressPct);
        Assert.Equal(recordedAt, stored.LastAccessed);
    }

    [Fact]
    public async Task PutProgress_ALaterServerRecord_KeepsTheServerState_AndAnswersWithIt()
    {
        var serverAt = Now.AddMinutes(-10);
        await SeedServerStateAsync(progress: 40, updatedAt: serverAt);

        var (status, state) = Unpack(await PutAsync(Request(80, expectedRevision: 0, offline: true, Now.AddHours(-1))));

        Assert.Equal(StatusCodes.Status200OK, status);
        Assert.Equal(40, state!.ProgressPct);
        Assert.Equal(1, state.Revision);
        var stored = (await _states.GetAsync(ProfileId, _assetId))!;
        Assert.Equal(40, stored.ProgressPct);
        Assert.Equal(1, stored.Revision);
        Assert.Equal(serverAt, stored.LastAccessed);
    }

    [Fact]
    public async Task PutProgress_AnEqualTimestamp_KeepsTheServerRecord()
    {
        await SeedServerStateAsync(progress: 40, updatedAt: Now.AddMinutes(-5));

        var (_, state) = Unpack(await PutAsync(Request(80, expectedRevision: 0, offline: true, Now.AddMinutes(-5))));

        Assert.Equal(40, state!.ProgressPct);
        Assert.Equal(40, (await _states.GetAsync(ProfileId, _assetId))!.ProgressPct);
    }

    [Fact]
    public async Task PutProgress_AStaleRevisionIsStillRejected_WithoutTheOfflineFlag_OrWithoutATimestamp()
    {
        await SeedServerStateAsync(progress: 40, updatedAt: Now.AddHours(-1));

        // Not offline: the timestamp is ignored, so a later one cannot be used to get around the revision check.
        var plain = Unpack(await PutAsync(Request(80, expectedRevision: 0, offline: false, Now.AddMinutes(-10))));
        // Offline but nothing to compare: nothing can decide which record is later.
        var noTimestamp = Unpack(await PutAsync(Request(80, expectedRevision: 0, offline: true, null)));

        Assert.Equal(StatusCodes.Status409Conflict, plain.Status);
        Assert.Equal(StatusCodes.Status409Conflict, noTimestamp.Status);
        Assert.Equal(40, (await _states.GetAsync(ProfileId, _assetId))!.ProgressPct);
    }

    [Fact]
    public async Task PutProgress_ANormalWriteWithTheCurrentRevision_IsUnchanged_AndStampsTheServerClock()
    {
        await SeedServerStateAsync(progress: 40, updatedAt: Now.AddHours(-1));

        var (status, state) = Unpack(await PutAsync(Request(55, expectedRevision: 1, offline: false, Now.AddDays(-30))));

        Assert.Equal(StatusCodes.Status200OK, status);
        Assert.Equal(55, state!.ProgressPct);
        Assert.Equal(Now.UtcDateTime, state.LastAccessed);
    }

    [Fact]
    public async Task PutProgress_AFutureClientTimestamp_IsClampedToTheServerClock()
    {
        await SeedServerStateAsync(progress: 40, updatedAt: Now.AddHours(-1));

        var (status, state) = Unpack(await PutAsync(Request(90, expectedRevision: 0, offline: true, Now.AddDays(3))));

        Assert.Equal(StatusCodes.Status200OK, status);
        Assert.Equal(90, state!.ProgressPct);
        // Recorded as the server's now, not three days ahead, so a later write from another device still wins.
        Assert.Equal(Now, (await _states.GetAsync(ProfileId, _assetId))!.LastAccessed);
    }

    [Fact]
    public async Task PutProgress_AFirstOfflineWrite_CreatesTheRecordAtTheDevicesTime()
    {
        var recordedAt = Now.AddHours(-2);

        var (status, state) = Unpack(await PutAsync(Request(25, expectedRevision: 0, offline: true, recordedAt)));

        Assert.Equal(StatusCodes.Status200OK, status);
        Assert.Equal(1, state!.Revision);
        Assert.Equal(recordedAt, (await _states.GetAsync(ProfileId, _assetId))!.LastAccessed);
    }

    private Task<IResult> PutAsync(ProgressUpdateRequest body, bool pairedApp = true, Guid? asset = null) =>
        ProgressEndpoints.PutProgressAsync(asset ?? _assetId, body, ProfileId, Now, pairedApp, _states, _assets, default);

    private static ProgressUpdateRequest Request(double progress, long expectedRevision, bool offline, DateTimeOffset? clientTimestamp) =>
        new(null, progress, null) { ExpectedRevision = expectedRevision, Offline = offline, ClientTimestamp = clientTimestamp };

    private static (int Status, UserStateResponse? State) Unpack(IResult result) =>
        (((IStatusCodeHttpResult)result).StatusCode ?? StatusCodes.Status200OK, (result as IValueHttpResult)?.Value as UserStateResponse);

    private async Task SeedServerStateAsync(double progress, DateTimeOffset updatedAt) =>
        await _states.SaveAsync(new UserState
        {
            Revision = 0,
            UserId = ProfileId,
            AssetId = _assetId,
            ContentHash = "hash",
            ProgressPct = progress,
            LastAccessed = updatedAt,
            ExtendedProperties = [],
        });

    private Guid SeedAsset()
    {
        var workId = Guid.NewGuid();
        var editionId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        using var connection = _database.CreateConnection();
        connection.Execute("INSERT INTO works (id, media_type) VALUES (@workId, 'Movies');", new { workId });
        connection.Execute("INSERT INTO editions (id, work_id) VALUES (@editionId, @workId);", new { editionId, workId });
        connection.Execute("""
            INSERT INTO media_assets (id, edition_id, content_hash, file_path_root, status)
            VALUES (@assetId, @editionId, @hash, @path, 'Normal');
            """, new { assetId, editionId, hash = assetId.ToString("N"), path = Path.Combine(Path.GetTempPath(), "offline-source.mp4") });
        return assetId;
    }

    public void Dispose()
    {
        _database.Dispose();
        foreach (var path in new[] { _databasePath, $"{_databasePath}-wal", $"{_databasePath}-shm" })
        {
            try { File.Delete(path); } catch (IOException) { /* a pooled handle may still be closing; the temp file is disposable */ }
        }
    }

    [Fact]
    public async Task PutProgress_OfflineFromAnyoneButAPairedApp_IsAnOrdinaryRequest()
    {
        await SeedServerStateAsync(progress: 40, updatedAt: Now.AddHours(-1));

        // A stale revision is rejected as before, even with a later timestamp: the offline flag is not honoured.
        var stale = Unpack(await PutAsync(Request(80, expectedRevision: 0, offline: true, Now.AddMinutes(-10)), pairedApp: false));
        Assert.Equal(StatusCodes.Status409Conflict, stale.Status);
        Assert.Equal(40, (await _states.GetAsync(ProfileId, _assetId))!.ProgressPct);

        // A current revision is an ordinary write stamped with the server clock, not the supplied time.
        var current = Unpack(await PutAsync(Request(55, expectedRevision: 1, offline: true, Now.AddDays(-2)), pairedApp: false));
        Assert.Equal(StatusCodes.Status200OK, current.Status);
        Assert.Equal(Now, (await _states.GetAsync(ProfileId, _assetId))!.LastAccessed);
    }

    [Fact]
    public void Heartbeat_OfflineFieldsCountOnlyForAPairedApp()
    {
        var beat = new MediaEngine.Contracts.Playback.PlayerHeartbeatDto { Offline = true, ClientTimestamp = Now };
        var paired = new MediaEngine.Domain.Authorization.RequestAuthority(
            MediaEngine.Domain.Authorization.PrincipalKind.DelegatedUserClient, true, Guid.NewGuid(), ProfileId, DeviceId: Guid.NewGuid());
        var browser = new MediaEngine.Domain.Authorization.RequestAuthority(
            MediaEngine.Domain.Authorization.PrincipalKind.Human, true, Guid.NewGuid(), ProfileId, DeviceId: Guid.NewGuid());
        var service = new MediaEngine.Domain.Authorization.RequestAuthority(
            MediaEngine.Domain.Authorization.PrincipalKind.ServiceApplication, true);

        Assert.Equal(beat, OfflineProgressSyncPolicy.ForCaller(beat, paired));
        foreach (var caller in new[] { browser, service })
        {
            var treated = OfflineProgressSyncPolicy.ForCaller(beat, caller);
            Assert.False(treated.Offline);
            Assert.Null(treated.ClientTimestamp);
        }
    }

    [Fact]
    public async Task PutProgress_AClientOffset_IsStoredAsUtc_AndSortsByTheInstant()
    {
        var other = SeedAsset();
        // 11:00 at +05:00 is 06:00Z, earlier than the 08:00Z record on the other asset, though "11:00" sorts after "08:00" as text.
        var earlier = new DateTimeOffset(2030, 6, 1, 11, 0, 0, TimeSpan.FromHours(5));
        await PutAsync(Request(30, expectedRevision: 0, offline: true, earlier));
        await _states.SaveAsync(new UserState
        {
            Revision = 0,
            UserId = ProfileId,
            AssetId = other,
            ContentHash = "hash",
            ProgressPct = 10,
            LastAccessed = new DateTimeOffset(2030, 6, 1, 8, 0, 0, TimeSpan.Zero),
            ExtendedProperties = [],
        });

        using var connection = _database.CreateConnection();
        var stored = connection.Query<string>("SELECT last_accessed FROM user_states ORDER BY last_accessed;").AsList();

        Assert.All(stored, text => Assert.EndsWith("+00:00", text));
        Assert.StartsWith("2030-06-01T06:00:00", stored[0]);
        Assert.StartsWith("2030-06-01T08:00:00", stored[1]);
        Assert.Equal(earlier, (await _states.GetAsync(ProfileId, _assetId))!.LastAccessed);
    }
}
