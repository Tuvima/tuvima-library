using Dapper;
using MediaEngine.Api.Services.HealthChecks;
using MediaEngine.Storage;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace MediaEngine.Api.Tests;

/// <summary>
/// The data store health check must prove the file is writable without changing the main schema, because a schema
/// change made concurrent search_index (FTS5) readers fail with SQLite error 17 and reported a healthy store as broken.
/// </summary>
public sealed class SqliteHealthCheckTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"tuvima-health-{Guid.NewGuid():N}.db");
    private readonly DatabaseConnection _database;

    public SqliteHealthCheckTests()
    {
        DapperConfiguration.Configure();
        _database = new DatabaseConnection(_databasePath);
        _database.InitializeSchema();
    }

    [Fact]
    public async Task Check_OnHealthyStore_IsHealthy_AndLeavesTheMainSchemaVersionUnchanged()
    {
        using var conn = _database.CreateConnection();
        var before = conn.ExecuteScalar<long>("PRAGMA schema_version;");

        var result = await new SqliteHealthCheck(_database).CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Equal(before, conn.ExecuteScalar<long>("PRAGMA schema_version;"));
    }

    [Fact]
    public async Task Check_RunRepeatedlyBesideSearchIndexQueries_StaysHealthy()
    {
        var check = new SqliteHealthCheck(_database);
        var searching = Task.Run(() =>
        {
            for (var i = 0; i < 100; i++)
            {
                using var conn = _database.CreateConnection();
                conn.Query<string>("SELECT entity_id FROM search_index WHERE search_index MATCH 'dune';").ToList();
            }
        });

        for (var i = 0; i < 20; i++)
        {
            var result = await check.CheckHealthAsync(new HealthCheckContext());
            Assert.Equal(HealthStatus.Healthy, result.Status);
        }

        await searching;
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var path in new[] { _databasePath, _databasePath + "-wal", _databasePath + "-shm" })
        {
            try { File.Delete(path); }
            catch (IOException) { /* temp file still in use; the OS temp cleaner removes it */ }
        }
    }
}
