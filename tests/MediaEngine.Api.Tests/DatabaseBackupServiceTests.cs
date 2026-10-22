using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Dapper;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services;
using MediaEngine.Api.Services.Backups;
using MediaEngine.Storage;
using MediaEngine.Storage.Backup;
using MediaEngine.TestSupport;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Api.Tests;

public sealed class DatabaseBackupServiceTests : IDisposable
{
    private const string Now = "2026-10-21T12:00:00.0000000+00:00";
    private const string TwoStepSecret = "JBSWY3DPEHPK3PXP";

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"tuvima-backup-tests-{Guid.NewGuid():N}");

    public DatabaseBackupServiceTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task CreateAsync_UsesExplicitBackupDirectory()
    {
        var config = Path.Combine(_root, "config-explicit");
        var backup = Path.Combine(_root, "backup-explicit");
        Directory.CreateDirectory(config);
        await File.WriteAllTextAsync(Path.Combine(config, "core.json"), "{}");
        using var database = BackupTestKit.OpenStore(Path.Combine(_root, "explicit.db"));
        var service = BackupTestKit.Service(database, config, backup);

        var archive = await service.CreateAsync(CancellationToken.None);

        Assert.StartsWith(Path.GetFullPath(backup), archive, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(archive));
    }

    [Fact]
    public async Task CreateAsync_WritesTheManifestConfigAndDataEntries_AndNoCopyOfTheDataStore()
    {
        var config = Path.Combine(_root, "config");
        Directory.CreateDirectory(Path.Combine(config, "secrets"));
        File.WriteAllText(Path.Combine(config, "core.json"), "{\"server_name\":\"Before\"}");
        File.WriteAllText(Path.Combine(config, "secrets", "tmdb.json"), "{\"api_key\":\"secret\"}");
        File.WriteAllText(Path.Combine(config, "secrets", "tailscale-auth-key"), "tskey-auth-not-for-backups");
        using var database = BackupTestKit.OpenStore(Path.Combine(_root, "library.db"));
        AddProfile(database, "Maya");

        var archivePath = await BackupTestKit.Service(database, config).CreateAsync(CancellationToken.None);

        using var archive = ZipFile.OpenRead(archivePath);
        Assert.Null(archive.GetEntry("database/library.db"));
        Assert.DoesNotContain(archive.Entries, entry => entry.FullName.StartsWith("database/", StringComparison.Ordinal));
        Assert.NotNull(archive.GetEntry("config/core.json"));
        Assert.NotNull(archive.GetEntry("data/contents.json"));
        Assert.NotNull(archive.GetEntry("data/configuration/profiles.json"));
        Assert.DoesNotContain(archive.Entries, entry => entry.FullName.StartsWith("config/secrets/", StringComparison.Ordinal));
        Assert.DoesNotContain(archive.Entries, entry => entry.FullName.Contains("tailscale", StringComparison.OrdinalIgnoreCase));

        using var manifest = JsonDocument.Parse(archive.GetEntry("manifest.json")!.Open());
        var root = manifest.RootElement;
        Assert.Equal("3.0", root.GetProperty("schema_version").GetString());
        Assert.Equal("settings-and-personal-state", root.GetProperty("kind").GetString());
        Assert.False(root.GetProperty("includes_secrets").GetBoolean());
        Assert.False(root.GetProperty("includes_keys").GetBoolean());
        Assert.Equal(
            new[] { "media files", "folders", "artwork", "catalogue", "adult-video catalogue", "secrets" },
            root.GetProperty("leaves_out").EnumerateArray().Select(item => item.GetString()!).ToArray());
        Assert.True(root.GetProperty("profiles").GetInt32() >= 1);
        Assert.True(root.TryGetProperty("created_at", out _));
        foreach (var count in new[] { "configuration_rows", "personal_rows", "correction_rows", "choice_rows", "titles", "titles_recognised_by_file_only" })
        {
            Assert.True(root.TryGetProperty(count, out var value) && value.ValueKind == JsonValueKind.Number, count);
        }

        // The manifest names the data store epoch this build writes, so a backup says which structure it came from.
        using var connection = database.CreateConnection();
        Assert.Equal(
            connection.ExecuteScalar<string>("SELECT value FROM storage_metadata WHERE key = 'storage_epoch';")!,
            root.GetProperty("database_epoch").GetString());
    }

    [Fact]
    public async Task CreateAsync_LeavesPluginSecretsOutOfTheArchive_AndRestoreRefusesThem()
    {
        var config = Path.Combine(_root, "plugin-secrets-config");
        Directory.CreateDirectory(Path.Combine(config, "secrets", "plugins"));
        File.WriteAllText(Path.Combine(config, "core.json"), "{\"server_name\":\"Before\"}");
        File.WriteAllText(Path.Combine(config, "secrets", "plugins", "x.json"), "{\"api_token\":\"plugin-secret-value\"}");
        using var database = BackupTestKit.OpenStore(Path.Combine(_root, "plugin-secrets.db"));
        var service = BackupTestKit.Service(database, config);

        var archivePath = await service.CreateAsync(CancellationToken.None);

        using (var archive = ZipFile.OpenRead(archivePath))
        {
            Assert.NotNull(archive.GetEntry("config/core.json"));
            Assert.DoesNotContain(archive.Entries, entry => entry.FullName.Contains("x.json", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(archive.Entries, entry => entry.FullName.Contains("secrets", StringComparison.OrdinalIgnoreCase));
            foreach (var entry in archive.Entries)
            {
                using var reader = new StreamReader(entry.Open());
                Assert.DoesNotContain("plugin-secret-value", reader.ReadToEnd(), StringComparison.Ordinal);
            }
        }

        var unsafePath = Path.Combine(_root, "unsafe-plugin-secrets.zip");
        using (var archive = ZipFile.Open(unsafePath, ZipArchiveMode.Create))
        {
            archive.CreateEntry("config/secrets/plugins/x.json");
        }

        var onboarding = new OnboardingRepository(database);
        await using var unsafeStream = File.OpenRead(unsafePath);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => service.UploadAndInspectAsync(
            unsafeStream, "unsafe.zip", onboarding, CancellationToken.None));
        Assert.Contains("secret", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SetupUpload_InspectsARealBackupWithAPlainSummary_AndRejectsSecretMaterial()
    {
        var config = Path.Combine(_root, "setup-upload-config");
        var backup = Path.Combine(_root, "setup-upload-backups");
        Directory.CreateDirectory(config);
        await File.WriteAllTextAsync(Path.Combine(config, "core.json"), "{}");
        using var database = BackupTestKit.OpenStore(Path.Combine(_root, "setup-upload.db"));
        AddProfile(database, "Maya");
        AddProfile(database, "Noor");
        var onboarding = new OnboardingRepository(database);
        var service = BackupTestKit.Service(database, config, backup);
        var archivePath = await service.CreateAsync(CancellationToken.None);

        await using (var stream = File.OpenRead(archivePath))
        {
            var inspection = await service.UploadAndInspectAsync(stream, "recovery.zip", onboarding, CancellationToken.None);
            Assert.Equal("recovery.zip", inspection.FileName);
            Assert.Equal("3.0", inspection.ManifestVersion);
            Assert.False(string.IsNullOrWhiteSpace(inspection.DatabaseEpoch) || inspection.DatabaseEpoch == "unknown");
            Assert.True(inspection.ConfigurationFiles >= 1);
            Assert.StartsWith("Holds 3 profiles and your settings", inspection.Warning, StringComparison.Ordinal);
            Assert.EndsWith("Media files and folders are not included.", inspection.Warning, StringComparison.Ordinal);
            Assert.DoesNotContain("Maya", inspection.Warning, StringComparison.Ordinal);
            Assert.Equal("inspected", onboarding.GetRestoreOperation(inspection.OperationId)?.Status);
        }

        var unsafePath = Path.Combine(_root, "unsafe.zip");
        using (var archive = ZipFile.Open(unsafePath, ZipArchiveMode.Create))
        {
            archive.CreateEntry("config/.keys/key.xml");
        }

        await using var unsafeStream = File.OpenRead(unsafePath);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => service.UploadAndInspectAsync(
            unsafeStream, "unsafe.zip", onboarding, CancellationToken.None));
        Assert.Contains("secret", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Summary_SaysWhatTheBackupHoldsInPlainWords_FromTheCountsAlone()
    {
        var many = new RestoreArchiveInfo("3.0", "epoch", null, 3, 1_240, 10, 2, 100);
        var one = new RestoreArchiveInfo("3.0", "epoch", null, 1, 1, 10, 2, 100);
        var none = new RestoreArchiveInfo("3.0", "epoch", null, 0, 0, 10, 2, 100);

        Assert.Equal(
            "Holds 3 profiles and your settings, with 1,240 items of progress, bookmarks and favourites that come back as their titles are found again. Media files and folders are not included.",
            DatabaseBackupService.Summarise(many));
        Assert.Equal(
            "Holds 1 profile and your settings, with 1 item of progress, bookmarks and favourites that come back as its title is found again. Media files and folders are not included.",
            DatabaseBackupService.Summarise(one));
        Assert.Equal("Holds your settings. Media files and folders are not included.", DatabaseBackupService.Summarise(none));
    }

    [Fact]
    public async Task ScheduledRestore_PutsTheCurrentLibraryAside_AndImportsTheBackupIntoAFreshOne()
    {
        var config = Path.Combine(_root, "restore-config");
        var dbPath = Path.Combine(_root, "restore.db");
        Directory.CreateDirectory(config);
        var corePath = Path.Combine(config, "core.json");
        File.WriteAllText(corePath, "{\"server_name\":\"Before\"}");
        var codec = BackupTestKit.Codec();
        var maya = Guid.NewGuid();

        using (var database = BackupTestKit.OpenStore(dbPath))
        {
            AddProfile(database, "Maya", maya);
            var movie = AddVideo(database, "Zeta Film", "hash-zeta");
            AddBookmark(database, maya, movie, 125.5);
            AddTwoStepSecret(database, codec);
            var service = BackupTestKit.Service(database, config, codec: codec);
            var archiveName = Path.GetFileName(await service.CreateAsync(CancellationToken.None));

            // After the backup the Library changes: a second profile, a changed setting.
            AddProfile(database, "Added After");
            File.WriteAllText(corePath, "{\"server_name\":\"After\"}");
            var result = service.ScheduleRestore(archiveName);

            Assert.True(result.Scheduled);
            Assert.True(result.RestartRequired);
            Assert.Equal(
                "Restore checked and scheduled. Restart the Engine to apply it. Your current Library is kept as a pre-restore copy.",
                result.Message);
        }

        DatabaseBackupService.ApplyPendingRestore(config, dbPath);

        // The old data store is put aside whole and the pending marker has become the import marker; nothing is imported yet.
        var preRestoreCopies = Directory.GetFiles(_root, "restore.db.pre-restore-*.bak");
        var copy = Assert.Single(preRestoreCopies);
        Assert.Equal(3, ProfileCount(copy));
        Assert.False(File.Exists(dbPath));
        Assert.False(File.Exists(Path.Combine(config, ".restore-pending.json")));
        Assert.True(File.Exists(Path.Combine(config, ".restore-import.json")));
        Assert.Contains("Before", File.ReadAllText(corePath));
        Assert.NotEmpty(Directory.GetFiles(config, "core.json.pre-restore-*.bak"));

        var log = new BackupTestKit.ListLogger<PendingRestoreImportService>();
        using var fresh = BackupTestKit.OpenStore(dbPath);
        var outcome = BackupTestKit.Importer(fresh, config, codec, log).ImportIfPending();

        Assert.Equal(PendingRestoreImportOutcome.Imported, outcome);
        using var connection = fresh.CreateConnection();
        Assert.Equal(["Maya", "Owner"], connection.Query<string>("SELECT display_name FROM profiles ORDER BY display_name;").ToArray());

        // The bookmark waits for its title; the two-step secret is protected again with this computer's key and still reads back.
        Assert.Equal(0, connection.ExecuteScalar<int>("SELECT COUNT(*) FROM audiobook_bookmarks;"));
        Assert.Equal(1, new WaitingPersonalStateRepository(fresh).CountWaiting(includeWithheld: false));
        var stored = connection.ExecuteScalar<string>("SELECT secret_protected FROM account_two_step;");
        Assert.NotEqual(TwoStepSecret, stored);
        Assert.Equal(TwoStepSecret, codec.Export("account_two_step", "secret_protected", stored!, new Dictionary<string, BackupValue>()));
        Assert.Equal(1, connection.ExecuteScalar<int>("SELECT COUNT(*) FROM backup_restores;"));

        // The marker and the staged folder are gone, and one line with counts was logged.
        Assert.False(File.Exists(Path.Combine(config, ".restore-import.json")));
        Assert.False(File.Exists(Path.Combine(config, ".restore-import-failed.json")));
        Assert.Empty(Directory.GetDirectories(Path.Combine(config, "backups"), "pending-*"));
        var line = Assert.Single(log.Lines, entry => entry.Level == LogLevel.Information);
        Assert.Contains("Restore imported", line.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Zeta", line.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARestoredBookmark_ComesBackWhenTheSameTitleIsInTheLibraryAgain()
    {
        var config = Path.Combine(_root, "reattach-config");
        var dbPath = Path.Combine(_root, "reattach.db");
        Directory.CreateDirectory(config);
        File.WriteAllText(Path.Combine(config, "core.json"), "{}");
        var maya = Guid.NewGuid();
        using (var database = BackupTestKit.OpenStore(dbPath))
        {
            AddProfile(database, "Maya", maya);
            AddBookmark(database, maya, AddVideo(database, "Zeta Film", "hash-zeta"), 125.5);
            var service = BackupTestKit.Service(database, config);
            service.ScheduleRestore(Path.GetFileName(await service.CreateAsync(CancellationToken.None)));
        }

        DatabaseBackupService.ApplyPendingRestore(config, dbPath);
        using var fresh = BackupTestKit.OpenStore(dbPath);
        BackupTestKit.Importer(fresh, config).ImportIfPending();

        AddVideo(fresh, "Zeta Film (rescanned)", "hash-zeta");
        var pass = new WaitingPersonalStateRepository(fresh).RunPass(DateTimeOffset.UtcNow.AddMinutes(1), 100, 90);

        Assert.Equal(1, pass.Attached);
        using var connection = fresh.CreateConnection();
        Assert.Equal(125.5, connection.ExecuteScalar<double>("SELECT position_seconds FROM audiobook_bookmarks;"));
    }

    [Fact]
    public async Task ARestoredWebhook_KeepsItsSecret_ProtectedWithTheNewComputersKey()
    {
        var config = Path.Combine(_root, "webhook-config");
        var dbPath = Path.Combine(_root, "webhook.db");
        Directory.CreateDirectory(config);
        File.WriteAllText(Path.Combine(config, "core.json"), "{}");
        var oldProvider = new EphemeralDataProtectionProvider();
        var newProvider = new EphemeralDataProtectionProvider();
        var webhook = Guid.NewGuid();
        using (var database = BackupTestKit.OpenStore(dbPath))
        {
            var application = Guid.NewGuid();
            using (var connection = database.CreateConnection())
            {
                connection.Execute(
                    """
                    INSERT INTO applications(id, name, application_type, created_at, updated_at) VALUES(@application,'Automation','Automation',@now,@now);
                    INSERT INTO application_webhooks(id, application_id, url, event_types_json, secret_ciphertext, created_at, updated_at)
                        VALUES(@webhook,@application,'https://example.test/hook','[]',@secret,@now,@now);
                    """,
                    new
                    {
                        application,
                        webhook,
                        now = Now,
                        secret = oldProvider.CreateProtector("Tuvima.ApplicationWebhooks.v1", webhook.ToString("D")).Protect("whsec_abc123"),
                    });
            }

            var service = BackupTestKit.Service(database, config, codec: BackupTestKit.Codec(oldProvider));
            var archivePath = await service.CreateAsync(CancellationToken.None);
            service.ScheduleRestore(Path.GetFileName(archivePath));

            // The backup holds the secret readable (nothing in a backup is secret, D15), so the person keeps the file safe.
            using var archive = ZipFile.OpenRead(archivePath);
            Assert.Contains("whsec_abc123", ReadText(archive, "data/configuration/application_webhooks.json"), StringComparison.Ordinal);
        }

        DatabaseBackupService.ApplyPendingRestore(config, dbPath);
        var newComputer = BackupTestKit.Codec(newProvider);
        using var fresh = BackupTestKit.OpenStore(dbPath);
        Assert.Equal(PendingRestoreImportOutcome.Imported, BackupTestKit.Importer(fresh, config, newComputer).ImportIfPending());

        using var restored = fresh.CreateConnection();
        var stored = restored.ExecuteScalar<string>("SELECT secret_ciphertext FROM application_webhooks;")!;
        Assert.NotEqual("whsec_abc123", stored);
        Assert.Equal("whsec_abc123", newProvider.CreateProtector("Tuvima.ApplicationWebhooks.v1", webhook.ToString("D")).Unprotect(stored));
    }

    [Fact]
    public async Task ApplyPendingRestore_WhenTheStagedBackupIsMissing_LeavesTheLibraryAsItWas()
    {
        var config = Path.Combine(_root, "missing-config");
        var dbPath = Path.Combine(_root, "missing.db");
        Directory.CreateDirectory(config);
        File.WriteAllText(Path.Combine(config, "core.json"), "{}");
        using (var database = BackupTestKit.OpenStore(dbPath))
        {
            AddProfile(database, "Maya");
            var service = BackupTestKit.Service(database, config);
            service.ScheduleRestore(Path.GetFileName(await service.CreateAsync(CancellationToken.None)));
        }

        foreach (var staged in Directory.GetFiles(Path.Combine(config, "backups"), "backup.zip", SearchOption.AllDirectories))
        {
            File.Delete(staged);
        }

        Assert.Throws<FileNotFoundException>(() => DatabaseBackupService.ApplyPendingRestore(config, dbPath));

        Assert.True(File.Exists(dbPath));
        Assert.Empty(Directory.GetFiles(_root, "missing.db.pre-restore-*"));
        Assert.True(File.Exists(Path.Combine(config, ".restore-pending.json")));
        Assert.False(File.Exists(Path.Combine(config, ".restore-import.json")));
        Assert.Equal(2, ProfileCount(dbPath));
    }

    [Theory]
    [InlineData("{\"schema_version\":\"1.0\",\"includes_secrets\":false}", true)]
    [InlineData("{\"schema_version\":\"2.0\",\"includes_secrets\":false}", true)]
    [InlineData("{\"schema_version\":\"1.0\"}", false)]
    [InlineData("{\"schema_version\":\"3.0\",\"includes_secrets\":false}", false)]
    public async Task ABackupOfTheOlderLayout_IsRefusedWithOnePlainSentence_OnEveryRouteIn(string manifest, bool withStoreCopy)
    {
        var config = Path.Combine(_root, $"older-{Guid.NewGuid():N}");
        var backup = Path.Combine(config, "backups");
        Directory.CreateDirectory(backup);
        using var database = BackupTestKit.OpenStore(Path.Combine(_root, $"older-{Guid.NewGuid():N}.db"));
        var service = BackupTestKit.Service(database, config);
        var name = "tuvima-backup-20200101-000000.zip";
        using (var archive = ZipFile.Open(Path.Combine(backup, name), ZipArchiveMode.Create))
        {
            WriteEntry(archive, "manifest.json", manifest);
            if (withStoreCopy)
            {
                WriteEntry(archive, "database/library.db", "not a real store");
            }
        }

        var expected = "This backup is from an older version of the Library and can't be restored. Make a new backup with this version.";
        Assert.Equal(expected, Assert.Throws<InvalidDataException>(() => service.ScheduleRestore(name)).Message);
        Assert.Equal(expected, Assert.Throws<InvalidDataException>(() => service.ValidateRestore(name)).Message);
        await using var upload = File.OpenRead(Path.Combine(backup, name));
        Assert.Equal(
            expected,
            (await Assert.ThrowsAsync<InvalidDataException>(() => service.UploadAndInspectAsync(
                upload, "older.zip", new OnboardingRepository(database), CancellationToken.None))).Message);
        Assert.False(File.Exists(Path.Combine(config, ".restore-pending.json")));
        Assert.Empty(Directory.GetDirectories(backup, "pending-*"));
    }

    [Fact]
    public async Task ABackupWithADamagedDataEntry_IsRefusedAsDamaged_AndNothingIsScheduled()
    {
        var config = Path.Combine(_root, "damaged-config");
        Directory.CreateDirectory(config);
        File.WriteAllText(Path.Combine(config, "core.json"), "{}");
        using var database = BackupTestKit.OpenStore(Path.Combine(_root, "damaged.db"));
        AddProfile(database, "Maya");
        var service = BackupTestKit.Service(database, config);
        var archivePath = await service.CreateAsync(CancellationToken.None);
        using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Update))
        {
            archive.GetEntry("data/configuration/profiles.json")!.Delete();
            WriteEntry(archive, "data/configuration/profiles.json", "{ this is not json");
        }

        var name = Path.GetFileName(archivePath);
        const string expected = "This backup file is damaged and can't be restored.";
        Assert.Equal(expected, Assert.Throws<InvalidDataException>(() => service.ScheduleRestore(name)).Message);
        Assert.Equal(expected, Assert.Throws<InvalidDataException>(() => service.ValidateRestore(name)).Message);
        Assert.False(File.Exists(Path.Combine(config, ".restore-pending.json")));
        Assert.Empty(Directory.GetDirectories(Path.GetDirectoryName(archivePath)!, "pending-*"));
    }

    [Fact]
    public void AFileThatIsNotAZip_IsRefusedAsDamaged()
    {
        var config = Path.Combine(_root, "notzip-config");
        var backup = Path.Combine(config, "backups");
        Directory.CreateDirectory(backup);
        File.WriteAllBytes(Path.Combine(backup, "tuvima-backup-20200101-000000.zip"), [1, 2, 3, 4]);
        using var database = BackupTestKit.OpenStore(Path.Combine(_root, "notzip.db"));
        var service = BackupTestKit.Service(database, config);

        var error = Assert.Throws<InvalidDataException>(() => service.ScheduleRestore("tuvima-backup-20200101-000000.zip"));

        Assert.Equal("This backup file is damaged and can't be restored.", error.Message);
    }

    [Fact]
    public async Task AFailedImport_KeepsTheStagedFileUnderAFailedMarker_NamesThePreRestoreCopy_AndLeavesTheEngineStoreUsable()
    {
        var config = Path.Combine(_root, "failed-config");
        var dbPath = Path.Combine(_root, "failed.db");
        Directory.CreateDirectory(config);
        File.WriteAllText(Path.Combine(config, "core.json"), "{}");
        using (var database = BackupTestKit.OpenStore(dbPath))
        {
            AddProfile(database, "Maya");
            var service = BackupTestKit.Service(database, config);
            service.ScheduleRestore(Path.GetFileName(await service.CreateAsync(CancellationToken.None)));
        }

        DatabaseBackupService.ApplyPendingRestore(config, dbPath);

        // The staged file is damaged after it was checked (a disk fault, say): the import fails, not the start-up.
        var staged = Directory.GetFiles(Path.Combine(config, "backups"), "backup.zip", SearchOption.AllDirectories).Single();
        using (var archive = ZipFile.Open(staged, ZipArchiveMode.Update))
        {
            archive.GetEntry("data/titles.json")!.Delete();
            WriteEntry(archive, "data/titles.json", "{ nope");
        }

        var log = new BackupTestKit.ListLogger<PendingRestoreImportService>();
        using var fresh = BackupTestKit.OpenStore(dbPath);
        var outcome = BackupTestKit.Importer(fresh, config, logger: log).ImportIfPending();

        Assert.Equal(PendingRestoreImportOutcome.Failed, outcome);
        Assert.False(File.Exists(Path.Combine(config, ".restore-import.json")));
        Assert.True(File.Exists(Path.Combine(config, ".restore-import-failed.json")));
        Assert.True(File.Exists(staged));
        var error = Assert.Single(log.Lines, entry => entry.Level == LogLevel.Error);
        var copy = Assert.Single(Directory.GetFiles(_root, "failed.db.pre-restore-*.bak"));
        Assert.Contains(copy, error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Maya", error.Message, StringComparison.Ordinal);

        // The Engine carries on with the fresh store, and does not try the same import again.
        using (var connection = fresh.CreateConnection())
        {
            Assert.True(connection.ExecuteScalar<int>("SELECT COUNT(*) FROM profiles;") >= 0);
            Assert.Equal(0, connection.ExecuteScalar<int>("SELECT COUNT(*) FROM backup_restores;"));
        }

        Assert.Equal(PendingRestoreImportOutcome.NothingPending, BackupTestKit.Importer(fresh, config).ImportIfPending());
    }

    [Fact]
    public void ImportIfPending_WithNothingStaged_DoesNothing()
    {
        var config = Path.Combine(_root, "nothing-config");
        Directory.CreateDirectory(config);
        using var database = BackupTestKit.OpenStore(Path.Combine(_root, "nothing.db"));

        Assert.Equal(PendingRestoreImportOutcome.NothingPending, BackupTestKit.Importer(database, config).ImportIfPending());
    }

    [Fact]
    public async Task ValidateRestore_VerifiesArchiveWithoutSchedulingRestore()
    {
        var config = Path.Combine(_root, "validate-config");
        Directory.CreateDirectory(config);
        File.WriteAllText(Path.Combine(config, "core.json"), "{}");
        using var database = BackupTestKit.OpenStore(Path.Combine(_root, "validate.db"));
        var service = BackupTestKit.Service(database, config);
        var archivePath = await service.CreateAsync(CancellationToken.None);

        var result = service.ValidateRestore(Path.GetFileName(archivePath));

        Assert.True(result.Valid);
        Assert.Contains("no restore was scheduled", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(Path.Combine(config, ".restore-pending.json")));
        Assert.Empty(Directory.EnumerateDirectories(Path.GetDirectoryName(archivePath)!, ".restore-drill-*"));
    }

    [Fact]
    public async Task SafetyCopyFolder_IsNotListedAsAUserBackup()
    {
        var config = Path.Combine(_root, "config-safety");
        var backup = Path.Combine(_root, "backup-safety");
        Directory.CreateDirectory(config);
        await File.WriteAllTextAsync(Path.Combine(config, "core.json"), "{}");
        using var database = BackupTestKit.OpenStore(Path.Combine(_root, "safety.db"));
        var service = BackupTestKit.Service(database, config, backup);
        var userBackup = await service.CreateAsync(CancellationToken.None);

        // The Engine writes upgrade safety copies here (MediaEngine.Storage DataStoreSafetyCopy).
        var safetyFolder = Path.Combine(backup, "safety-copies");
        Directory.CreateDirectory(safetyFolder);
        var safetyCopy = Path.Combine(safetyFolder, "tuvima-safety-copy-20260101-000000-step-0001-to-0002.db");
        await File.WriteAllTextAsync(safetyCopy, "copy");

        var listed = service.List();

        Assert.Equal([Path.GetFileName(userBackup)], listed.Select(item => item.FileName));
        Assert.ThrowsAny<Exception>(() => service.ResolveArchive(Path.GetFileName(safetyCopy)));
        Assert.ThrowsAny<Exception>(() => service.ResolveArchive(Path.Combine("safety-copies", Path.GetFileName(safetyCopy))));
    }

    // -- helpers -------------------------------------------------------------------------------------------

    private static string ReadText(ZipArchive archive, string name)
    {
        using var reader = new StreamReader(archive.GetEntry(name)!.Open());
        return reader.ReadToEnd();
    }

    private static void WriteEntry(ZipArchive archive, string name, string text)
    {
        var entry = archive.CreateEntry(name);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(text);
    }

    private static void AddProfile(DatabaseConnection database, string name, Guid? id = null)
    {
        using var connection = database.CreateConnection();
        connection.Execute(
            "INSERT INTO profiles(id,display_name,avatar_color,role,content_limit,created_at) VALUES(@id,@name,'#7C4DFF','Administrator',NULL,@now);",
            new { id = id ?? Guid.NewGuid(), name, now = Now });
    }

    private static (Guid Work, Guid Asset) AddVideo(DatabaseConnection database, string title, string hash)
    {
        var (work, edition, asset) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        using var connection = database.CreateConnection();
        connection.Execute(
            """
            INSERT INTO works (id, media_type, work_kind, curator_state, ready_at) VALUES (@work, 'Videos', 'standalone', 'accepted', @now);
            INSERT INTO editions (id, work_id) VALUES (@edition, @work);
            INSERT INTO media_assets (id, edition_id, content_hash, file_path_root, presented_at, library_id, is_orphaned)
                VALUES (@asset, @edition, @hash, '/mnt/movies/film.mkv', CURRENT_TIMESTAMP, 'movies', 0);
            INSERT INTO canonical_values(entity_id, key, value, last_scored_at) VALUES(@work,'title',@title,@now);
            """,
            new { work, edition, asset, hash, title, now = Now });
        return (work, asset);
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

    private static void AddTwoStepSecret(DatabaseConnection database, DataProtectionBackupPortableValueCodec codec)
    {
        var account = Guid.NewGuid();
        // The real protector that seals the secret in the data store is the one the codec wraps.
        var sealedSecret = codec.Import("account_two_step", "secret_protected", TwoStepSecret, new Dictionary<string, BackupValue>());
        using var connection = database.CreateConnection();
        connection.Execute(
            """
            INSERT INTO accounts(id, created_at, updated_at) VALUES(@account,@now,@now);
            INSERT INTO account_two_step(account_id, secret_protected, created_at) VALUES(@account,@secret,@now);
            """,
            new { account, secret = sealedSecret, now = Now });
    }

    private static int ProfileCount(string databasePath)
    {
        using var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False;Mode=ReadOnly");
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
