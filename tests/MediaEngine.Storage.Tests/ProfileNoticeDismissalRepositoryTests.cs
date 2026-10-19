using Dapper;

namespace MediaEngine.Storage.Tests;

public sealed class ProfileNoticeDismissalRepositoryTests : IDisposable
{
    private readonly string _dbPath;
    private readonly DatabaseConnection _db;
    private readonly ProfileNoticeDismissalRepository _dismissals;

    public ProfileNoticeDismissalRepositoryTests()
    {
        DapperConfiguration.Configure();
        _dbPath = Path.Combine(Path.GetTempPath(), $"tuvima_profile_notices_{Guid.NewGuid():N}.db");
        _db = new DatabaseConnection(_dbPath);
        _db.InitializeSchema();
        _db.RunStartupChecks();
        _dismissals = new ProfileNoticeDismissalRepository(_db);
    }

    public void Dispose()
    {
        _db.Dispose();
        File.Delete(_dbPath);
    }

    [Fact]
    public async Task List_ForAProfileThatNeverDismissedAnything_IsEmpty()
    {
        var profile = SeedProfile();

        Assert.Empty(await _dismissals.ListAsync(profile));
    }

    [Fact]
    public async Task Dismiss_RemembersTheNoticeAndItsFingerprintForThatProfileOnly()
    {
        var mine = SeedProfile();
        var someoneElse = SeedProfile();

        await _dismissals.DismissAsync(mine, "needs-review", "3", DateTimeOffset.UtcNow);
        await _dismissals.DismissAsync(mine, "missing-tools", "ffmpeg,image_engine", DateTimeOffset.UtcNow);

        var dismissed = await _dismissals.ListAsync(mine);
        Assert.Equal(["missing-tools", "needs-review"], dismissed.Select(row => row.NoticeKey));
        Assert.Equal("ffmpeg,image_engine", dismissed.Single(row => row.NoticeKey == "missing-tools").Fingerprint);
        Assert.Equal("3", dismissed.Single(row => row.NoticeKey == "needs-review").Fingerprint);
        Assert.Empty(await _dismissals.ListAsync(someoneElse));
    }

    [Fact]
    public async Task Dismiss_Again_ReplacesTheFingerprintAndKeepsOneRow()
    {
        var profile = SeedProfile();
        var first = new DateTimeOffset(2026, 10, 1, 8, 0, 0, TimeSpan.Zero);

        await _dismissals.DismissAsync(profile, "needs-review", "3", first);
        await _dismissals.DismissAsync(profile, "needs-review", "7", first.AddDays(3));

        var only = Assert.Single(await _dismissals.ListAsync(profile));
        Assert.Equal("7", only.Fingerprint);
        using var conn = _db.CreateConnection();
        var dismissedAt = conn.ExecuteScalar<string>("SELECT dismissed_at FROM profile_notice_dismissals;");
        Assert.StartsWith("2026-10-04T08:00:00", dismissedAt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeletingTheProfile_RemovesItsDismissals()
    {
        var profile = SeedProfile();
        await _dismissals.DismissAsync(profile, "needs-review", "3", DateTimeOffset.UtcNow);

        using (var conn = _db.CreateConnection())
        {
            conn.Execute("DELETE FROM profiles WHERE id = @profile;", new { profile });
        }

        using var check = _db.CreateConnection();
        Assert.Equal(0, check.ExecuteScalar<int>("SELECT COUNT(*) FROM profile_notice_dismissals;"));
    }

    private Guid SeedProfile()
    {
        var id = Guid.NewGuid();
        using var conn = _db.CreateConnection();
        conn.Execute(
            "INSERT INTO profiles (id, display_name, created_at) VALUES (@id, 'Test', @now);",
            new { id, now = DateTimeOffset.UtcNow.ToString("O") });
        return id;
    }
}
