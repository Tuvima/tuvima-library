using MediaEngine.Storage.Schema.Upgrades;
using MediaEngine.TestSupport;
using Microsoft.Data.Sqlite;

namespace MediaEngine.Storage.Tests;

public sealed class DataStoreVersionRepositoryTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"tuvima_version_{Guid.NewGuid():N}.db");
    private readonly DatabaseConnection _database;

    public DataStoreVersionRepositoryTests()
    {
        _database = new DatabaseConnection(_dbPath);
    }

    public void Dispose()
    {
        _database.Dispose();
        TestTemp.DeleteDatabase(_dbPath);
    }

    [Fact]
    public void GetCurrent_WithNoRecordedStep_ReturnsNull()
    {
        CreateRecordTable();

        Assert.Null(new DataStoreVersionRepository(_database).GetCurrent());
    }

    [Fact]
    public void GetCurrent_ReturnsTheStepWithTheHighestNumber()
    {
        CreateRecordTable();
        Record(1, "baseline", "2026-10-01T08:00:00.0000000Z");
        Record(3, "add_third_thing", "2026-10-18T09:30:00.0000000Z");
        Record(2, "add_second_thing", "2026-10-10T12:00:00.0000000Z");

        var current = new DataStoreVersionRepository(_database).GetCurrent();

        Assert.NotNull(current);
        Assert.Equal(3, current.Number);
        Assert.Equal("add_third_thing", current.Name);
        Assert.Equal(new DateTimeOffset(2026, 10, 18, 9, 30, 0, TimeSpan.Zero), current.AppliedAt);
        Assert.Equal(TimeSpan.Zero, current.AppliedAt.Offset);
    }

    [Fact]
    public void GetCurrent_OnAFreshlyUpgradedDataStore_ReportsTheLatestProductStep()
    {
        _database.InitializeSchema();

        var current = new DataStoreVersionRepository(_database).GetCurrent();

        Assert.NotNull(current);
        var latest = DataStoreUpgradeCatalog.ForProduct()[^1];
        Assert.Equal(latest.Number, current.Number);
        Assert.Equal(latest.Name, current.Name);
    }

    private void CreateRecordTable() =>
        Execute("""
            CREATE TABLE data_store_upgrades (
                number         INTEGER NOT NULL PRIMARY KEY,
                name           TEXT    NOT NULL,
                fingerprint    TEXT    NOT NULL,
                applied_at     TEXT    NOT NULL,
                engine_version TEXT    NOT NULL
            );
            """);

    private void Record(int number, string name, string appliedAt) =>
        Execute($"INSERT INTO data_store_upgrades VALUES ({number}, '{name}', 'fingerprint', '{appliedAt}', '1.0.0');");

    private void Execute(string sql)
    {
        using var connection = new SqliteConnection($"Data Source={_dbPath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
