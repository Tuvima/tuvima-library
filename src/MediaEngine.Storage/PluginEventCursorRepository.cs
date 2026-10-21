using System.Globalization;
using Dapper;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Storage;

/// <summary>SQLite implementation of <see cref="IPluginEventCursorRepository"/>, backed by <c>plugin_event_cursors</c>.</summary>
public sealed class PluginEventCursorRepository : IPluginEventCursorRepository
{
    private readonly IDatabaseConnection _db;

    public PluginEventCursorRepository(IDatabaseConnection db)
    {
        ArgumentNullException.ThrowIfNull(db);
        _db = db;
    }

    public Task<long?> GetAsync(string pluginId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = _db.CreateConnection();
        return Task.FromResult(conn.QuerySingleOrDefault<long?>(
            "SELECT last_sequence FROM plugin_event_cursors WHERE plugin_id = @pluginId;", new { pluginId }));
    }

    public Task SetAsync(string pluginId, long lastSequence, DateTimeOffset updatedAt, CancellationToken ct = default) =>
        _db.ExecuteWriteAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            connection.Execute("""
                INSERT INTO plugin_event_cursors (plugin_id, last_sequence, updated_at)
                VALUES (@pluginId, @lastSequence, @updatedAt)
                ON CONFLICT (plugin_id) DO UPDATE SET last_sequence = excluded.last_sequence, updated_at = excluded.updated_at;
                """,
                new { pluginId, lastSequence, updatedAt = updatedAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture) }, transaction);
        }, ct);

    public Task DeleteAsync(string pluginId, CancellationToken ct = default) =>
        _db.ExecuteWriteAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            connection.Execute("DELETE FROM plugin_event_cursors WHERE plugin_id = @pluginId;", new { pluginId }, transaction);
        }, ct);
}
