using Dapper;
using MediaEngine.Domain.Contracts;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Storage;

/// <summary>
/// Keeps the tools banner's "Not now" answers in the data store's own key/value table (<c>storage_metadata</c>), one
/// <c>tool_install_declined:{tool id}</c> row per declined tool. The table is server-wide and is kept across a
/// development reset of the library's content, which is what a decline about the server's tools should do.
/// </summary>
public sealed class ToolPromptPreferenceRepository : IToolPromptPreferenceRepository
{
    private const string KeyPrefix = "tool_install_declined:";

    private readonly IDatabaseConnection _db;

    public ToolPromptPreferenceRepository(IDatabaseConnection db)
    {
        ArgumentNullException.ThrowIfNull(db);
        _db = db;
    }

    public Task<IReadOnlySet<string>> GetDeclinedAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = _db.CreateConnection();
        var keys = connection.Query<string>(
            "SELECT key FROM storage_metadata WHERE substr(key, 1, @length) = @prefix;",
            new { length = KeyPrefix.Length, prefix = KeyPrefix });
        IReadOnlySet<string> ids = keys
            .Select(key => key[KeyPrefix.Length..])
            .ToHashSet(StringComparer.Ordinal);
        return Task.FromResult(ids);
    }

    public Task DeclineAsync(string toolId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(toolId);
        using var connection = _db.CreateConnection();
        connection.Execute(
            "INSERT OR REPLACE INTO storage_metadata(key, value) VALUES (@key, '1');",
            new { key = KeyPrefix + toolId });
        return Task.CompletedTask;
    }

    public Task ClearDeclinedAsync(string toolId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(toolId);
        using var connection = _db.CreateConnection();
        connection.Execute("DELETE FROM storage_metadata WHERE key = @key;", new { key = KeyPrefix + toolId });
        return Task.CompletedTask;
    }
}
