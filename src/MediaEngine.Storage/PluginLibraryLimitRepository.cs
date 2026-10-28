using Dapper;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Storage;

/// <summary>SQLite implementation of <see cref="IPluginLibraryLimitRepository"/>, backed by <c>plugin_library_limits</c>.</summary>
public sealed class PluginLibraryLimitRepository : IPluginLibraryLimitRepository
{
    private readonly IDatabaseConnection _db;

    public PluginLibraryLimitRepository(IDatabaseConnection db)
    {
        ArgumentNullException.ThrowIfNull(db);
        _db = db;
    }

    public Task<IReadOnlyList<string>> GetAsync(string pluginId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        ct.ThrowIfCancellationRequested();
        using var conn = _db.CreateConnection();
        IReadOnlyList<string> ids = conn.Query<string>(
            "SELECT library_id FROM plugin_library_limits WHERE plugin_id = @pluginId ORDER BY library_id;",
            new { pluginId }).ToList();
        return Task.FromResult(ids);
    }

    public Task ReplaceAsync(string pluginId, IReadOnlyCollection<string> libraryIds, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        ArgumentNullException.ThrowIfNull(libraryIds);
        return _db.ExecuteWriteAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            connection.Execute("DELETE FROM plugin_library_limits WHERE plugin_id = @pluginId;", new { pluginId }, transaction);
            foreach (var libraryId in libraryIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal))
            {
                connection.Execute(
                    "INSERT INTO plugin_library_limits (plugin_id, library_id) VALUES (@pluginId, @libraryId);",
                    new { pluginId, libraryId }, transaction);
            }
        }, ct);
    }

    public Task DeleteForPluginAsync(string pluginId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        return _db.ExecuteWriteAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            connection.Execute("DELETE FROM plugin_library_limits WHERE plugin_id = @pluginId;", new { pluginId }, transaction);
        }, ct);
    }
}
