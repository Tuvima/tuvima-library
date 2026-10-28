using Dapper;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Storage;

/// <summary>SQLite implementation of <see cref="IPluginDataRepository"/>.</summary>
public sealed class PluginDataRepository : IPluginDataRepository
{
    private readonly IDatabaseConnection _db;

    public PluginDataRepository(IDatabaseConnection db)
    {
        ArgumentNullException.ThrowIfNull(db);
        _db = db;
    }

    public Task<PluginDataFootprint> GetFootprintAsync(string pluginId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        ct.ThrowIfCancellationRequested();
        using var conn = _db.CreateConnection();
        var records = conn.QuerySingle<(long Count, long Bytes)>(
            "SELECT COUNT(*), COALESCE(SUM(size_bytes), 0) FROM plugin_records WHERE plugin_id = @pluginId;", new { pluginId });
        var runs = conn.ExecuteScalar<long>("SELECT COUNT(*) FROM scheduled_task_runs WHERE plugin_id = @pluginId;", new { pluginId });
        var markers = conn.ExecuteScalar<long>(
            "SELECT COUNT(*) FROM playback_segments WHERE plugin_id = @pluginId AND review_status = 'detected';", new { pluginId });
        return Task.FromResult(new PluginDataFootprint((int)records.Count, records.Bytes, (int)runs, (int)markers));
    }

    public async Task<PluginDataRemoved> RemoveAsync(string pluginId, bool includeOutput, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        var removed = new PluginDataRemoved(0, 0, 0);
        await _db.ExecuteWriteAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            var records = connection.Execute("DELETE FROM plugin_records WHERE plugin_id = @pluginId;", new { pluginId }, transaction);
            connection.Execute("DELETE FROM plugin_event_cursors WHERE plugin_id = @pluginId;", new { pluginId }, transaction);
            connection.Execute("DELETE FROM plugin_library_limits WHERE plugin_id = @pluginId;", new { pluginId }, transaction);
            connection.Execute("DELETE FROM plugin_profile_connections WHERE plugin_id = @pluginId;", new { pluginId }, transaction);
            var runs = connection.Execute("DELETE FROM scheduled_task_runs WHERE plugin_id = @pluginId;", new { pluginId }, transaction);
            var markers = includeOutput
                ? connection.Execute("DELETE FROM playback_segments WHERE plugin_id = @pluginId AND review_status = 'detected';", new { pluginId }, transaction)
                : 0;
            removed = new PluginDataRemoved(records, runs, markers);
        }, ct).ConfigureAwait(false);
        return removed;
    }

    public Task<IReadOnlyList<string>> ListPluginIdsAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = _db.CreateConnection();
        IReadOnlyList<string> ids = conn.Query<string>("""
            SELECT plugin_id FROM plugin_records
            UNION SELECT plugin_id FROM plugin_event_cursors
            UNION SELECT plugin_id FROM plugin_library_limits
            UNION SELECT plugin_id FROM plugin_profile_connections
            UNION SELECT plugin_id FROM scheduled_task_runs WHERE plugin_id IS NOT NULL
            ORDER BY 1;
            """).ToList();
        return Task.FromResult(ids);
    }
}
