using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Dapper;
using MediaEngine.Api.Services;
using MediaEngine.Api.Services.Backups;
using MediaEngine.Storage;
using MediaEngine.Storage.Backup;
using MediaEngine.TestSupport;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Api.Tests;

/// <summary>How a restore holds up: a stop at the wrong moment, a failed copy, leftovers of an old store, and the whole round trip for an ordinary title.</summary>
public sealed class BackupRestoreFlowTests : IDisposable
{
    private const string Now = "2026-10-21T12:00:00.0000000+00:00";

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"tuvima-restore-flow-{Guid.NewGuid():N}");

    public BackupRestoreFlowTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task AnImportThatCommittedBeforeAStop_IsNotImportedAgain_AndIsCleanedUp()
    {
        var (config, dbPath) = NewLibrary("committed");
        var maya = Guid.NewGuid();
        using (var database = BackupTestKit.OpenStore(dbPath))
        {
            AddProfile(database, "Maya", maya);
            AddBookmark(database, maya, AddMovie(database, "Zeta Film", "hash-zeta", "tt0000001", "11", 7200), 125.5);
            var service = BackupTestKit.Service(database, config);
            service.ScheduleRestore(Path.GetFileName(await service.CreateAsync(CancellationToken.None)));
        }

        DatabaseBackupService.ApplyPendingRestore(config, dbPath);
        var markerPath = Path.Combine(config, ".restore-import.json");
        var markerJson = File.ReadAllText(markerPath);
        var staged = Directory.GetFiles(Path.Combine(config, "backups"), "backup.zip", SearchOption.AllDirectories).Single();
        var stagedCopy = Path.Combine(_root, "staged-copy.zip");
        File.Copy(staged, stagedCopy);

        using var fresh = BackupTestKit.OpenStore(dbPath);
        Assert.Equal(PendingRestoreImportOutcome.Imported, BackupTestKit.Importer(fresh, config).ImportIfPending());
        var restoreId = JsonDocument.Parse(markerJson).RootElement.GetProperty("RestoreId").GetGuid();
        Assert.NotEqual(Guid.Empty, restoreId);
        Assert.True(new BackupRestoreRepository(fresh).HasRestore(restoreId));
        var waitingBefore = new WaitingPersonalStateRepository(fresh).CountWaiting(includeWithheld: true);

        // The Engine stopped after the import committed but before the clean-up: the marker and the staged file are still there.
        Directory.CreateDirectory(Path.GetDirectoryName(staged)!);
        File.Copy(stagedCopy, staged);
        File.WriteAllText(markerPath, markerJson);
        var log = new BackupTestKit.ListLogger<PendingRestoreImportService>();
        var outcome = BackupTestKit.Importer(fresh, config, logger: log).ImportIfPending();

        Assert.Equal(PendingRestoreImportOutcome.Imported, outcome);
        using var connection = fresh.CreateConnection();
        Assert.Equal(1, connection.ExecuteScalar<int>("SELECT COUNT(*) FROM backup_restores;"));
        Assert.Equal(waitingBefore, new WaitingPersonalStateRepository(fresh).CountWaiting(includeWithheld: true));
        Assert.False(File.Exists(markerPath));
        Assert.False(File.Exists(Path.Combine(config, ".restore-import-failed.json")));
        Assert.False(File.Exists(staged));
        var line = Assert.Single(log.Lines, entry => entry.Level == LogLevel.Information);
        Assert.Contains("already imported", line.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFailureToRemoveTheStagedCopy_AfterTheImportCommitted_IsOnlyAWarning()
    {
        var (config, dbPath) = NewLibrary("cleanup");
        using (var database = BackupTestKit.OpenStore(dbPath))
        {
            AddProfile(database, "Maya");
            var service = BackupTestKit.Service(database, config);
            service.ScheduleRestore(Path.GetFileName(await service.CreateAsync(CancellationToken.None)));
        }

        DatabaseBackupService.ApplyPendingRestore(config, dbPath);
        var log = new BackupTestKit.ListLogger<PendingRestoreImportService>();
        using var fresh = BackupTestKit.OpenStore(dbPath);

        var outcome = BackupTestKit.Importer(
            fresh, config, logger: log, removeStagedFolder: _ => throw new IOException("The folder is in use.")).ImportIfPending();

        Assert.Equal(PendingRestoreImportOutcome.Imported, outcome);
        Assert.False(File.Exists(Path.Combine(config, ".restore-import-failed.json")));
        Assert.DoesNotContain(log.Lines, entry => entry.Level == LogLevel.Error);
        var warning = Assert.Single(log.Lines, entry => entry.Level == LogLevel.Warning);
        Assert.Contains("Restore finished; couldn't remove the staged copy at", warning.Message, StringComparison.Ordinal);
        using var connection = fresh.CreateConnection();
        Assert.Equal(1, connection.ExecuteScalar<int>("SELECT COUNT(*) FROM backup_restores;"));
    }

    [Fact]
    public async Task AFailedImport_WhoseMarkerIsAlreadyGone_DoesNotFailAgain()
    {
        var (config, dbPath) = NewLibrary("nomarker");
        using (var database = BackupTestKit.OpenStore(dbPath))
        {
            var service = BackupTestKit.Service(database, config);
            service.ScheduleRestore(Path.GetFileName(await service.CreateAsync(CancellationToken.None)));
        }

        DatabaseBackupService.ApplyPendingRestore(config, dbPath);
        var markerPath = Path.Combine(config, ".restore-import.json");
        // The staged file is gone, so the import fails; the marker is removed by someone else at the same moment.
        File.Delete(Directory.GetFiles(Path.Combine(config, "backups"), "backup.zip", SearchOption.AllDirectories).Single());
        using var fresh = BackupTestKit.OpenStore(dbPath);
        var log = new BackupTestKit.ListLogger<PendingRestoreImportService>();

        Assert.Equal(PendingRestoreImportOutcome.Failed, BackupTestKit.Importer(fresh, config, logger: log).ImportIfPending());
        Assert.True(File.Exists(Path.Combine(config, ".restore-import-failed.json")));
        Assert.False(File.Exists(markerPath));
        // Renaming a marker that is not there is not an error of its own.
        Assert.Equal(PendingRestoreImportOutcome.NothingPending, BackupTestKit.Importer(fresh, config).ImportIfPending());
    }

    [Fact]
    public async Task AConfigCopyThatFailsAfterTheStoreMoved_PutsTheLibraryBack_AndKeepsThePendingMarker()
    {
        var (config, dbPath) = NewLibrary("undo");
        File.WriteAllText(Path.Combine(config, "a.json"), "backup-a");
        File.WriteAllText(Path.Combine(config, "b.json"), "backup-b");
        using (var database = BackupTestKit.OpenStore(dbPath))
        {
            AddProfile(database, "Maya");
            var service = BackupTestKit.Service(database, config);
            service.ScheduleRestore(Path.GetFileName(await service.CreateAsync(CancellationToken.None)));
        }

        // After scheduling, a.json changes (and is overwritten first) and b.json becomes something the restore cannot write over.
        File.WriteAllText(Path.Combine(config, "a.json"), "changed-a");
        File.Delete(Path.Combine(config, "b.json"));
        Directory.CreateDirectory(Path.Combine(config, "b.json"));

        Assert.ThrowsAny<Exception>(() => DatabaseBackupService.ApplyPendingRestore(config, dbPath));

        Assert.Equal(2, ProfileCount(dbPath));
        Assert.Empty(Directory.GetFiles(_root, "undo.db.pre-restore-*"));
        Assert.DoesNotContain(Directory.GetFiles(_root).Select(Path.GetFileName), name => name!.StartsWith("undo.db.", StringComparison.Ordinal));
        Assert.Equal("changed-a", File.ReadAllText(Path.Combine(config, "a.json")));
        Assert.Empty(Directory.GetFiles(config, "a.json.pre-restore-*"));
        Assert.False(File.Exists(Path.Combine(config, ".restore-import.json")));
        var pending = Path.Combine(config, ".restore-pending.json");
        Assert.True(File.Exists(pending));

        // The marker already says where the Library would have gone, so a stop at any point still tells where it is.
        Assert.Contains("undo.db.pre-restore-", JsonDocument.Parse(File.ReadAllText(pending)).RootElement.GetProperty("PreRestoreCopy").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFailedImport_NamesThePreRestoreCopyFromTheMarker()
    {
        var (config, dbPath) = NewLibrary("namecopy");
        using (var database = BackupTestKit.OpenStore(dbPath))
        {
            AddProfile(database, "Maya");
            var service = BackupTestKit.Service(database, config);
            service.ScheduleRestore(Path.GetFileName(await service.CreateAsync(CancellationToken.None)));
        }

        DatabaseBackupService.ApplyPendingRestore(config, dbPath);
        var copy = Assert.Single(Directory.GetFiles(_root, "namecopy.db.pre-restore-*.bak"));
        var staged = Directory.GetFiles(Path.Combine(config, "backups"), "backup.zip", SearchOption.AllDirectories).Single();
        using (var archive = ZipFile.Open(staged, ZipArchiveMode.Update))
        {
            archive.GetEntry("data/titles.json")!.Delete();
            using var writer = new StreamWriter(archive.CreateEntry("data/titles.json").Open(), new UTF8Encoding(false));
            writer.Write("{ nope");
        }

        var log = new BackupTestKit.ListLogger<PendingRestoreImportService>();
        using var fresh = BackupTestKit.OpenStore(dbPath);
        Assert.Equal(PendingRestoreImportOutcome.Failed, BackupTestKit.Importer(fresh, config, logger: log).ImportIfPending());

        var error = Assert.Single(log.Lines, entry => entry.Level == LogLevel.Error);
        Assert.Contains(copy, error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("there was none", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LeftoverWalAndShmFiles_OfAStoreThatIsGone_AreMovedAside_SoTheyAreNeverReplayedIntoTheNewStore()
    {
        var (config, dbPath) = NewLibrary("orphans");
        using (var database = BackupTestKit.OpenStore(Path.Combine(_root, "other.db")))
        {
            var service = BackupTestKit.Service(database, config);
            service.ScheduleRestore(Path.GetFileName(await service.CreateAsync(CancellationToken.None)));
        }

        // The store file is gone but its log files are not.
        File.WriteAllText(dbPath + "-wal", "old log");
        File.WriteAllText(dbPath + "-shm", "old memory");

        DatabaseBackupService.ApplyPendingRestore(config, dbPath);

        Assert.False(File.Exists(dbPath + "-wal"));
        Assert.False(File.Exists(dbPath + "-shm"));
        Assert.Equal(2, Directory.GetFiles(_root, "orphans.db.orphaned-*").Length);
        using var fresh = BackupTestKit.OpenStore(dbPath);
        Assert.Equal(PendingRestoreImportOutcome.Imported, BackupTestKit.Importer(fresh, config).ImportIfPending());
    }

    [Fact]
    public async Task AnOrdinaryTitle_ComesBackWithItsProgressAndBookmark_WhenItReturnsAsADifferentFileWithTheSameIds()
    {
        var (config, dbPath) = NewLibrary("roundtrip");
        var maya = Guid.NewGuid();
        using (var database = BackupTestKit.OpenStore(dbPath))
        {
            AddProfile(database, "Maya", maya);
            var movie = AddMovie(database, "Dune", "hash-original", "tt1160419", "438631", 9300.5);
            AddProgress(database, maya, movie.Asset, 42.5);
            AddBookmark(database, maya, movie, 120.0);
            var service = BackupTestKit.Service(database, config);
            service.ScheduleRestore(Path.GetFileName(await service.CreateAsync(CancellationToken.None)));
        }

        DatabaseBackupService.ApplyPendingRestore(config, dbPath);
        using var fresh = BackupTestKit.OpenStore(dbPath);
        Assert.Equal(PendingRestoreImportOutcome.Imported, BackupTestKit.Importer(fresh, config).ImportIfPending());
        var waiting = new WaitingPersonalStateRepository(fresh);
        using var connection = fresh.CreateConnection();
        Assert.Equal(0, connection.ExecuteScalar<int>("SELECT COUNT(*) FROM user_states;"));
        Assert.Equal(0, connection.ExecuteScalar<int>("SELECT COUNT(*) FROM audiobook_bookmarks;"));
        Assert.True(waiting.CountWaiting(includeWithheld: false) >= 2);

        // The same film is found again as a re-encoded file: another hash, the same IMDb and TMDB ids and running time.
        var returned = AddMovie(fresh, "Dune (rescanned)", "hash-reencoded", "tt1160419", "438631", 9300.5);
        var pass = waiting.RunPass(DateTimeOffset.UtcNow.AddMinutes(1), 100, 90);

        Assert.Equal(2, pass.Attached);
        Assert.Equal(0, pass.StillWaiting);
        Assert.Equal(42.5, connection.ExecuteScalar<double>("SELECT progress_pct FROM user_states WHERE asset_id = @asset;", new { asset = returned.Asset }));
        Assert.Equal(120.0, connection.ExecuteScalar<double>("SELECT position_seconds FROM audiobook_bookmarks WHERE asset_id = @asset;", new { asset = returned.Asset }));
        var summary = Assert.IsType<RestoreSummary>(waiting.GetLatestRestore());
        Assert.Equal(2, summary.AttachedRows);
    }

    [Fact]
    public async Task AValidManifestThatSaysItHoldsSecrets_IsRefusedOnScheduleAndOnUpload()
    {
        var (config, dbPath) = NewLibrary("secrets");
        using var database = BackupTestKit.OpenStore(dbPath);
        var service = BackupTestKit.Service(database, config);
        var archivePath = await service.CreateAsync(CancellationToken.None);
        using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Update))
        {
            string text;
            using (var reader = new StreamReader(archive.GetEntry("manifest.json")!.Open()))
            {
                text = reader.ReadToEnd();
            }

            archive.GetEntry("manifest.json")!.Delete();
            using var writer = new StreamWriter(archive.CreateEntry("manifest.json").Open(), new UTF8Encoding(false));
            writer.Write(text.Replace("\"includes_secrets\":false", "\"includes_secrets\":true", StringComparison.Ordinal));
        }

        using (var archive = ZipFile.OpenRead(archivePath))
        {
            Assert.Contains("\"includes_secrets\":true", new StreamReader(archive.GetEntry("manifest.json")!.Open()).ReadToEnd(), StringComparison.Ordinal);
            Assert.Equal("3.0", JsonDocument.Parse(archive.GetEntry("manifest.json")!.Open()).RootElement.GetProperty("schema_version").GetString());
        }

        var scheduled = Assert.Throws<InvalidDataException>(() => service.ScheduleRestore(Path.GetFileName(archivePath)));
        Assert.Contains("secrets", scheduled.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(Path.Combine(config, ".restore-pending.json")));
        await using var stream = File.OpenRead(archivePath);
        var uploaded = await Assert.ThrowsAsync<InvalidDataException>(() => service.UploadAndInspectAsync(
            stream, "secrets.zip", new OnboardingRepository(database), CancellationToken.None));
        Assert.Contains("secrets", uploaded.Message, StringComparison.OrdinalIgnoreCase);
    }

    // -- helpers -------------------------------------------------------------------------------------------

    private (string Config, string DbPath) NewLibrary(string name)
    {
        var config = Path.Combine(_root, name + "-config");
        Directory.CreateDirectory(config);
        File.WriteAllText(Path.Combine(config, "core.json"), "{}");
        return (config, Path.Combine(_root, name + ".db"));
    }

    private static void AddProfile(DatabaseConnection database, string name, Guid? id = null)
    {
        using var connection = database.CreateConnection();
        connection.Execute(
            "INSERT INTO profiles(id,display_name,avatar_color,role,created_at) VALUES(@id,@name,'#7C4DFF','Administrator',@now);",
            new { id = id ?? Guid.NewGuid(), name, now = Now });
    }

    private static (Guid Work, Guid Asset) AddMovie(DatabaseConnection database, string title, string hash, string imdb, string tmdb, double seconds)
    {
        var (work, edition, asset) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        using var connection = database.CreateConnection();
        connection.Execute(
            """
            INSERT INTO works (id, media_type, work_kind, curator_state, ready_at) VALUES (@work, 'Movies', 'standalone', 'accepted', @now);
            INSERT INTO editions (id, work_id, format_label) VALUES (@edition, @work, '4K Remux');
            INSERT INTO media_assets (id, edition_id, content_hash, file_path_root, presented_at, library_id, is_orphaned)
                VALUES (@asset, @edition, @hash, '/mnt/movies/film.mkv', CURRENT_TIMESTAMP, 'movies', 0);
            INSERT INTO canonical_values(entity_id, key, value, last_scored_at) VALUES(@work,'title',@title,@now);
            INSERT INTO bridge_ids(id, entity_id, id_type, id_value) VALUES(@bridge1, @work, 'imdb_id', @imdb);
            INSERT INTO bridge_ids(id, entity_id, id_type, id_value) VALUES(@bridge2, @work, 'tmdb_id', @tmdb);
            INSERT INTO playback_inspection_cache(asset_id, source_hash, inspected_at, duration_secs) VALUES(@asset, @hash, @now, @seconds);
            """,
            new { work, edition, asset, hash, title, imdb, tmdb, seconds, bridge1 = Guid.NewGuid(), bridge2 = Guid.NewGuid(), now = Now });
        return (work, asset);
    }

    private static void AddProgress(DatabaseConnection database, Guid profile, Guid asset, double progress)
    {
        using var connection = database.CreateConnection();
        connection.Execute(
            "INSERT INTO user_states(user_id, asset_id, progress_pct, last_accessed) VALUES(@profile,@asset,@progress,@now);",
            new { profile, asset, progress, now = Now });
    }

    private static void AddBookmark(DatabaseConnection database, Guid profile, (Guid Work, Guid Asset) movie, double position)
    {
        using var connection = database.CreateConnection();
        connection.Execute(
            """
            INSERT INTO audiobook_bookmarks(id, profile_id, work_id, asset_id, position_seconds, duration_seconds, label, is_global, created_at)
                VALUES(@id,@profile,@work,@asset,@position,7200,'bookmark',0,@now);
            """,
            new { id = Guid.NewGuid(), profile, work = movie.Work, asset = movie.Asset, position, now = Now });
    }

    private static int ProfileCount(string databasePath)
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={databasePath};Pooling=False;Mode=ReadOnly");
        connection.Open();
        return connection.ExecuteScalar<int>("SELECT COUNT(*) FROM profiles;");
    }

    public void Dispose()
    {
        TestTemp.ReleaseSqlitePoolsIn(_root);
        if (Directory.Exists(_root))
        {
            try { Directory.Delete(_root, recursive: true); }
            catch (IOException) { }
        }
    }
}
