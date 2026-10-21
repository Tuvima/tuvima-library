using System.Globalization;
using System.Text;
using Dapper;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Storage;

/// <summary>SQLite implementation of <see cref="IPluginRecordRepository"/>, backed by <c>plugin_records</c>.</summary>
public sealed class PluginRecordRepository : IPluginRecordRepository
{
    private const string Columns =
        "plugin_id AS PluginId, collection AS Collection, record_key AS Key, value_json AS Json, size_bytes AS SizeBytes, updated_at AS UpdatedAt";

    private readonly IDatabaseConnection _db;

    public PluginRecordRepository(IDatabaseConnection db)
    {
        ArgumentNullException.ThrowIfNull(db);
        _db = db;
    }

    public Task<StoredPluginRecord?> GetAsync(string pluginId, string collection, string key, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = _db.CreateConnection();
        var row = conn.QuerySingleOrDefault<Row>(
            $"SELECT {Columns} FROM plugin_records WHERE plugin_id = @pluginId AND collection = @collection AND record_key = @key;",
            new { pluginId, collection, key });
        return Task.FromResult(row is null ? null : Map(row));
    }

    public Task<PluginRecordWriteResult> PutAsync(
        string pluginId,
        string collection,
        string key,
        string json,
        DateTimeOffset updatedAt,
        PluginRecordLimits limits,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        ArgumentNullException.ThrowIfNull(json);
        ArgumentNullException.ThrowIfNull(limits);

        var size = Encoding.UTF8.GetByteCount(json);
        if (size > limits.MaxValueBytes)
        {
            return Task.FromResult(PluginRecordWriteResult.ValueTooLarge);
        }

        return _db.ExecuteWriteAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            var existing = connection.QuerySingleOrDefault<int?>(
                "SELECT size_bytes FROM plugin_records WHERE plugin_id = @pluginId AND collection = @collection AND record_key = @key;",
                new { pluginId, collection, key }, transaction);
            var usage = connection.QuerySingle<(long Records, long Bytes)>(
                "SELECT COUNT(*), COALESCE(SUM(size_bytes), 0) FROM plugin_records WHERE plugin_id = @pluginId;",
                new { pluginId }, transaction);

            if (existing is null && usage.Records + 1 > limits.MaxRecords)
            {
                return PluginRecordWriteResult.TooManyRecords;
            }

            if (usage.Bytes - (existing ?? 0) + size > limits.MaxTotalBytes)
            {
                return PluginRecordWriteResult.OutOfSpace;
            }

            connection.Execute("""
                INSERT INTO plugin_records (plugin_id, collection, record_key, value_json, size_bytes, updated_at)
                VALUES (@pluginId, @collection, @key, @json, @size, @updatedAt)
                ON CONFLICT (plugin_id, collection, record_key) DO UPDATE SET
                    value_json = excluded.value_json,
                    size_bytes = excluded.size_bytes,
                    updated_at = excluded.updated_at;
                """,
                new { pluginId, collection, key, json, size, updatedAt = Format(updatedAt) }, transaction);
            return PluginRecordWriteResult.Saved;
        }, ct);
    }

    public Task<bool> DeleteAsync(string pluginId, string collection, string key, CancellationToken ct = default) =>
        _db.ExecuteWriteAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            return connection.Execute(
                "DELETE FROM plugin_records WHERE plugin_id = @pluginId AND collection = @collection AND record_key = @key;",
                new { pluginId, collection, key }, transaction) > 0;
        }, ct);

    public Task<IReadOnlyList<StoredPluginRecord>> ListAsync(
        string pluginId,
        string collection,
        string? keyPrefix,
        int limit,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = _db.CreateConnection();
        // substr() compares the prefix literally, so '%' and '_' in a key need no escaping (LIKE would).
        var rows = conn.Query<Row>(
            $"""
            SELECT {Columns} FROM plugin_records
            WHERE plugin_id = @pluginId AND collection = @collection
              AND (@prefix IS NULL OR substr(record_key, 1, length(@prefix)) = @prefix)
            ORDER BY record_key
            LIMIT @limit;
            """,
            new { pluginId, collection, prefix = string.IsNullOrEmpty(keyPrefix) ? null : keyPrefix, limit = Math.Clamp(limit, 1, 1000) });
        return Task.FromResult<IReadOnlyList<StoredPluginRecord>>(rows.Select(Map).ToList());
    }

    public Task<PluginRecordUsage> GetUsageAsync(string pluginId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = _db.CreateConnection();
        var usage = conn.QuerySingle<(long Records, long Bytes)>(
            "SELECT COUNT(*), COALESCE(SUM(size_bytes), 0) FROM plugin_records WHERE plugin_id = @pluginId;",
            new { pluginId });
        return Task.FromResult(new PluginRecordUsage((int)usage.Records, usage.Bytes));
    }

    private static string Format(DateTimeOffset value) => value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    private static StoredPluginRecord Map(Row row) => new(
        row.PluginId,
        row.Collection,
        row.Key,
        row.Json,
        row.SizeBytes,
        DateTimeOffset.Parse(row.UpdatedAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal));

    private sealed class Row
    {
        public string PluginId { get; set; } = "";
        public string Collection { get; set; } = "";
        public string Key { get; set; } = "";
        public string Json { get; set; } = "";
        public int SizeBytes { get; set; }
        public string UpdatedAt { get; set; } = "";
    }
}
