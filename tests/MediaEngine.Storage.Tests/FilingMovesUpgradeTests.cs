using MediaEngine.Storage.Schema.Upgrades;
using MediaEngine.TestSupport;
using Microsoft.Data.Sqlite;

namespace MediaEngine.Storage.Tests;

/// <summary>
/// A data store built at the step before "filing_moves_wait_for_the_batch", holding file write rows (a tag write and a library move
/// part-way), upgrades through that step: every row survives the rebuild, a row that had a move step becomes a person's move, and the
/// list can now hold a filing move that waits for the next run or is held for the first check.
/// </summary>
public sealed class FilingMovesUpgradeTests : IDisposable
{
    private const string StepName = "filing_moves_wait_for_the_batch";

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"tuvima_filing_{Guid.NewGuid():N}.db");
    private readonly SqliteConnection _connection;
    private readonly DataStoreUpgradeRunner _runner = new();

    public FilingMovesUpgradeTests()
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
    public void ExistingRowsSurviveTheRebuild_AndAMoveInProgressBecomesAPersonsMove()
    {
        var all = DataStoreUpgradeCatalog.ForProduct();
        var step = all.Single(candidate => candidate.Name == StepName);
        _runner.Run(_connection, all.Where(candidate => candidate.Number < step.Number).ToList(), Options());

        var work = Guid.NewGuid();
        var edition = Guid.NewGuid();
        var tagged = Guid.NewGuid();
        var moving = Guid.NewGuid();
        Execute($"""
            INSERT INTO works (id, media_type, work_kind) VALUES ({Blob(work)}, 'Music', 'child');
            INSERT INTO editions (id, work_id) VALUES ({Blob(edition)}, {Blob(work)});
            INSERT INTO media_assets (id, edition_id, content_hash, file_path_root) VALUES ({Blob(tagged)}, {Blob(edition)}, 'h1', '/m/a.flac');
            INSERT INTO media_assets (id, edition_id, content_hash, file_path_root) VALUES ({Blob(moving)}, {Blob(edition)}, 'h2', '/m/b.flac');
            INSERT INTO media_file_write_intents (id, target_kind, asset_id, generation, operation_token, trigger, status, attempts, created_at, updated_at)
                VALUES ({Blob(Guid.NewGuid())}, 'media_asset', {Blob(tagged)}, 3, 't1', 'auto_match', 'pending', 2, '2026-10-22T09:00:00Z', '2026-10-22T09:00:00Z');
            INSERT INTO media_file_write_intents
                (id, target_kind, asset_id, generation, operation_token, trigger, status, created_at, updated_at,
                 move_step, move_source_path, move_target_path, move_source_hash, move_generation, recovery_attempts, claimed_run_id)
                VALUES ({Blob(Guid.NewGuid())}, 'media_asset', {Blob(moving)}, 5, 't2', 'library_move', 'pending', '2026-10-22T09:00:00Z', '2026-10-22T09:00:00Z',
                 'planned', '/m/b.flac', '/m/c.flac', 'hash', 5, 1, 'run-1');
            """);

        var result = _runner.Run(_connection, all, Options());

        Assert.Contains(result.Applied, applied => applied.Number == step.Number);
        Assert.Equal(2, Scalar<long>("SELECT COUNT(*) FROM media_file_write_intents;"));
        Assert.Equal(
            ("auto_match", 3L, 2L, (string?)null, (string?)null),
            Row($"SELECT trigger, generation, attempts, move_step, move_origin FROM media_file_write_intents WHERE asset_id = {Blob(tagged)};"));
        Assert.Equal(
            ("planned", "person", "/m/b.flac", "/m/c.flac", "hash", 5L, 1L, "run-1"),
            Row2($"SELECT move_step, move_origin, move_source_path, move_target_path, move_source_hash, move_generation, recovery_attempts, claimed_run_id FROM media_file_write_intents WHERE asset_id = {Blob(moving)};"));

        // The indexes came back with the rebuilt table.
        var indexes = Query<string>("SELECT name FROM sqlite_master WHERE type = 'index' AND tbl_name = 'media_file_write_intents';");
        Assert.Contains("ux_media_file_write_intents_asset", indexes);
        Assert.Contains("ux_media_file_write_intents_local_item", indexes);
        Assert.Contains("ix_media_file_write_intents_dispatch", indexes);
        Assert.Contains("ix_media_file_write_intents_move", indexes);
    }

    [Fact]
    public void TheRebuiltList_AcceptsWaitingAndHeldFilingMoves_AndRefusesAMoveWithoutItsOriginOrAHeldPersonsMove()
    {
        _runner.Run(_connection, DataStoreUpgradeCatalog.ForProduct(), Options());
        var work = Guid.NewGuid();
        var edition = Guid.NewGuid();
        Execute($"""
            INSERT INTO works (id, media_type, work_kind) VALUES ({Blob(work)}, 'Music', 'child');
            INSERT INTO editions (id, work_id) VALUES ({Blob(edition)}, {Blob(work)});
            """);
        var assets = Enumerable.Range(0, 4).Select(_ => Guid.NewGuid()).ToArray();
        foreach (var asset in assets)
        {
            Execute($"INSERT INTO media_assets (id, edition_id, content_hash, file_path_root) VALUES ({Blob(asset)}, {Blob(edition)}, '{asset:N}', '/m/{asset:N}.flac');");
        }

        Execute(Insert(assets[0], "'waiting'", "'filing'"));
        Execute(Insert(assets[1], "'held'", "'filing'"));

        Assert.Throws<SqliteException>(() => Execute(Insert(assets[2], "'waiting'", "NULL")));
        Assert.Throws<SqliteException>(() => Execute(Insert(assets[2], "'held'", "'person'")));
        Assert.Throws<SqliteException>(() => Execute(Insert(assets[2], "NULL", "'filing'")));
        Assert.Throws<SqliteException>(() => Execute(Insert(assets[3], "'sometime'", "'filing'")));
        Assert.Equal(2, Scalar<long>("SELECT COUNT(*) FROM media_file_write_intents;"));
    }

    private static string Insert(Guid asset, string step, string origin) => $"""
        INSERT INTO media_file_write_intents
            (id, target_kind, asset_id, operation_token, trigger, status, created_at, updated_at, move_step, move_origin)
        VALUES ({Blob(Guid.NewGuid())}, 'media_asset', {Blob(asset)}, 't', 'filing', 'pending', '2026-10-22T09:00:00Z', '2026-10-22T09:00:00Z', {step}, {origin});
        """;

    private static string Blob(Guid id) => $"X'{Convert.ToHexString(id.ToByteArray())}'";

    private static DataStoreUpgradeOptions Options()
        => new() { Clock = TimeProvider.System, EngineVersion = "test" };

    private void Execute(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private T Scalar<T>(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        return (T)Convert.ChangeType(command.ExecuteScalar()!, typeof(T), System.Globalization.CultureInfo.InvariantCulture);
    }

    private (string, long, long, string?, string?) Row(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        return (reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2),
            reader.IsDBNull(3) ? null : reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4));
    }

    private (string, string, string, string, string, long, long, string) Row2(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        return (reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
            reader.GetInt64(5), reader.GetInt64(6), reader.GetString(7));
    }

    private List<T> Query<T>(string sql)
    {
        var rows = new List<T>();
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add((T)reader.GetValue(0));
        }

        return rows;
    }
}
