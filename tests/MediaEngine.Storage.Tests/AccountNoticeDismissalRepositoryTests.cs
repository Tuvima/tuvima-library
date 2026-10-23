using Dapper;

namespace MediaEngine.Storage.Tests;

public sealed class AccountNoticeDismissalRepositoryTests : IDisposable
{
    private readonly string _dbPath;
    private readonly DatabaseConnection _db;
    private readonly AccountNoticeDismissalRepository _dismissals;

    public AccountNoticeDismissalRepositoryTests()
    {
        DapperConfiguration.Configure();
        _dbPath = Path.Combine(Path.GetTempPath(), $"tuvima_account_notices_{Guid.NewGuid():N}.db");
        _db = new DatabaseConnection(_dbPath);
        _db.InitializeSchema();
        _db.RunStartupChecks();
        _dismissals = new AccountNoticeDismissalRepository(_db);
    }

    public void Dispose()
    {
        _db.Dispose();
        File.Delete(_dbPath);
    }

    [Fact]
    public async Task ListDismissed_ForAnAccountThatNeverDismissedAnything_IsEmpty()
    {
        var account = SeedAccount();

        Assert.Empty(await _dismissals.ListDismissedAsync(account));
    }

    [Fact]
    public async Task Dismiss_RemembersTheNoticeForThatAccountOnly()
    {
        var mine = SeedAccount();
        var someoneElse = SeedAccount();

        await _dismissals.DismissAsync(mine, "secure-account", DateTimeOffset.UtcNow);

        Assert.Equal(["secure-account"], await _dismissals.ListDismissedAsync(mine));
        Assert.Empty(await _dismissals.ListDismissedAsync(someoneElse));
    }

    [Fact]
    public async Task Dismiss_TwiceKeepsOneRowAndTheFirstTime()
    {
        var account = SeedAccount();
        var first = new DateTimeOffset(2026, 10, 1, 8, 0, 0, TimeSpan.Zero);

        await _dismissals.DismissAsync(account, "secure-account", first);
        await _dismissals.DismissAsync(account, "secure-account", first.AddDays(3));

        Assert.Single(await _dismissals.ListDismissedAsync(account));
        using var conn = _db.CreateConnection();
        var dismissedAt = conn.ExecuteScalar<string>("SELECT dismissed_at FROM account_notice_dismissals;");
        Assert.StartsWith("2026-10-01T08:00:00", dismissedAt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeletingTheAccount_RemovesItsDismissals()
    {
        var account = SeedAccount();
        await _dismissals.DismissAsync(account, "secure-account", DateTimeOffset.UtcNow);

        using (var conn = _db.CreateConnection())
        {
            conn.Execute("DELETE FROM accounts WHERE id = @account;", new { account });
        }

        using var check = _db.CreateConnection();
        Assert.Equal(0, check.ExecuteScalar<int>("SELECT COUNT(*) FROM account_notice_dismissals;"));
    }

    private Guid SeedAccount()
    {
        var id = Guid.NewGuid();
        using var conn = _db.CreateConnection();
        conn.Execute(
            "INSERT INTO profiles (id, display_name, avatar_color, role, created_at) VALUES (@profile, 'Person', '#000000', 'StandardUser', @now); " +
            "INSERT INTO accounts (id, created_at, updated_at, profile_id) VALUES (@id, @now, @now, @profile);",
            new { id, profile = Guid.NewGuid(), now = DateTimeOffset.UtcNow.ToString("O") });
        return id;
    }
}
