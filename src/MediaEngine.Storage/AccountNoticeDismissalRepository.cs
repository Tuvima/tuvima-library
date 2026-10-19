using System.Globalization;
using Dapper;
using MediaEngine.Domain.Contracts;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Storage;

/// <summary>SQLite implementation of <see cref="IAccountNoticeDismissalRepository"/>, backed by <c>account_notice_dismissals</c>.</summary>
public sealed class AccountNoticeDismissalRepository : IAccountNoticeDismissalRepository
{
    private readonly IDatabaseConnection _db;

    public AccountNoticeDismissalRepository(IDatabaseConnection db)
    {
        ArgumentNullException.ThrowIfNull(db);
        _db = db;
    }

    public Task<IReadOnlyList<string>> ListDismissedAsync(Guid accountId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        using var conn = _db.CreateConnection();
        var keys = conn.Query<string>(
            "SELECT notice_key FROM account_notice_dismissals WHERE account_id = @accountId ORDER BY notice_key;",
            new { accountId }).ToList();
        return Task.FromResult<IReadOnlyList<string>>(keys);
    }

    public Task DismissAsync(Guid accountId, string noticeKey, DateTimeOffset dismissedAt, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(noticeKey);

        using var conn = _db.CreateConnection();
        conn.Execute("""
            INSERT INTO account_notice_dismissals (account_id, notice_key, dismissed_at)
            VALUES (@accountId, @noticeKey, @dismissedAt)
            ON CONFLICT(account_id, notice_key) DO NOTHING;
            """,
            new { accountId, noticeKey, dismissedAt = dismissedAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture) });
        return Task.CompletedTask;
    }
}
