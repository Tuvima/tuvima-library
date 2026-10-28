using System.Globalization;
using Dapper;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Storage;

/// <summary>SQLite implementation of <see cref="IPluginActivityRepository"/>, backed by <c>plugin_activity</c>.</summary>
public sealed class PluginActivityRepository : IPluginActivityRepository
{
    private readonly IDatabaseConnection _db;

    public PluginActivityRepository(IDatabaseConnection db)
    {
        ArgumentNullException.ThrowIfNull(db);
        _db = db;
    }

    public Task AddAsync(StoredPluginActivity entry, int keepPerPlugin, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentException.ThrowIfNullOrWhiteSpace(entry.PluginId);
        return _db.ExecuteWriteAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            connection.Execute("""
                INSERT INTO plugin_activity
                    (occurred_at, plugin_id, plugin_version, kind, reason, item_id, items_changed, summary, detail)
                VALUES (@occurredAt, @pluginId, @pluginVersion, @kind, @reason, @itemId, @itemsChanged, @summary, @detail);
                """,
                new
                {
                    occurredAt = Format(entry.OccurredAt),
                    pluginId = entry.PluginId,
                    pluginVersion = entry.PluginVersion,
                    kind = KindWord(entry.Kind),
                    reason = entry.Reason,
                    itemId = GuidSql.ToDb(entry.ItemId),
                    itemsChanged = entry.ItemsChanged,
                    summary = entry.Summary,
                    detail = entry.Detail,
                }, transaction);
            connection.Execute("""
                DELETE FROM plugin_activity
                WHERE plugin_id = @pluginId
                  AND id NOT IN (SELECT id FROM plugin_activity WHERE plugin_id = @pluginId ORDER BY id DESC LIMIT @keep);
                """,
                new { pluginId = entry.PluginId, keep = Math.Max(1, keepPerPlugin) }, transaction);
        }, ct);
    }

    public Task<IReadOnlyList<StoredPluginActivity>> GetRecentAsync(string pluginId, int limit, bool failuresOnly, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = _db.CreateConnection();
        var rows = conn.Query<Row>($"""
            SELECT id AS Id, occurred_at AS OccurredAt, plugin_id AS PluginId, plugin_version AS PluginVersion, kind AS Kind,
                   reason AS Reason, item_id AS ItemId, items_changed AS ItemsChanged, summary AS Summary, detail AS Detail
            FROM plugin_activity
            WHERE plugin_id = @pluginId {(failuresOnly ? "AND kind = 'failure'" : "")}
            ORDER BY id DESC
            LIMIT @limit;
            """,
            new { pluginId, limit = Math.Clamp(limit, 1, 500) });
        IReadOnlyList<StoredPluginActivity> list = rows.Select(Map).ToList();
        return Task.FromResult(list);
    }

    public Task<int> CountFailuresSinceAsync(string pluginId, DateTimeOffset since, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = _db.CreateConnection();
        return Task.FromResult(conn.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM plugin_activity WHERE plugin_id = @pluginId AND kind = 'failure' AND occurred_at >= @since;",
            new { pluginId, since = Format(since) }));
    }

    public Task<int> DeleteForPluginAsync(string pluginId, CancellationToken ct = default) =>
        _db.ExecuteWriteAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            return connection.Execute("DELETE FROM plugin_activity WHERE plugin_id = @pluginId;", new { pluginId }, transaction);
        }, ct);

    public Task<int> PruneAsync(DateTimeOffset olderThan, CancellationToken ct = default) =>
        _db.ExecuteWriteAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            return connection.Execute("DELETE FROM plugin_activity WHERE occurred_at < @olderThan;", new { olderThan = Format(olderThan) }, transaction);
        }, ct);

    private static string KindWord(PluginActivityKind kind) => kind switch
    {
        PluginActivityKind.Failure => "failure",
        PluginActivityKind.Skipped => "skipped",
        PluginActivityKind.Note => "note",
        _ => "run",
    };

    private static PluginActivityKind ParseKind(string word) => word switch
    {
        "failure" => PluginActivityKind.Failure,
        "skipped" => PluginActivityKind.Skipped,
        "note" => PluginActivityKind.Note,
        _ => PluginActivityKind.Run,
    };

    private static string Format(DateTimeOffset value) => value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    private static StoredPluginActivity Map(Row row) => new(
        row.Id,
        DateTimeOffset.Parse(row.OccurredAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal),
        row.PluginId,
        row.PluginVersion,
        ParseKind(row.Kind),
        row.Reason,
        GuidSql.FromDbNullable(row.ItemId),
        row.ItemsChanged,
        row.Summary,
        row.Detail);

    private sealed class Row
    {
        public long Id { get; set; }
        public string OccurredAt { get; set; } = "";
        public string PluginId { get; set; } = "";
        public string PluginVersion { get; set; } = "";
        public string Kind { get; set; } = "";
        public string? Reason { get; set; }
        public object? ItemId { get; set; }
        public int ItemsChanged { get; set; }
        public string Summary { get; set; } = "";
        public string? Detail { get; set; }
    }
}
