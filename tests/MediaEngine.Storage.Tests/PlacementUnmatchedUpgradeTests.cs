using MediaEngine.Storage.Schema.Upgrades;
using MediaEngine.TestSupport;
using Microsoft.Data.Sqlite;

namespace MediaEngine.Storage.Tests;

/// <summary>
/// A data store built at the step before "placement_unmatched", holding titles and editions, upgrades through that step once: every
/// existing row starts placed normally (0), the mark can be 0 or 1 and nothing else.
/// </summary>
public sealed class PlacementUnmatchedUpgradeTests : IDisposable
{
    private const string StepName = "placement_unmatched";

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"tuvima_unmatched_{Guid.NewGuid():N}.db");
    private readonly SqliteConnection _connection;
    private readonly DataStoreUpgradeRunner _runner = new();

    public PlacementUnmatchedUpgradeTests()
    {
        _connection = new SqliteConnection($"Data Source={_dbPath}");
        _connection.Open();
        Execute("PRAGMA foreign_keys = ON;");
    }

    public void Dispose()
    {
        _connection.Dispose();
        TestTemp.DeleteDatabase(_dbPath);
    }

    [Fact]
    public void AnExistingLibrary_UpgradesOnce_AndEveryTitleAndEditionStartsPlacedNormally()
    {
        var all = DataStoreUpgradeCatalog.ForProduct();
        var step = all.Single(candidate => candidate.Name == StepName);
        _runner.Run(_connection, all.Where(candidate => candidate.Number < step.Number).ToList(), Options());
        var work = Guid.NewGuid();
        var edition = Guid.NewGuid();
        Execute($"""
            INSERT INTO works (id, media_type, work_kind) VALUES ({Blob(work)}, 'Music', 'standalone');
            INSERT INTO editions (id, work_id) VALUES ({Blob(edition)}, {Blob(work)});
            """);

        var first = _runner.Run(_connection, all, Options());
        var second = _runner.Run(_connection, all, Options());

        Assert.Contains(first.Applied, applied => applied.Number == step.Number);
        Assert.DoesNotContain(second.Applied, applied => applied.Number == step.Number);
        Assert.Equal(0, Scalar("SELECT placement_unmatched FROM works;"));
        Assert.Equal(0, Scalar("SELECT placement_unmatched FROM editions;"));
    }

    [Fact]
    public void TheMark_CanBeSetOrCleared_ButHoldsNothingElse()
    {
        var all = DataStoreUpgradeCatalog.ForProduct();
        _runner.Run(_connection, all, Options());
        var work = Guid.NewGuid();
        Execute($"""
            INSERT INTO works (id, media_type, work_kind) VALUES ({Blob(work)}, 'Music', 'standalone');
            INSERT INTO editions (id, work_id) VALUES ({Blob(Guid.NewGuid())}, {Blob(work)});
            """);

        Execute($"UPDATE works SET placement_unmatched = 1 WHERE id = {Blob(work)};");
        Assert.Equal(1, Scalar("SELECT placement_unmatched FROM works;"));
        Execute($"UPDATE works SET placement_unmatched = 0 WHERE id = {Blob(work)};");
        Assert.Equal(0, Scalar("SELECT placement_unmatched FROM works;"));

        Assert.Throws<SqliteException>(() => Execute($"UPDATE works SET placement_unmatched = 2 WHERE id = {Blob(work)};"));
        Assert.Throws<SqliteException>(() => Execute("UPDATE editions SET placement_unmatched = 5;"));
    }

    private static string Blob(Guid id) => $"X'{Convert.ToHexString(id.ToByteArray())}'";

    private static DataStoreUpgradeOptions Options()
        => new() { Clock = TimeProvider.System, EngineVersion = "test" };

    private void Execute(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private long Scalar(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }
}
