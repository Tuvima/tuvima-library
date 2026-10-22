using Dapper;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Storage.Contracts;
using Microsoft.Data.Sqlite;

namespace MediaEngine.Storage;

/// <summary>
/// SQLite cache for UI settings. Stores the JSON blob for each scope
/// (global, device:{class}, profile:{uuid}) so API reads do not require
/// filesystem I/O on every request.
///
/// <para>
/// The cache is rebuilt from config files on Engine startup and updated
/// on every save operation.
/// </para>
/// </summary>
public sealed class UISettingsCacheRepository
{
    private readonly IDatabaseConnection _db;

    public UISettingsCacheRepository(IDatabaseConnection db)
    {
        ArgumentNullException.ThrowIfNull(db);
        _db = db;
    }

    /// <summary>
    /// Reads a cached settings JSON blob by scope key.
    /// Returns <c>null</c> if no cache entry exists for the given scope.
    /// </summary>
    /// <param name="scope">
    /// Scope key: <c>"global"</c>, <c>"device:web"</c>, <c>"profile:{uuid}"</c>.
    /// </param>
    public string? Get(string scope)
    {
        using var conn = _db.CreateConnection();
        return conn.ExecuteScalar<string?>(
            "SELECT settings FROM ui_settings_cache WHERE scope = @scope",
            new { scope });
    }

    /// <summary>
    /// Inserts or updates a cache entry.
    /// </summary>
    public void Upsert(string scope, string settingsJson)
    {
        using var conn = _db.CreateConnection();
        conn.Execute("""
            INSERT INTO ui_settings_cache (scope, settings, cached_at)
            VALUES (@scope, @settingsJson, strftime('%Y-%m-%dT%H:%M:%fZ','now'))
            ON CONFLICT(scope)
            DO UPDATE SET settings = excluded.settings, cached_at = excluded.cached_at
            """, new { scope, settingsJson });
    }

    /// <summary>
    /// Deletes a cache entry by scope.
    /// </summary>
    public void Delete(string scope)
    {
        using var conn = _db.CreateConnection();
        conn.Execute(
            "DELETE FROM ui_settings_cache WHERE scope = @scope",
            new { scope });
    }

    /// <summary>
    /// Deletes all cache entries (used before a full rebuild).
    /// </summary>
    public void Clear()
    {
        using var conn = _db.CreateConnection();
        conn.Execute("DELETE FROM ui_settings_cache");
    }

    /// <summary>
    /// Rebuilds the cache from configuration files.
    /// Called on Engine startup to ensure the cache reflects the current file state.
    /// </summary>
    public Task RebuildFromFilesAsync(IConfigurationLoader configLoader, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(configLoader);

        return _db.ExecuteWriteAsync((conn, transaction, innerCt) =>
        {
            // Clear existing cache — must use raw command here because the transaction
            // cannot be passed through Dapper's Execute overload for SqliteTransaction
            using (var clearCmd = conn.CreateCommand())
            {
                clearCmd.Transaction = transaction;
                clearCmd.CommandText = "DELETE FROM ui_settings_cache;";
                clearCmd.ExecuteNonQuery();
            }

            // Cache global settings
            var global = configLoader.LoadConfig<UIGlobalSettings>("ui", "global");
            if (global is not null)
            {
                UpsertInTransaction(conn, transaction, "global",
                    System.Text.Json.JsonSerializer.Serialize(global));
            }

            var libraryPreferences = configLoader.LoadLibraryPreferences();
            if (libraryPreferences is not null)
            {
                UpsertInTransaction(conn, transaction, "library-preferences",
                    System.Text.Json.JsonSerializer.Serialize(libraryPreferences));
            }

            // Cache device profiles
            string[] deviceClasses = ["web", "mobile", "television", "automotive"];
            foreach (var dc in deviceClasses)
            {
                var device = configLoader.LoadConfig<UIDeviceProfile>("ui/devices", dc);
                if (device is not null)
                {
                    UpsertInTransaction(conn, transaction, $"device:{dc}",
                        System.Text.Json.JsonSerializer.Serialize(device));
                }
            }

        }, ct);
    }

    private static void UpsertInTransaction(
        SqliteConnection conn, SqliteTransaction transaction, string scope, string settingsJson)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = """
            INSERT INTO ui_settings_cache (scope, settings, cached_at)
            VALUES (@scope, @settings, strftime('%Y-%m-%dT%H:%M:%fZ','now'))
            ON CONFLICT(scope)
            DO UPDATE SET settings = excluded.settings, cached_at = excluded.cached_at;
            """;
        cmd.Parameters.AddWithValue("@scope", scope);
        cmd.Parameters.AddWithValue("@settings", settingsJson);
        cmd.ExecuteNonQuery();
    }
}
