using System.Globalization;
using MediaEngine.Storage.Schema;
using MediaEngine.Storage.Schema.Upgrades;
using MediaEngine.TestSupport;
using Microsoft.Data.Sqlite;

namespace MediaEngine.Storage.Tests;

public sealed class DataStoreSafetyCopyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"tuvima_safety_{Guid.NewGuid():N}");
    private readonly string _dbPath;
    private readonly string _backups;

    public DataStoreSafetyCopyTests()
    {
        Directory.CreateDirectory(_root);
        _dbPath = Path.Combine(_root, "library.db");
        _backups = Path.Combine(_root, "backups");
    }

    public void Dispose() => TestTemp.DeleteDirectory(_root);

    [Fact]
    public void PendingStepsOnAnExistingDataStore_WriteOneSafetyCopyFirst()
    {
        CreateStoreAtLatestStep();

        using var database = Open(_backups, [.. ProductSteps(), AddProbeNote(Latest + 1)]);
        database.InitializeSchema();

        var copy = Assert.Single(SafetyCopies());
        Assert.Equal(Path.Combine(_backups, "safety-copies"), Path.GetDirectoryName(copy));
        Assert.Equal([copy], Directory.GetFiles(Path.GetDirectoryName(copy)!));
        Assert.Matches($@"^tuvima-safety-copy-\d{{8}}-\d{{6}}-step-{Latest:D4}-to-{Latest + 1:D4}\.db$", Path.GetFileName(copy));

        // The copy opens as a valid data store at the "from" step, with the data from before the upgrade
        // and without the structure change the upgrade made.
        Assert.Equal((long)Latest, Scalar(copy, "SELECT MAX(number) FROM data_store_upgrades;"));
        Assert.Equal("ok", Text(copy, "PRAGMA quick_check;"));
        Assert.Equal(2L, Scalar(copy, "SELECT COUNT(*) FROM safety_probe;"));
        Assert.Equal(0L, Scalar(copy, "SELECT COUNT(*) FROM pragma_table_info('safety_probe') WHERE name = 'note';"));

        // The live data store did move on.
        Assert.Equal((long)Latest + 1, Scalar(_dbPath, "SELECT MAX(number) FROM data_store_upgrades;"));
        Assert.Equal(1L, Scalar(_dbPath, "SELECT COUNT(*) FROM pragma_table_info('safety_probe') WHERE name = 'note';"));

        Assert.Contains(database.StartupNotes, note => note.Contains(copy, StringComparison.Ordinal));
    }

    [Fact]
    public void BrandNewDataStore_GetsNoSafetyCopy()
    {
        var addTable = new SqlDataStoreUpgradeStep(Latest + 1, "add_table", "CREATE TABLE added_later (id INTEGER PRIMARY KEY);", $"test:{Latest + 1:D4}_add_table.sql");
        using var database = Open(_backups, [.. ProductSteps(), addTable]);
        database.InitializeSchema();

        Assert.False(Directory.Exists(Path.Combine(_backups, "safety-copies")));
        Assert.Empty(SafetyCopies());
        Assert.Equal((long)Latest + 1, Scalar(_dbPath, "SELECT MAX(number) FROM data_store_upgrades;"));
    }

    [Fact]
    public void NoPendingSteps_GetsNoSafetyCopy()
    {
        CreateStoreAtLatestStep();

        using var database = Open(_backups, ProductSteps());
        database.InitializeSchema();

        Assert.False(Directory.Exists(Path.Combine(_backups, "safety-copies")));
        Assert.Empty(database.StartupNotes);
    }

    [Fact]
    public void OnlyTheTwoNewestSafetyCopiesAreKept_AndUserBackupsAreUntouched()
    {
        CreateStoreAtLatestStep();
        var folder = Path.Combine(_backups, "safety-copies");
        Directory.CreateDirectory(folder);
        var oldest = SeedFile(folder, "tuvima-safety-copy-20260101-000000-step-0001-to-0002.db", DateTime.UtcNow.AddDays(-30));
        var older = SeedFile(folder, "tuvima-safety-copy-20260201-000000-step-0001-to-0002.db", DateTime.UtcNow.AddDays(-20));
        var recent = SeedFile(folder, "tuvima-safety-copy-20260301-000000-step-0001-to-0002.db", DateTime.UtcNow.AddDays(-10));
        var stray = SeedFile(folder, "notes.txt", DateTime.UtcNow.AddDays(-40));
        var userBackup = SeedFile(_backups, "tuvima-backup-20260101-000000.zip", DateTime.UtcNow.AddDays(-50));
        var otherDatabase = SeedFile(_backups, "tuvima-safety-copy-in-wrong-folder.db", DateTime.UtcNow.AddDays(-60));

        using var database = Open(_backups, [.. ProductSteps(), AddProbeNote(Latest + 1)]);
        database.InitializeSchema();

        var kept = SafetyCopies().Select(Path.GetFileName).ToList();
        Assert.Equal(2, kept.Count);
        Assert.Contains(Path.GetFileName(recent), kept);
        Assert.DoesNotContain(Path.GetFileName(oldest), kept);
        Assert.DoesNotContain(Path.GetFileName(older), kept);
        Assert.Contains(kept, name => name!.Contains($"step-{Latest:D4}-to-{Latest + 1:D4}", StringComparison.Ordinal) && name != Path.GetFileName(recent));

        Assert.True(File.Exists(stray));
        Assert.True(File.Exists(userBackup));
        Assert.True(File.Exists(otherDatabase));
    }

    [Fact]
    public void NotEnoughDiskSpace_RefusesAndChangesNothing()
    {
        CreateStoreAtLatestStep();

        using var database = Open(_backups, [.. ProductSteps(), AddProbeNote(Latest + 1)], freeSpace: _ => 1_000);
        var refusal = Assert.Throws<DataStoreUpgradeRefusedException>(() => database.InitializeSchema());

        Assert.Contains("Not enough free disk space to make a safety copy", refusal.Message, StringComparison.Ordinal);
        Assert.Contains(_dbPath, refusal.Message, StringComparison.Ordinal);
        Assert.Contains(Path.Combine(_backups, "safety-copies"), refusal.Message, StringComparison.Ordinal);
        Assert.Contains("TUVIMA_BACKUP_DIR", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("Nothing was changed", refusal.Message, StringComparison.Ordinal);
        Assert.Empty(SafetyCopies());
        // The folder is only created once there is room for the copy.
        Assert.False(Directory.Exists(Path.Combine(_backups, "safety-copies")));

        database.Dispose();
        Assert.Equal((long)Latest, Scalar(_dbPath, "SELECT MAX(number) FROM data_store_upgrades;"));
        Assert.Equal(0L, Scalar(_dbPath, "SELECT COUNT(*) FROM pragma_table_info('safety_probe') WHERE name = 'note';"));
    }

    [Fact]
    public void NoBackupFolder_MakesNoSafetyCopy_AndSaysSo()
    {
        CreateStoreAtLatestStep();

        using var database = Open(backupDirectory: null, [.. ProductSteps(), AddProbeNote(Latest + 1)]);
        database.InitializeSchema();

        Assert.Equal((long)Latest + 1, Scalar(_dbPath, "SELECT MAX(number) FROM data_store_upgrades;"));
        Assert.Contains("No backup folder configured, so no safety copy was made.", database.StartupNotes);
        Assert.False(Directory.Exists(_backups));
    }

    [Fact]
    public void FailedStep_MessageNamesTheSafetyCopy()
    {
        CreateStoreAtLatestStep();
        var failing = new SqlDataStoreUpgradeStep(Latest + 1, "break_things", "ALTER TABLE no_such_table ADD COLUMN x TEXT;", $"test:{Latest + 1:D4}_break_things.sql");

        using var database = Open(_backups, [.. ProductSteps(), failing]);
        var failure = Assert.Throws<DataStoreUpgradeFailedException>(() => database.InitializeSchema());

        var copy = Assert.Single(SafetyCopies());
        Assert.Equal(copy, failure.SafetyCopyPath);
        Assert.Contains(copy, failure.Message, StringComparison.Ordinal);
        Assert.Contains($"{Latest + 1:D4} (break_things)", failure.Message, StringComparison.Ordinal);
        Assert.Contains(_dbPath, failure.Message, StringComparison.Ordinal);
        Assert.Equal((long)Latest, Scalar(copy, "SELECT MAX(number) FROM data_store_upgrades;"));
    }

    [Fact]
    public void SafetyCopyFolder_IsKeptApartFromUserBackups()
    {
        CreateStoreAtLatestStep();

        using var database = Open(_backups, [.. ProductSteps(), AddProbeNote(Latest + 1)]);
        database.InitializeSchema();

        // The user-facing backup list reads only tuvima-backup-*.zip directly inside the backups folder
        // (see DatabaseBackupServiceTests.SafetyCopyFolder_IsNotListedAsAUserBackup for the list itself).
        var copy = Assert.Single(SafetyCopies());
        Assert.NotEqual(Path.GetFullPath(_backups), Path.GetDirectoryName(copy));
        Assert.StartsWith("tuvima-safety-copy-", Path.GetFileName(copy), StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(_backups, "tuvima-backup-*.zip", SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public void FinishedCopy_HasItsFinalNameAndLeavesNoPartialFile()
    {
        CreateStoreAtLatestStep();
        using var connection = new SqliteConnection($"Data Source={_dbPath};Pooling=False");
        connection.Open();
        var safetyCopy = new DataStoreSafetyCopy(_dbPath, _backups);
        var now = new DateTimeOffset(2026, 10, 18, 9, 30, 15, TimeSpan.Zero);

        var path = safetyCopy.Create(connection, new DataStoreUpgradePlan(Latest, [AddProbeNote(Latest + 1)], now));

        // The name uses the time the runner supplied, not the machine clock.
        Assert.Equal($"tuvima-safety-copy-20261018-093015-step-{Latest:D4}-to-{Latest + 1:D4}.db", Path.GetFileName(path));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.partial"));
        Assert.Equal("ok", Text(path, "PRAGMA quick_check;"));
    }

    [Fact]
    public void Pruning_OrdersByTheTimestampInTheName_NotByFileTime_AndIgnoresPartialFiles()
    {
        CreateStoreAtLatestStep();
        var folder = Path.Combine(_backups, "safety-copies");
        Directory.CreateDirectory(folder);
        // File times are the reverse of the names: only the name may decide which copies are the newest.
        var newestByName = SeedFile(folder, "tuvima-safety-copy-20260301-000000-step-0001-to-0002.db", DateTime.UtcNow.AddDays(-60));
        var oldestByName = SeedFile(folder, "tuvima-safety-copy-20260101-000000-step-0001-to-0002.db", DateTime.UtcNow.AddDays(-1));
        var partial = SeedFile(folder, "tuvima-safety-copy-20250101-000000-step-0001-to-0002.db.partial", DateTime.UtcNow.AddDays(-90));
        var oddName = SeedFile(folder, "tuvima-safety-copy-by-hand.db", DateTime.UtcNow.AddDays(-90));

        using var connection = new SqliteConnection($"Data Source={_dbPath};Pooling=False");
        connection.Open();
        var path = new DataStoreSafetyCopy(_dbPath, _backups)
            .Create(connection, new DataStoreUpgradePlan(Latest, [AddProbeNote(Latest + 1)], new DateTimeOffset(2026, 10, 18, 0, 0, 0, TimeSpan.Zero)));

        Assert.True(File.Exists(path));
        Assert.True(File.Exists(newestByName));
        Assert.False(File.Exists(oldestByName));
        Assert.True(File.Exists(partial));
        Assert.True(File.Exists(oddName));
    }

    [Fact]
    public void CopyThatCannotBeChecked_IsDeletedAndTheUpgradeRefused()
    {
        CreateStoreAtLatestStep();
        // A store with no record of the step it claims: the check against the plan's "from" step fails.
        using var connection = new SqliteConnection($"Data Source={_dbPath}");
        connection.Open();
        var safetyCopy = new DataStoreSafetyCopy(_dbPath, _backups);

        var plan = new DataStoreUpgradePlan(5, [AddProbeNote(6)], DateTimeOffset.UtcNow);
        var runnerStyleFailure = Assert.Throws<DataStoreUpgradeRefusedException>(() => safetyCopy.Create(connection, plan));

        Assert.Contains("should be at step 5", runnerStyleFailure.Message, StringComparison.Ordinal);
        Assert.Empty(SafetyCopies());
        Assert.Empty(Directory.GetFiles(Path.Combine(_backups, "safety-copies")));
    }

    private DatabaseConnection Open(
        string? backupDirectory,
        IReadOnlyList<DataStoreUpgradeStep> steps,
        Func<string, long>? freeSpace = null)
        => new(
            _dbPath,
            new DataStoreStartupOptions { BackupDirectory = backupDirectory },
            new SqliteConnectionFactory(_dbPath),
            new ReferenceDataTopUp(),
            new DatabaseIntegrityChecker(),
            new SqliteMaintenanceService(),
            steps,
            freeSpace);

    private static IReadOnlyList<DataStoreUpgradeStep> ProductSteps() => DataStoreUpgradeCatalog.ForProduct();

    /// <summary>The newest step this build ships with; a test step that comes after it is numbered <c>Latest + 1</c>.</summary>
    private static int Latest => ProductSteps().Count;

    private static SqlDataStoreUpgradeStep AddProbeNote(int number)
        => new(number, "add_probe_note", "ALTER TABLE safety_probe ADD COLUMN note TEXT;", $"test:{number:D4}_add_probe_note.sql");

    /// <summary>A real data store at the latest product step holding two rows of data a safety copy has to preserve.</summary>
    private void CreateStoreAtLatestStep()
    {
        using (var database = Open(_backups, ProductSteps()))
        {
            database.InitializeSchema();
        }

        using var connection = new SqliteConnection($"Data Source={_dbPath};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE safety_probe (id INTEGER PRIMARY KEY, label TEXT NOT NULL);
            INSERT INTO safety_probe (label) VALUES ('first'), ('second');
            """;
        command.ExecuteNonQuery();
        Assert.False(Directory.Exists(Path.Combine(_backups, "safety-copies")));
    }

    private string[] SafetyCopies()
    {
        var folder = Path.Combine(_backups, "safety-copies");
        return Directory.Exists(folder)
            ? Directory.GetFiles(folder, "tuvima-safety-copy-*.db", SearchOption.TopDirectoryOnly)
            : [];
    }

    private static string SeedFile(string folder, string name, DateTime lastWriteUtc)
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, name);
        File.WriteAllText(path, "seeded");
        File.SetLastWriteTimeUtc(path, lastWriteUtc);
        return path;
    }

    private static long Scalar(string databasePath, string sql)
        => Convert.ToInt64(Run(databasePath, sql), CultureInfo.InvariantCulture);

    private static string Text(string databasePath, string sql)
        => Convert.ToString(Run(databasePath, sql), CultureInfo.InvariantCulture)!;

    private static object Run(string databasePath, string sql)
    {
        using var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly;Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar()!;
    }
}
