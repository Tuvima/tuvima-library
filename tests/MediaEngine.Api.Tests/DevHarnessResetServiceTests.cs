using System.Reflection;
using MediaEngine.Api.DevSupport;
using MediaEngine.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Api.Tests;

public sealed class DevHarnessResetServiceTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"tuvima_harness_reset_{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var file in new[] { _path, $"{_path}-wal", $"{_path}-shm" })
        {
            if (File.Exists(file))
            {
                File.Delete(file);
            }
        }
    }

    [Fact]
    public async Task DevHarnessDatabaseReset_ReappliesTheBaseline()
    {
        using var database = StartedDatabase();
        var service = NewService(database);

        await InvokeResetAsync(service, "ResetDatabaseAsync");

        using (var conn = database.CreateConnection())
        {
            Assert.Equal(1L, Scalar(conn, "SELECT COUNT(*) FROM data_store_upgrades WHERE number = 1 AND name = 'baseline';"));
            Assert.Equal(1L, Scalar(conn, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'view' AND name = 'work_owned_assets';"));
            Assert.Equal(1L, Scalar(conn, "SELECT COUNT(*) FROM applications WHERE id = X'00000000000000000000000000000004';"));
            Assert.Equal(16L, Scalar(conn, "SELECT COUNT(*) FROM metadata_providers;"));
        }

        // The next normal start finds a current data store and applies nothing.
        database.InitializeSchema();
        database.RunStartupChecks();
        Assert.Empty(database.StartupNotes);
    }

    [Fact]
    public async Task LibraryDataReset_ClearsFullTextIndexesWithoutCorruptingThem()
    {
        using var database = StartedDatabase();
        using (var conn = database.CreateConnection())
        {
            Execute(conn, """
                INSERT INTO artwork_asset_search (artwork_asset_id, search_text) VALUES ('art-1', 'dune desert poster');
                INSERT INTO artwork_asset_search (artwork_asset_id, search_text) VALUES ('art-2', 'arrakis spice cover');
                INSERT INTO search_index (entity_id, title) VALUES ('work-1', 'Dune');
                INSERT INTO local_item_search (title, file_name) VALUES ('Beach sunset', 'IMG_0001.jpg');
                """);
        }

        await InvokeResetAsync(NewService(database), "ResetLibraryDatabaseAsync");

        using (var conn = database.CreateConnection())
        {
            using (var integrityCmd = conn.CreateCommand())
            {
                integrityCmd.CommandText = "PRAGMA integrity_check;";
                Assert.Equal("ok", integrityCmd.ExecuteScalar() as string);
            }

            Assert.Equal(0L, Scalar(conn, "SELECT COUNT(*) FROM artwork_asset_search;"));
            Assert.Equal(0L, Scalar(conn, "SELECT COUNT(*) FROM artwork_asset_search WHERE artwork_asset_search MATCH 'desert';"));
            Assert.Equal(0L, Scalar(conn, "SELECT COUNT(*) FROM search_index;"));

            // The indexes still accept and find new rows after the reset.
            Execute(conn, "INSERT INTO artwork_asset_search (artwork_asset_id, search_text) VALUES ('art-3', 'fresh backdrop');");
            Assert.Equal(1L, Scalar(conn, "SELECT COUNT(*) FROM artwork_asset_search WHERE artwork_asset_search MATCH 'backdrop';"));
            Execute(conn, "INSERT INTO artwork_asset_search (artwork_asset_search) VALUES ('integrity-check');");

            // The local item index is preserved, and still searchable, after a library-only reset.
            Assert.Equal(1L, Scalar(conn, "SELECT COUNT(*) FROM local_item_search WHERE local_item_search MATCH 'sunset';"));
            Execute(conn, "INSERT INTO local_item_search (local_item_search) VALUES ('integrity-check');");
        }
    }

    private DatabaseConnection StartedDatabase()
    {
        var database = new DatabaseConnection(_path);
        database.InitializeSchema();
        database.RunStartupChecks();
        return database;
    }

    private static DevHarnessResetService NewService(DatabaseConnection database) =>
        new(database, null!, null!, null!, null!, NullLogger<DevHarnessResetService>.Instance);

    private static async Task InvokeResetAsync(DevHarnessResetService service, string methodName)
    {
        var method = typeof(DevHarnessResetService).GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        await (Task)method!.Invoke(service, [new List<string>(), CancellationToken.None])!;
    }

    private static void Execute(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static long Scalar(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }
}
