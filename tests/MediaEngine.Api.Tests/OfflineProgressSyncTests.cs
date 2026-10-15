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

    private Task<IResult> PutAsync(ProgressUpdateRequest body) =>
        ProgressEndpoints.PutProgressAsync(_assetId, body, ProfileId, Now, _states, _assets, default);

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
            VALUES (@assetId, @editionId, 'hash', @path, 'Normal');
            """, new { assetId, editionId, path = Path.Combine(Path.GetTempPath(), "offline-source.mp4") });
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
}
