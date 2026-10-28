using System.Globalization;
using Dapper;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Storage;

/// <summary>SQLite implementation of <see cref="IPluginDisplayRepository"/>, backed by <c>plugin_display_contributions</c>.</summary>
public sealed class PluginDisplayRepository : IPluginDisplayRepository
{
    private readonly IDatabaseConnection _db;

    public PluginDisplayRepository(IDatabaseConnection db)
    {
        ArgumentNullException.ThrowIfNull(db);
        _db = db;
    }

    public Task<int> UpsertAsync(PluginDisplayRow row, int maxPerPlugin, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentException.ThrowIfNullOrWhiteSpace(row.PluginId);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxPerPlugin, 1);
        return _db.ExecuteWriteAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            connection.Execute(
                """
                INSERT INTO plugin_display_contributions (plugin_id, slot, subject_id, key, title, text, icon, strength, from_local_ai, updated_at)
                VALUES (@PluginId, @Slot, @SubjectId, @Key, @Title, @Text, @Icon, @Strength, @FromLocalAi, @at)
                ON CONFLICT (plugin_id, slot, subject_id, key) DO UPDATE SET
                    title = excluded.title, text = excluded.text, icon = excluded.icon,
                    strength = excluded.strength, from_local_ai = excluded.from_local_ai, updated_at = excluded.updated_at;
                """,
                new
                {
                    row.PluginId,
                    row.Slot,
                    row.SubjectId,
                    row.Key,
                    row.Title,
                    row.Text,
                    row.Icon,
                    row.Strength,
                    FromLocalAi = row.FromLocalAi ? 1 : 0,
                    at = Text(row.UpdatedAt),
                },
                transaction);
            return connection.Execute(
                """
                DELETE FROM plugin_display_contributions WHERE rowid IN (
                    SELECT rowid FROM plugin_display_contributions WHERE plugin_id = @PluginId
                    ORDER BY updated_at DESC, rowid DESC LIMIT -1 OFFSET @maxPerPlugin);
                """,
                new { row.PluginId, maxPerPlugin },
                transaction);
        }, ct);
    }

    public Task<bool> DeleteAsync(string pluginId, string slot, string subjectId, string key, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        return _db.ExecuteWriteAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            return connection.Execute(
                "DELETE FROM plugin_display_contributions WHERE plugin_id = @pluginId AND slot = @slot AND subject_id = @subjectId AND key = @key;",
                new { pluginId, slot, subjectId, key },
                transaction) > 0;
        }, ct);
    }

    public Task<IReadOnlyList<PluginDisplayRow>> ListAsync(string slot, string subjectId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(slot);
        ct.ThrowIfCancellationRequested();
        using var conn = _db.CreateConnection();
        IReadOnlyList<PluginDisplayRow> rows = conn.Query<(string PluginId, string Slot, string SubjectId, string Key, string? Title, string Text, string? Icon, int Strength, int Ai, string UpdatedAt)>(
                """
                SELECT plugin_id, slot, subject_id, key, title, text, icon, strength, from_local_ai, updated_at
                FROM plugin_display_contributions WHERE slot = @slot AND subject_id = @subjectId
                ORDER BY strength DESC, updated_at DESC, plugin_id, key;
                """,
                new { slot, subjectId })
            .Select(row => new PluginDisplayRow(
                row.PluginId, row.Slot, row.SubjectId, row.Key, row.Title, row.Text, row.Icon, row.Strength, row.Ai != 0,
                DateTimeOffset.Parse(row.UpdatedAt, CultureInfo.InvariantCulture)))
            .ToList();
        return Task.FromResult(rows);
    }

    public Task<int> CountAsync(string pluginId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        ct.ThrowIfCancellationRequested();
        using var conn = _db.CreateConnection();
        return Task.FromResult(conn.ExecuteScalar<int>("SELECT COUNT(*) FROM plugin_display_contributions WHERE plugin_id = @pluginId;", new { pluginId }));
    }

    private static string Text(DateTimeOffset value) => value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
}
