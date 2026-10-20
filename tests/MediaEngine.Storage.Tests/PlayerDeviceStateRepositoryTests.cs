using Dapper;
using MediaEngine.Storage.Playback;
using MediaEngine.TestSupport;

namespace MediaEngine.Storage.Tests;

public sealed class PlayerDeviceStateRepositoryTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"tuvima-device-states-{Guid.NewGuid():N}.db");
    private readonly DatabaseConnection _database;
    private readonly PlayerDeviceStateRepository _repository;

    public PlayerDeviceStateRepositoryTests()
    {
        DapperConfiguration.Configure();
        _database = new DatabaseConnection(_path);
        _database.InitializeSchema();
        _repository = new PlayerDeviceStateRepository(_database);
    }

    [Fact]
    public async Task TwoDevicesOnOneProfile_UpsertIndependently()
    {
        var profile = SeedProfile();
        var asset = SeedAsset();

        await _repository.UpsertAsync(State(profile, "phone", asset, position: 10, at: Now));
        await _repository.UpsertAsync(State(profile, "tv", asset, position: 500, at: Now));
        await _repository.UpsertAsync(State(profile, "phone", asset, position: 40, at: Now.AddSeconds(10)));

        var rows = await _repository.ListAsync(profile);
        Assert.Equal(2, rows.Count);
        Assert.Equal(40, rows.Single(row => row.DeviceId == "phone").PositionSeconds);
        Assert.Equal(500, rows.Single(row => row.DeviceId == "tv").PositionSeconds);
        Assert.Equal(Now.AddSeconds(10), (await _repository.GetAsync(profile, "phone"))!.UpdatedAt);
    }

    [Fact]
    public async Task Upsert_RemovesTheProfilesRowsOlderThanADay_ButKeepsOtherProfiles()
    {
        var profile = SeedProfile();
        var other = SeedProfile();
        var asset = SeedAsset();
        await _repository.UpsertAsync(State(profile, "old-tv", asset, position: 1, at: Now.AddHours(-25)));
        await _repository.UpsertAsync(State(other, "old-tv", asset, position: 1, at: Now.AddHours(-25)));

        await _repository.UpsertAsync(State(profile, "phone", asset, position: 2, at: Now));

        Assert.Null(await _repository.GetAsync(profile, "old-tv"));
        Assert.NotNull(await _repository.GetAsync(profile, "phone"));
        Assert.NotNull(await _repository.GetAsync(other, "old-tv"));
    }

    [Fact]
    public async Task MarkEnded_SetsOnlyThatDevicesStateToEnded()
    {
        var profile = SeedProfile();
        var asset = SeedAsset();
        await _repository.UpsertAsync(State(profile, "phone", asset, position: 10, at: Now));
        await _repository.UpsertAsync(State(profile, "tv", asset, position: 20, at: Now));

        await _repository.MarkEndedAsync(profile, "tv", Now.AddSeconds(5));

        Assert.Equal("ended", (await _repository.GetAsync(profile, "tv"))!.PlaybackState);
        Assert.Equal("playing", (await _repository.GetAsync(profile, "phone"))!.PlaybackState);
    }

    [Fact]
    public async Task DeletingTheProfile_RemovesItsDeviceStates()
    {
        var profile = SeedProfile();
        var asset = SeedAsset();
        await _repository.UpsertAsync(State(profile, "phone", asset, position: 10, at: Now));

        using (var connection = _database.CreateConnection())
        {
            connection.Execute("DELETE FROM profiles WHERE id = @profile;", new { profile });
        }

        Assert.Empty(await _repository.ListAsync(profile));
    }

    private static PlayerDeviceStateRecord State(Guid profile, string device, Guid asset, double position, DateTimeOffset at) =>
        new()
        {
            ProfileId = profile,
            DeviceId = device,
            Client = "web",
            AssetId = asset,
            PlaybackState = "playing",
            PositionSeconds = position,
            UpdatedAt = at,
        };

    private Guid SeedProfile()
    {
        var profile = Guid.NewGuid();
        using var connection = _database.CreateConnection();
        connection.Execute("""
            INSERT INTO profiles(id,display_name,avatar_color,role,created_at)
            VALUES(@profile,'Viewer','#000000','RestrictedProfile',@now);
            """, new { profile, now = Now.ToString("O") });
        return profile;
    }

    private Guid SeedAsset()
    {
        var work = Guid.NewGuid();
        var edition = Guid.NewGuid();
        var asset = Guid.NewGuid();
        using var connection = _database.CreateConnection();
        connection.Execute("""
            INSERT INTO works(id,media_type,work_kind,is_catalog_only,ownership)
            VALUES(@work,'Movies','standalone',0,'Owned');
            INSERT INTO editions(id,work_id) VALUES(@edition,@work);
            INSERT INTO media_assets(id,edition_id,content_hash,file_path_root,library_id)
            VALUES(@asset,@edition,@hash,@path,@library);
            """, new { work, edition, asset, hash = asset.ToString("N"), path = $"C:/fixtures/{asset:N}.media", library = Guid.NewGuid().ToString("D") });
        return asset;
    }

    public void Dispose()
    {
        _database.Dispose();
        TestTemp.ReleaseSqlitePool(_path);
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            if (File.Exists(_path + suffix))
            {
                File.Delete(_path + suffix);
            }
        }
    }
}
