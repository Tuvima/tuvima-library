using System.Globalization;
using Dapper;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Storage;

/// <summary>SQLite implementation of <see cref="IBackupDestinationRepository"/>.</summary>
public sealed class BackupDestinationRepository(IDatabaseConnection db) : IBackupDestinationRepository
{
    private const string CopyColumns =
        "id AS Id, destination_id AS DestinationId, remote_name AS RemoteName, size_bytes AS SizeBytes, sha256 AS Sha256, state AS State, verified_at AS VerifiedAt, last_error AS LastError, created_at AS CreatedAt";

    public Task<string?> GetSettingAsync(string key, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = db.CreateConnection();
        return Task.FromResult(conn.QueryFirstOrDefault<string?>("SELECT value_json FROM backup_settings WHERE setting_key = @key;", new { key }));
    }

    public Task SetSettingAsync(string key, string json, DateTimeOffset now, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = db.CreateConnection();
        conn.Execute(
            """
            INSERT INTO backup_settings (setting_key, value_json, updated_at) VALUES (@key, @json, @now)
            ON CONFLICT (setting_key) DO UPDATE SET value_json = @json, updated_at = @now;
            """,
            new { key, json, now = Text(now) });
        return Task.CompletedTask;
    }

    public Task AddCopyAsync(StoredBackupCopy copy, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = db.CreateConnection();
        conn.Execute(
            """
            INSERT INTO backup_copies (id, destination_id, remote_name, size_bytes, sha256, state, verified_at, last_error, created_at)
            VALUES (@id, @destinationId, @remoteName, @sizeBytes, @sha256, @state, @verifiedAt, @lastError, @createdAt);
            """,
            new
            {
                id = copy.Id.ToString(),
                destinationId = copy.DestinationId,
                remoteName = copy.RemoteName,
                sizeBytes = copy.SizeBytes,
                sha256 = copy.Sha256,
                state = copy.State,
                verifiedAt = copy.VerifiedAt is { } verified ? Text(verified) : null,
                lastError = copy.LastError,
                createdAt = Text(copy.CreatedAt),
            });
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<StoredBackupCopy>> ListCopiesAsync(string? destinationId, int limit, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = db.CreateConnection();
        var rows = conn.Query<CopyRow>(
            $"SELECT {CopyColumns} FROM backup_copies WHERE (@destinationId IS NULL OR destination_id = @destinationId) ORDER BY created_at DESC LIMIT @limit;",
            new { destinationId, limit = Math.Clamp(limit, 1, 500) });
        return Task.FromResult<IReadOnlyList<StoredBackupCopy>>(rows.Select(Map).ToList());
    }

    public Task MarkDeletedAsync(string destinationId, string remoteName, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = db.CreateConnection();
        conn.Execute(
            "UPDATE backup_copies SET state = 'deleted' WHERE destination_id = @destinationId AND remote_name = @remoteName;",
            new { destinationId, remoteName });
        return Task.CompletedTask;
    }

    private static string Text(DateTimeOffset value) => value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset? Parse(string? value) =>
        value is null ? null : DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static StoredBackupCopy Map(CopyRow row) => new(
        Guid.Parse(row.Id), row.DestinationId, row.RemoteName, row.SizeBytes, row.Sha256, row.State,
        Parse(row.VerifiedAt), row.LastError, Parse(row.CreatedAt)!.Value);

    private sealed class CopyRow
    {
        public string Id { get; set; } = "";
        public string DestinationId { get; set; } = "";
        public string RemoteName { get; set; } = "";
        public long SizeBytes { get; set; }
        public string Sha256 { get; set; } = "";
        public string State { get; set; } = "";
        public string? VerifiedAt { get; set; }
        public string? LastError { get; set; }
        public string CreatedAt { get; set; } = "";
    }
}
