using System.Globalization;
using Dapper;
using MediaEngine.Domain.Contracts;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Storage;

/// <summary>SQLite implementation of <see cref="IProfileNoticeDismissalRepository"/>, backed by <c>profile_notice_dismissals</c>.</summary>
public sealed class ProfileNoticeDismissalRepository : IProfileNoticeDismissalRepository
{
    private readonly IDatabaseConnection _db;

    public ProfileNoticeDismissalRepository(IDatabaseConnection db)
    {
        ArgumentNullException.ThrowIfNull(db);
        _db = db;
    }

    public Task<IReadOnlyList<ProfileNoticeDismissal>> ListAsync(Guid profileId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        using var conn = _db.CreateConnection();
        var rows = conn.Query<(string NoticeKey, string Fingerprint)>(
            "SELECT notice_key AS NoticeKey, fingerprint AS Fingerprint FROM profile_notice_dismissals WHERE profile_id = @profileId ORDER BY notice_key;",
            new { profileId })
            .Select(row => new ProfileNoticeDismissal(row.NoticeKey, row.Fingerprint))
            .ToList();
        return Task.FromResult<IReadOnlyList<ProfileNoticeDismissal>>(rows);
    }

    public Task DismissAsync(Guid profileId, string noticeKey, string fingerprint, DateTimeOffset dismissedAt, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(noticeKey);
        ArgumentNullException.ThrowIfNull(fingerprint);

        using var conn = _db.CreateConnection();
        conn.Execute("""
            INSERT INTO profile_notice_dismissals (profile_id, notice_key, fingerprint, dismissed_at)
            VALUES (@profileId, @noticeKey, @fingerprint, @dismissedAt)
            ON CONFLICT(profile_id, notice_key) DO UPDATE SET
                fingerprint = excluded.fingerprint,
                dismissed_at = excluded.dismissed_at;
            """,
            new { profileId, noticeKey, fingerprint, dismissedAt = dismissedAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture) });
        return Task.CompletedTask;
    }
}
