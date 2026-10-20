using Dapper;
using MediaEngine.Storage.Playback;
using MediaEngine.TestSupport;
using Microsoft.Data.Sqlite;

namespace MediaEngine.Storage.Tests;

public sealed class CompanionSessionRepositoryTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"tuvima-companion-{Guid.NewGuid():N}.db");
    private readonly DatabaseConnection _database;
    private readonly CompanionSessionRepository _repository;
    private readonly Guid _profile = Guid.NewGuid();
    private readonly Guid _work = Guid.NewGuid();

    public CompanionSessionRepositoryTests()
    {
        DapperConfiguration.Configure();
        _database = new DatabaseConnection(_path);
        _database.InitializeSchema();
        _repository = new CompanionSessionRepository(_database);
        using var connection = _database.CreateConnection();
        connection.Execute("""
            INSERT INTO profiles(id,display_name,avatar_color,role,created_at)
            VALUES(@profile,'Viewer','#000000','RestrictedProfile',@now);
            INSERT INTO works(id,media_type,work_kind,is_catalog_only,ownership)
            VALUES(@work,'Movies','standalone',0,'Owned');
            """, new { profile = _profile, work = _work, now = Now.ToString("O") });
    }

    [Fact]
    public async Task NoCompanionRow_ReadsAsNull()
    {
        Assert.Null(await _repository.GetAsync(_profile, "phone"));
    }

    [Fact]
    public async Task FollowThenManual_ReplacesTheModeForTheSameCompanion()
    {
        await _repository.UpsertFollowAsync(_profile, "phone", "tv", Now);
        await _repository.UpsertManualAsync(_profile, "phone", _work, chapterIndex: 12, positionSeconds: 30, Now.AddMinutes(1));

        var row = (await _repository.GetAsync(_profile, "phone"))!;
        Assert.Equal("manual", row.Mode);
        Assert.Null(row.FollowedDeviceId);
        Assert.Equal(_work, row.ManualWorkId);
        Assert.Equal(12, row.ManualChapterIndex);
        Assert.Equal(30, row.ManualPositionSeconds);
        Assert.Equal(Now.AddMinutes(1), row.UpdatedAt);

        await _repository.UpsertFollowAsync(_profile, "phone", "tv", Now.AddMinutes(2));
        var followed = (await _repository.GetAsync(_profile, "phone"))!;
        Assert.Equal("follow", followed.Mode);
        Assert.Equal("tv", followed.FollowedDeviceId);
        Assert.Null(followed.ManualWorkId);
    }

    [Fact]
    public async Task Delete_RemovesOnlyThatCompanionsChoice()
    {
        await _repository.UpsertFollowAsync(_profile, "phone", "tv", Now);
        await _repository.UpsertFollowAsync(_profile, "tablet", "tv", Now);

        await _repository.DeleteAsync(_profile, "phone");

        Assert.Null(await _repository.GetAsync(_profile, "phone"));
        Assert.NotNull(await _repository.GetAsync(_profile, "tablet"));
    }

    [Fact]
    public void CheckConstraint_RejectsAFollowWithoutADevice()
    {
        using var connection = _database.CreateConnection();

        Assert.Throws<SqliteException>(() => connection.Execute("""
            INSERT INTO companion_sessions(profile_id, companion_device_id, mode, updated_at)
            VALUES(@profile, 'phone', 'follow', @now);
            """, new { profile = _profile, now = Now.ToString("O") }));
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
