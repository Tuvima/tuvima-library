using Dapper;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Storage;

/// <summary>Lease-based durable queue for physical media-file synchronization.</summary>
public sealed class MediaFileWriteIntentRepository(IDatabaseConnection database)
{
    public Task<MediaFileWriteIntent?> ClaimNextAsync(TimeSpan lease, CancellationToken ct = default) =>
        database.ExecuteWriteAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            var now = DateTimeOffset.UtcNow;
            var row = connection.QuerySingleOrDefault<MediaFileWriteIntent>("""
                SELECT asset_id AS AssetId, generation AS Generation,
                       operation_token AS OperationToken, trigger AS Trigger,
                       status AS Status, attempts AS Attempts
                FROM media_file_write_intents
                WHERE status='pending'
                   OR (status='writing' AND lease_expires_at < @now)
                ORDER BY updated_at, asset_id LIMIT 1;
                """, new { now = now.ToString("O") }, transaction);
            if (row is null)
            {
                return null;
            }
            var changed = connection.Execute("""
                UPDATE media_file_write_intents
                SET status='writing', attempts=attempts+1, lease_expires_at=@leaseUntil,
                    updated_at=@now
                WHERE asset_id=@AssetId AND generation=@Generation
                  AND (status='pending' OR (status='writing' AND lease_expires_at < @now));
                """, new { row.AssetId, row.Generation, now = now.ToString("O"),
                    leaseUntil = now.Add(lease).ToString("O") }, transaction);
            return changed == 1 ? row with { Status = "writing", Attempts = row.Attempts + 1 } : null;
        }, ct);

    public Task<bool> CompleteAsync(Guid assetId, long generation, string status,
        string? error = null, CancellationToken ct = default)
    {
        if (status is not ("verified" or "blocked" or "unsupported" or "failed" or "pending"))
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }
        return database.ExecuteWriteAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            return connection.Execute("""
                UPDATE media_file_write_intents
                SET status=@status, lease_expires_at=NULL, last_error=@error, updated_at=@now
                WHERE asset_id=@assetId AND generation=@generation AND status='writing';
                """, new { assetId, generation, status, error,
                    now = DateTimeOffset.UtcNow.ToString("O") }, transaction) == 1;
        }, ct);
    }
}

public sealed record MediaFileWriteIntent
{
    public Guid AssetId { get; init; }
    public long Generation { get; init; }
    public string OperationToken { get; init; } = "";
    public string Trigger { get; init; } = "";
    public string Status { get; init; } = "";
    public int Attempts { get; init; }
}
