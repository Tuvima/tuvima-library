using System.Globalization;
using Dapper;
using MediaEngine.Domain.Contracts;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Storage;

/// <summary>SQLite implementation of <see cref="IMediaInspectionHoldRepository"/>, backed by <c>media_inspection_holds</c>.</summary>
public sealed class MediaInspectionHoldRepository : IMediaInspectionHoldRepository
{
    private readonly IDatabaseConnection _db;

    public MediaInspectionHoldRepository(IDatabaseConnection db)
    {
        ArgumentNullException.ThrowIfNull(db);
        _db = db;
    }

    public Task UpsertAsync(Guid assetId, string toolId, DateTimeOffset heldAt, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(toolId);

        using var conn = _db.CreateConnection();
        conn.Execute("""
            INSERT INTO media_inspection_holds (asset_id, tool_id, held_at)
            VALUES (@assetId, @toolId, @heldAt)
            ON CONFLICT(asset_id) DO UPDATE SET tool_id = excluded.tool_id;
            """,
            new { assetId, toolId, heldAt = heldAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture) });
        return Task.CompletedTask;
    }

    public Task DeleteAsync(Guid assetId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        using var conn = _db.CreateConnection();
        conn.Execute("DELETE FROM media_inspection_holds WHERE asset_id = @assetId;", new { assetId });
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<Guid>> ListAssetIdsByToolAsync(string toolId, int limit, int skip = 0, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        using var conn = _db.CreateConnection();
        var ids = conn.Query<Guid>("""
            SELECT asset_id
            FROM media_inspection_holds
            WHERE tool_id = @toolId
            ORDER BY held_at, asset_id
            LIMIT @limit OFFSET @skip;
            """,
            new { toolId, limit = Math.Max(limit, 0), skip = Math.Max(skip, 0) }).ToList();
        return Task.FromResult<IReadOnlyList<Guid>>(ids);
    }

    public Task<int> CountByToolAsync(string toolId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        using var conn = _db.CreateConnection();
        var count = conn.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM media_inspection_holds WHERE tool_id = @toolId;", new { toolId });
        return Task.FromResult(count);
    }

    public Task<bool> ExistsAsync(Guid assetId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        using var conn = _db.CreateConnection();
        var exists = conn.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM media_inspection_holds WHERE asset_id = @assetId;", new { assetId }) > 0;
        return Task.FromResult(exists);
    }
}
