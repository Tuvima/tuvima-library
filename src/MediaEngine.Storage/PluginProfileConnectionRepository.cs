using System.Globalization;
using Dapper;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Storage;

/// <summary>SQLite implementation of <see cref="IPluginProfileConnectionRepository"/>, backed by <c>plugin_profile_connections</c>.</summary>
public sealed class PluginProfileConnectionRepository : IPluginProfileConnectionRepository
{
    private readonly IDatabaseConnection _db;

    public PluginProfileConnectionRepository(IDatabaseConnection db)
    {
        ArgumentNullException.ThrowIfNull(db);
        _db = db;
    }

    public Task<IReadOnlyList<PluginProfileConnectionRow>> ListAsync(string pluginId, Guid profileId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        ct.ThrowIfCancellationRequested();
        using var conn = _db.CreateConnection();
        IReadOnlyList<PluginProfileConnectionRow> rows = conn.Query<(string Name, string LinkedAt)>(
                """
                SELECT credential_name, linked_at FROM plugin_profile_connections
                WHERE plugin_id = @pluginId AND profile_id = @profile ORDER BY credential_name;
                """,
                new { pluginId, profile = GuidSql.ToBlob(profileId) })
            .Select(row => new PluginProfileConnectionRow(row.Name, DateTimeOffset.Parse(row.LinkedAt, CultureInfo.InvariantCulture)))
            .ToList();
        return Task.FromResult(rows);
    }

    public Task<string?> GetCiphertextAsync(string pluginId, Guid profileId, string credentialName, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        ArgumentException.ThrowIfNullOrWhiteSpace(credentialName);
        ct.ThrowIfCancellationRequested();
        using var conn = _db.CreateConnection();
        return Task.FromResult(conn.ExecuteScalar<string?>(
            """
            SELECT secret_ciphertext FROM plugin_profile_connections
            WHERE plugin_id = @pluginId AND profile_id = @profile AND credential_name = @credentialName;
            """,
            new { pluginId, profile = GuidSql.ToBlob(profileId), credentialName }));
    }

    public Task UpsertAsync(string pluginId, Guid profileId, string credentialName, string ciphertext, DateTimeOffset linkedAt, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        ArgumentException.ThrowIfNullOrWhiteSpace(credentialName);
        ArgumentException.ThrowIfNullOrWhiteSpace(ciphertext);
        return _db.ExecuteWriteAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            connection.Execute(
                """
                INSERT INTO plugin_profile_connections (plugin_id, profile_id, credential_name, secret_ciphertext, linked_at)
                VALUES (@pluginId, @profile, @credentialName, @ciphertext, @linkedAt)
                ON CONFLICT (plugin_id, profile_id, credential_name)
                DO UPDATE SET secret_ciphertext = excluded.secret_ciphertext, linked_at = excluded.linked_at;
                """,
                new { pluginId, profile = GuidSql.ToBlob(profileId), credentialName, ciphertext, linkedAt = linkedAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture) },
                transaction);
        }, ct);
    }

    public Task<bool> DeleteAsync(string pluginId, Guid profileId, string credentialName, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        ArgumentException.ThrowIfNullOrWhiteSpace(credentialName);
        return _db.ExecuteWriteAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            return connection.Execute(
                "DELETE FROM plugin_profile_connections WHERE plugin_id = @pluginId AND profile_id = @profile AND credential_name = @credentialName;",
                new { pluginId, profile = GuidSql.ToBlob(profileId), credentialName },
                transaction) > 0;
        }, ct);
    }

    public Task<IReadOnlyList<Guid>> ListProfilesAsync(string pluginId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        ct.ThrowIfCancellationRequested();
        using var conn = _db.CreateConnection();
        IReadOnlyList<Guid> ids = conn.Query<byte[]>(
                "SELECT DISTINCT profile_id FROM plugin_profile_connections WHERE plugin_id = @pluginId;", new { pluginId })
            .Select(bytes => GuidSql.FromDb(bytes))
            .ToList();
        return Task.FromResult(ids);
    }
}
