using MediaEngine.Storage.Schema.Upgrades;
using MediaEngine.TestSupport;
using Microsoft.Data.Sqlite;

namespace MediaEngine.Storage.Tests;

public sealed class DataStoreUpgradeRunnerTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"tuvima_upgrade_{Guid.NewGuid():N}.db");
    private readonly SqliteConnection _connection;
    private readonly DataStoreUpgradeRunner _runner = new();

    public DataStoreUpgradeRunnerTests()
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
    public void PendingSteps_ApplyOnceInNumberOrder_AndAreRecorded()
    {
        var clock = new FixedClock(new DateTimeOffset(2026, 10, 18, 9, 30, 0, TimeSpan.Zero));
        var steps = new DataStoreUpgradeStep[]
        {
            Sql(2, "add_note", "ALTER TABLE things ADD COLUMN note TEXT;\nINSERT INTO order_log (step) VALUES (2);"),
            Sql(1, "create_things", "CREATE TABLE order_log (n INTEGER PRIMARY KEY AUTOINCREMENT, step INTEGER);\nCREATE TABLE things (id INTEGER PRIMARY KEY);\nINSERT INTO order_log (step) VALUES (1);"),
            new RecordingCodeStep(3, "code_third", "INSERT INTO order_log (step) VALUES (3);"),
        };

        var result = _runner.Run(_connection, steps, Options(clock, "1.2.3"));

        Assert.Equal(0, result.FromNumber);
        Assert.Equal(3, result.ToNumber);
        Assert.Equal([1, 2, 3], result.Applied.Select(applied => applied.Number));
        Assert.Equal([1L, 2L, 3L], Query<long>("SELECT step FROM order_log ORDER BY n;"));

        var records = ReadRecords();
        Assert.Equal([1L, 2L, 3L], records.Select(record => record.Number));
        Assert.Equal(["create_things", "add_note", "code_third"], records.Select(record => record.Name));
        Assert.All(records, record => Assert.Equal("1.2.3", record.Version));
        Assert.All(records, record => Assert.Equal("2026-10-18T09:30:00.0000000Z", record.AppliedAt));
        Assert.Equal(steps.OrderBy(step => step.Number).Select(step => step.Fingerprint), records.Select(record => record.Fingerprint));
    }

    [Fact]
    public void SecondRun_AppliesNothing()
    {
        var steps = TwoSteps();
        _runner.Run(_connection, steps, Options());

        var second = _runner.Run(_connection, steps, Options());

        Assert.Equal(2, second.FromNumber);
        Assert.Equal(2, second.ToNumber);
        Assert.Empty(second.Applied);
        Assert.Empty(second.DescribeForLog());
        Assert.Equal(2L, Scalar<long>("SELECT COUNT(*) FROM data_store_upgrades;"));
    }

    [Fact]
    public void NewStepsAddedLater_RunWithoutRepeatingEarlierOnes()
    {
        _runner.Run(_connection, TwoSteps(), Options());
        var steps = TwoSteps().Append(Sql(3, "add_tag", "ALTER TABLE things ADD COLUMN tag TEXT;")).ToList();

        var result = _runner.Run(_connection, steps, Options());

        Assert.Equal(2, result.FromNumber);
        Assert.Equal(3, result.ToNumber);
        Assert.Equal([3], result.Applied.Select(applied => applied.Number));
    }

    [Fact]
    public void FailingStep_RollsBackItsChanges_AndLeavesNoRecord()
    {
        var steps = new DataStoreUpgradeStep[]
        {
            Sql(1, "create_things", "CREATE TABLE things (id INTEGER PRIMARY KEY);"),
            Sql(2, "half_done", "CREATE TABLE half_done (id INTEGER);\nINSERT INTO table_that_does_not_exist VALUES (1);"),
            Sql(3, "never_runs", "CREATE TABLE never_runs (id INTEGER);"),
        };

        var exception = Assert.Throws<DataStoreUpgradeFailedException>(() => _runner.Run(_connection, steps, Options()));

        Assert.Equal(2, exception.StepNumber);
        Assert.Equal("half_done", exception.StepName);
        Assert.Contains("0002", exception.Message);
        Assert.Contains("half_done", exception.Message);
        Assert.Contains("table_that_does_not_exist", exception.Message);
        Assert.Contains("The data store was left as it was before this step.", exception.Message);
        Assert.Contains(Path.GetFileName(_dbPath), exception.Message);
        Assert.IsType<SqliteException>(exception.InnerException);
        Assert.Equal(1, TableCount("things"));
        Assert.Equal(0, TableCount("half_done"));
        Assert.Equal(0, TableCount("never_runs"));
        Assert.Equal([1L], Query<long>("SELECT number FROM data_store_upgrades ORDER BY number;"));
    }

    [Fact]
    public void FailingCodeStep_RollsBackItsChanges_AndLeavesNoRecord()
    {
        var steps = new DataStoreUpgradeStep[]
        {
            Sql(1, "create_things", "CREATE TABLE things (id INTEGER PRIMARY KEY);"),
            new RecordingCodeStep(2, "code_fails", "INSERT INTO things (id) VALUES (1);", failAfterRunning: true),
        };

        var exception = Assert.Throws<DataStoreUpgradeFailedException>(() => _runner.Run(_connection, steps, Options()));

        Assert.Contains("code_fails", exception.Message);
        Assert.Equal(0L, Scalar<long>("SELECT COUNT(*) FROM things;"));
        Assert.Equal([1L], Query<long>("SELECT number FROM data_store_upgrades;"));
    }

    [Fact]
    public void AppliedStepNewerThanEngine_RefusesWithInstallNewerMessage()
    {
        _runner.Run(_connection, TwoSteps(), Options());
        var olderEngine = new DataStoreUpgradeStep[] { TwoSteps()[0] };
        var hookCalls = 0;

        var exception = Assert.Throws<DataStoreUpgradeRefusedException>(
            () => _runner.Run(_connection, olderEngine, new DataStoreUpgradeOptions
            {
                BackupLocationHint = "the Backups folder",
                BeforeFirstPendingStep = _ =>
                {
                    hookCalls++;
                    return null;
                },
            }));

        Assert.Equal(0, hookCalls);
        Assert.Equal([1L, 2L], Query<long>("SELECT number FROM data_store_upgrades ORDER BY number;"));
        Assert.Contains(Path.GetFileName(_dbPath), exception.Message);
        Assert.Contains("newer version of Tuvima", exception.Message);
        Assert.Contains("0002", exception.Message);
        Assert.Contains("Install that version", exception.Message);
        Assert.Contains("the Backups folder", exception.Message);
    }

    [Fact]
    public void EditedAppliedStep_RefusesWithAddANewStepMessage()
    {
        _runner.Run(_connection, TwoSteps(), Options());
        var edited = new DataStoreUpgradeStep[]
        {
            TwoSteps()[0],
            Sql(2, "add_note", "ALTER TABLE things ADD COLUMN note TEXT NOT NULL DEFAULT '';"),
            Sql(3, "pending_step", "CREATE TABLE pending_table (id INTEGER);"),
        };

        var exception = Assert.Throws<DataStoreUpgradeRefusedException>(() => _runner.Run(_connection, edited, Options()));

        Assert.Equal(0, TableCount("pending_table"));
        Assert.Equal([1L, 2L], Query<long>("SELECT number FROM data_store_upgrades ORDER BY number;"));
        Assert.Contains(Path.GetFileName(_dbPath), exception.Message);
        Assert.Contains("0002", exception.Message);
        Assert.Contains("add_note", exception.Message);
        Assert.Contains("add a new step", exception.Message);
    }

    [Fact]
    public void RenamedAppliedStep_Refuses()
    {
        _runner.Run(_connection, TwoSteps(), Options());
        var renamed = new DataStoreUpgradeStep[]
        {
            TwoSteps()[0],
            Sql(2, "add_comment", "ALTER TABLE things ADD COLUMN note TEXT;"),
        };

        Assert.Throws<DataStoreUpgradeRefusedException>(() => _runner.Run(_connection, renamed, Options()));
    }

    [Fact]
    public void GapInAppliedRecord_Refuses()
    {
        var steps = new List<DataStoreUpgradeStep>(TwoSteps()) { Sql(3, "add_tag", "ALTER TABLE things ADD COLUMN tag TEXT;") };
        _runner.Run(_connection, steps, Options());
        Execute("DELETE FROM data_store_upgrades WHERE number = 2;");
        steps.Add(Sql(4, "pending_step", "CREATE TABLE pending_table (id INTEGER);"));

        var exception = Assert.Throws<DataStoreUpgradeRefusedException>(() => _runner.Run(_connection, steps, Options()));

        Assert.Equal(0, TableCount("pending_table"));
        Assert.Equal([1L, 3L], Query<long>("SELECT number FROM data_store_upgrades ORDER BY number;"));
        Assert.Contains(Path.GetFileName(_dbPath), exception.Message);
        Assert.Contains("damaged", exception.Message);
        Assert.Contains("Restore a backup", exception.Message);
    }

    [Fact]
    public void ForeignKeysOffStep_CanRebuildATable_AndForeignKeysAreOnAfterwards()
    {
        var steps = new DataStoreUpgradeStep[]
        {
            Sql(1, "parent_and_child", """
                CREATE TABLE parent (id INTEGER NOT NULL PRIMARY KEY, label TEXT NOT NULL);
                CREATE TABLE child (id INTEGER NOT NULL PRIMARY KEY, parent_id INTEGER NOT NULL REFERENCES parent(id) ON DELETE CASCADE);
                INSERT INTO parent (id, label) VALUES (1, 'a'), (2, 'b');
                INSERT INTO child (id, parent_id) VALUES (10, 1), (20, 2);
                """),
            Sql(2, "rebuild_parent", """
                -- tuvima:foreign-keys-off
                CREATE TABLE parent_rebuild (id INTEGER NOT NULL PRIMARY KEY, label TEXT NOT NULL, extra TEXT);
                INSERT INTO parent_rebuild (id, label) SELECT id, label FROM parent;
                DROP TABLE parent;
                ALTER TABLE parent_rebuild RENAME TO parent;
                """),
        };

        var result = _runner.Run(_connection, steps, Options());

        Assert.Equal(2, result.ToNumber);
        Assert.Equal(2L, Scalar<long>("SELECT COUNT(*) FROM child;"));
        Assert.Equal(2L, Scalar<long>("SELECT COUNT(*) FROM parent;"));
        Assert.Equal(1L, Scalar<long>("PRAGMA foreign_keys;"));
    }

    [Fact]
    public void ForeignKeysOffStep_LeavingOrphans_FailsAndRollsBack()
    {
        var steps = new DataStoreUpgradeStep[]
        {
            Sql(1, "parent_and_child", """
                CREATE TABLE parent (id INTEGER NOT NULL PRIMARY KEY);
                CREATE TABLE child (id INTEGER NOT NULL PRIMARY KEY, parent_id INTEGER NOT NULL REFERENCES parent(id));
                INSERT INTO parent (id) VALUES (1);
                INSERT INTO child (id, parent_id) VALUES (10, 1);
                """),
            Sql(2, "drop_parent_rows", """
                -- tuvima:foreign-keys-off
                DELETE FROM parent;
                """),
        };

        var exception = Assert.Throws<DataStoreUpgradeFailedException>(() => _runner.Run(_connection, steps, Options()));

        Assert.Contains("child", exception.Message);
        Assert.Equal(1L, Scalar<long>("SELECT COUNT(*) FROM parent;"));
        Assert.Equal([1L], Query<long>("SELECT number FROM data_store_upgrades;"));
        Assert.Equal(1L, Scalar<long>("PRAGMA foreign_keys;"));
    }

    [Fact]
    public void BeforeFirstPendingStep_RunsOnceOnlyForAnExistingDataStoreWithPendingSteps()
    {
        var calls = new List<DataStoreUpgradePlan>();
        var options = new DataStoreUpgradeOptions
        {
            BeforeFirstPendingStep = plan =>
            {
                calls.Add(plan);
                return Path.Combine("backups", "safety-copy.db");
            },
        };

        // Brand-new data store: nothing applied yet, so no safety copy is asked for.
        var fresh = _runner.Run(_connection, TwoSteps(), options);
        Assert.Empty(calls);
        Assert.Null(fresh.SafetyCopyPath);

        // Nothing pending: still no call.
        var nothing = _runner.Run(_connection, TwoSteps(), options);
        Assert.Empty(calls);
        Assert.Null(nothing.SafetyCopyPath);

        // Existing data store with two pending steps: one call, before the first of them.
        var steps = TwoSteps().Concat(
        [
            Sql(3, "add_tag", "ALTER TABLE things ADD COLUMN tag TEXT;"),
            Sql(4, "add_more", "ALTER TABLE things ADD COLUMN more TEXT;"),
        ]).ToList();
        var upgraded = _runner.Run(_connection, steps, options);

        var plan = Assert.Single(calls);
        Assert.Equal(2, plan.FromNumber);
        Assert.Equal([3, 4], plan.PendingSteps.Select(step => step.Number));
        Assert.Equal(Path.Combine("backups", "safety-copy.db"), upgraded.SafetyCopyPath);
        Assert.Contains(upgraded.DescribeForLog(), line => line.Contains("safety-copy.db", StringComparison.Ordinal));
    }

    [Fact]
    public void BeforeFirstPendingStep_PathAppearsInAFailureMessage()
    {
        _runner.Run(_connection, TwoSteps(), Options());
        var steps = TwoSteps().Append(Sql(3, "breaks", "INSERT INTO table_that_does_not_exist VALUES (1);")).ToList();
        var options = new DataStoreUpgradeOptions { BeforeFirstPendingStep = _ => "D:/backups/before-0003.db" };

        var exception = Assert.Throws<DataStoreUpgradeFailedException>(() => _runner.Run(_connection, steps, options));

        Assert.Equal("D:/backups/before-0003.db", exception.SafetyCopyPath);
        Assert.Contains("D:/backups/before-0003.db", exception.Message);
    }

    [Fact]
    public void BeforeFirstPendingStep_Throwing_RefusesAndAppliesNothing()
    {
        _runner.Run(_connection, TwoSteps(), Options());
        var steps = TwoSteps().Append(Sql(3, "pending_step", "CREATE TABLE pending_table (id INTEGER);")).ToList();
        var options = new DataStoreUpgradeOptions
        {
            BeforeFirstPendingStep = _ => throw new IOException("The disk is full."),
        };

        var exception = Assert.Throws<DataStoreUpgradeRefusedException>(() => _runner.Run(_connection, steps, options));

        Assert.StartsWith("Could not prepare the upgrade", exception.Message);
        Assert.Contains("The disk is full.", exception.Message);
        Assert.Contains(Path.GetFileName(_dbPath), exception.Message);
        Assert.IsType<IOException>(exception.InnerException);
        Assert.Equal(0, TableCount("pending_table"));
        Assert.Equal([1L, 2L], Query<long>("SELECT number FROM data_store_upgrades ORDER BY number;"));
    }

    [Fact]
    public void BeforeFirstPendingStep_ThrowingAnUpgradeException_IsNotWrapped()
    {
        _runner.Run(_connection, TwoSteps(), Options());
        var steps = TwoSteps().Append(Sql(3, "pending_step", "CREATE TABLE pending_table (id INTEGER);")).ToList();
        var options = new DataStoreUpgradeOptions
        {
            BeforeFirstPendingStep = _ => throw new DataStoreUpgradeRefusedException("No room for a safety copy."),
        };

        var exception = Assert.Throws<DataStoreUpgradeRefusedException>(() => _runner.Run(_connection, steps, options));

        Assert.Equal("No room for a safety copy.", exception.Message);
    }

    [Fact]
    public void ForeignKeysOffStep_WhenForeignKeysCannotBeRestored_ReportsBothProblems()
    {
        var steps = new DataStoreUpgradeStep[]
        {
            new ClosingCodeStep(1, "closes_connection"),
        };

        var exception = Assert.Throws<DataStoreUpgradeFailedException>(() => _runner.Run(_connection, steps, Options()));

        Assert.Contains("Simulated step failure.", exception.Message);
        Assert.Contains("Foreign keys could not be switched back on", exception.Message);
        Assert.Equal("Simulated step failure.", exception.InnerException!.Message);
        Assert.True(exception.InnerException.Data.Contains("ForeignKeysRestoreFailure"));
    }

    [Fact]
    public void AppliedSteps_DescribeThemselvesForTheStartupLog()
    {
        var result = _runner.Run(_connection, TwoSteps(), Options());

        var lines = result.DescribeForLog();

        Assert.Equal(2, lines.Count);
        Assert.Matches(@"^Applied data store upgrade step 0001 \(create_things\) in \d+ ms$", lines[0]);
        Assert.Matches(@"^Applied data store upgrade step 0002 \(add_note\) in \d+ ms$", lines[1]);
    }

    [Fact]
    public void EmptyCatalog_OnANewDataStore_CreatesTheRecordAndAppliesNothing()
    {
        var result = _runner.Run(_connection, [], Options());

        Assert.Equal(0, result.FromNumber);
        Assert.Equal(0, result.ToNumber);
        Assert.Empty(result.Applied);
        Assert.Equal(1, TableCount("data_store_upgrades"));
    }

    [Fact]
    public void SqlAndCodeStepsLoadedFromTheTestAssembly_ApplyTogether()
    {
        var steps = DataStoreUpgradeCatalog.Load(
            typeof(DataStoreUpgradeRunnerTests).Assembly,
            "MediaEngine.Storage.Tests.UpgradeSteps.Valid.",
            "MediaEngine.Storage.Tests.UpgradeCodeSteps");

        var result = _runner.Run(_connection, steps, Options());

        Assert.Equal(3, result.ToNumber);
        Assert.Equal("by code step", Scalar<string>("SELECT note FROM things WHERE id = 1;"));
    }

    private static List<DataStoreUpgradeStep> TwoSteps()
        =>
        [
            Sql(1, "create_things", "CREATE TABLE things (id INTEGER PRIMARY KEY);"),
            Sql(2, "add_note", "ALTER TABLE things ADD COLUMN note TEXT;"),
        ];

    private static SqlDataStoreUpgradeStep Sql(int number, string name, string script)
        => new(number, name, script, $"test:{number:D4}_{name}.sql");

    private static DataStoreUpgradeOptions Options(TimeProvider? clock = null, string engineVersion = "test")
        => new() { Clock = clock ?? TimeProvider.System, EngineVersion = engineVersion };

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

    private int TableCount(string table)
        => (int)Scalar<long>($"SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = '{table}';");

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

    private List<(long Number, string Name, string Fingerprint, string AppliedAt, string Version)> ReadRecords()
    {
        var rows = new List<(long, string, string, string, string)>();
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT number, name, fingerprint, applied_at, engine_version FROM data_store_upgrades ORDER BY number;";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add((reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4)));
        }

        return rows;
    }

    /// <summary>A code step that runs one SQL statement, optionally failing afterwards to prove the rollback.</summary>
    private sealed class RecordingCodeStep(int number, string name, string sql, bool failAfterRunning = false) : DataStoreCodeUpgradeStep
    {
        public override int Number => number;

        public override string Name => name;

        public override void Apply(SqliteConnection connection, SqliteTransaction transaction)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            command.ExecuteNonQuery();
            if (failAfterRunning)
            {
                throw new InvalidOperationException("Simulated code step failure.");
            }
        }
    }

    /// <summary>A foreign-keys-off step that closes the connection and then fails, so nothing can be restored afterwards.</summary>
    private sealed class ClosingCodeStep(int number, string name) : DataStoreCodeUpgradeStep
    {
        public override int Number => number;

        public override string Name => name;

        public override bool RequiresForeignKeysOff => true;

        public override void Apply(SqliteConnection connection, SqliteTransaction transaction)
        {
            connection.Close();
            throw new InvalidOperationException("Simulated step failure.");
        }
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
