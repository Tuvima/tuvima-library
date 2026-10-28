using System.Globalization;
using Dapper;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Storage;

/// <summary>SQLite implementation of <see cref="IPluginOutboxRepository"/>, backed by <c>plugin_outbox</c>.</summary>
public sealed class PluginOutboxRepository : IPluginOutboxRepository
{
    private readonly IDatabaseConnection _db;

    public PluginOutboxRepository(IDatabaseConnection db)
    {
        ArgumentNullException.ThrowIfNull(db);
        _db = db;
    }

    public Task<int> EnqueueAsync(PluginOutboxRow row, int maxQueued, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentException.ThrowIfNullOrWhiteSpace(row.PluginId);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxQueued, 1);
        return _db.ExecuteWriteAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            var at = Text(row.CreatedAt);
            connection.Execute(
                """
                INSERT INTO plugin_outbox (id, plugin_id, profile_id, payload_json, created_at, attempts, next_attempt_at, last_reason)
                VALUES (@id, @pluginId, @profile, @payload, @at, 0, @at, NULL);
                """,
                new { id = GuidSql.ToBlob(row.Id), pluginId = row.PluginId, profile = GuidSql.ToBlob(row.ProfileId), payload = row.PayloadJson, at },
                transaction);
            return connection.Execute(
                """
                DELETE FROM plugin_outbox WHERE id IN (
                    SELECT id FROM plugin_outbox WHERE plugin_id = @pluginId ORDER BY created_at DESC, rowid DESC LIMIT -1 OFFSET @maxQueued);
                """,
                new { pluginId = row.PluginId, maxQueued },
                transaction);
        }, ct);
    }

    public Task<IReadOnlyList<PluginOutboxRow>> ListDueAsync(string pluginId, DateTimeOffset now, int limit, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        ct.ThrowIfCancellationRequested();
        using var conn = _db.CreateConnection();
        IReadOnlyList<PluginOutboxRow> rows = conn.Query<(byte[] Id, string PluginId, byte[] Profile, string Payload, string CreatedAt, int Attempts)>(
                """
                SELECT id, plugin_id, profile_id, payload_json, created_at, attempts FROM plugin_outbox
                WHERE plugin_id = @pluginId AND next_attempt_at <= @now ORDER BY created_at, rowid LIMIT @limit;
                """,
                new { pluginId, now = Text(now), limit })
            .Select(row => new PluginOutboxRow(
                GuidSql.FromDb(row.Id), row.PluginId, GuidSql.FromDb(row.Profile), row.Payload,
                DateTimeOffset.Parse(row.CreatedAt, CultureInfo.InvariantCulture), row.Attempts))
            .ToList();
        return Task.FromResult(rows);
    }

    public Task<IReadOnlyList<string>> ListPluginsWithDueItemsAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = _db.CreateConnection();
        IReadOnlyList<string> ids = conn.Query<string>(
            "SELECT DISTINCT plugin_id FROM plugin_outbox WHERE next_attempt_at <= @now ORDER BY plugin_id;", new { now = Text(now) }).ToList();
        return Task.FromResult(ids);
    }

    public Task CompleteAsync(Guid id, CancellationToken ct = default) =>
        _db.ExecuteWriteAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            connection.Execute("DELETE FROM plugin_outbox WHERE id = @id;", new { id = GuidSql.ToBlob(id) }, transaction);
        }, ct);

    public Task RescheduleAsync(Guid id, int attempts, DateTimeOffset nextAttemptAt, string? reason, CancellationToken ct = default) =>
        _db.ExecuteWriteAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            connection.Execute(
                "UPDATE plugin_outbox SET attempts = @attempts, next_attempt_at = @next, last_reason = @reason WHERE id = @id;",
                new { id = GuidSql.ToBlob(id), attempts, next = Text(nextAttemptAt), reason },
                transaction);
        }, ct);

    public Task<int> CountAsync(string pluginId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        ct.ThrowIfCancellationRequested();
        using var conn = _db.CreateConnection();
        return Task.FromResult(conn.ExecuteScalar<int>("SELECT COUNT(*) FROM plugin_outbox WHERE plugin_id = @pluginId;", new { pluginId }));
    }

    public Task<int> DeleteForProfileAsync(string pluginId, Guid profileId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        return _db.ExecuteWriteAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            return connection.Execute(
                "DELETE FROM plugin_outbox WHERE plugin_id = @pluginId AND profile_id = @profile;",
                new { pluginId, profile = GuidSql.ToBlob(profileId) },
                transaction);
        }, ct);
    }

    private static string Text(DateTimeOffset value) => value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
}
