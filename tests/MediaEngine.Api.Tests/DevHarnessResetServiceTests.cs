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

    private static long Scalar(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }
}
