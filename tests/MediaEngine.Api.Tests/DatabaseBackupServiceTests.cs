using System.IO.Compression;
using Dapper;
using MediaEngine.Api.Services;
using MediaEngine.Storage;
using Microsoft.Data.Sqlite;

namespace MediaEngine.Api.Tests;

public sealed class DatabaseBackupServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"tuvima-backup-tests-{Guid.NewGuid():N}");

    [Fact]
    public async Task CreateAsync_UsesExplicitBackupDirectory()
    {
        var config = Path.Combine(_root, "config-explicit");
        var backup = Path.Combine(_root, "backup-explicit");
        Directory.CreateDirectory(config);
        await File.WriteAllTextAsync(Path.Combine(config, "core.json"), "{}");
        var dbPath = Path.Combine(_root, "explicit.db");
        using var database = CreateDatabase(dbPath);
        var service = new DatabaseBackupService(database, config, backup);

        var archive = await service.CreateAsync(CancellationToken.None);

        Assert.StartsWith(Path.GetFullPath(backup), archive, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(archive));
    }

    public DatabaseBackupServiceTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task CreateAsync_ProducesConsistentArchiveWithoutSecrets()
    {
        var config = Path.Combine(_root, "config");
        var dbPath = Path.Combine(_root, "library.db");
        Directory.CreateDirectory(Path.Combine(config, "secrets"));
        File.WriteAllText(Path.Combine(config, "core.json"), "{\"server_name\":\"Before\"}");
        File.WriteAllText(Path.Combine(config, "secrets", "tmdb.json"), "{\"api_key\":\"secret\"}");
        File.WriteAllText(Path.Combine(config, "secrets", "tailscale-auth-key"), "tskey-auth-not-for-backups");

        using var database = CreateDatabase(dbPath);
        using (var connection = database.CreateConnection())
        {
            connection.Execute("CREATE TABLE backup_probe(value TEXT NOT NULL);");
            connection.Execute("INSERT INTO backup_probe(value) VALUES ('before');");
        }

        var service = new DatabaseBackupService(database, config);
        var archivePath = await service.CreateAsync(CancellationToken.None);

        using var archive = ZipFile.OpenRead(archivePath);
        Assert.NotNull(archive.GetEntry("database/library.db"));
        Assert.NotNull(archive.GetEntry("config/core.json"));
        Assert.NotNull(archive.GetEntry("manifest.json"));
        Assert.DoesNotContain(archive.Entries, entry => entry.FullName.StartsWith("config/secrets/", StringComparison.Ordinal));
        Assert.DoesNotContain(archive.Entries, entry => entry.FullName.Contains("tailscale", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task CreateAsync_LeavesPluginSecretsOutOfTheArchive_AndRestoreRefusesThem()
    {
        var config = Path.Combine(_root, "plugin-secrets-config");
        var dbPath = Path.Combine(_root, "plugin-secrets.db");
        Directory.CreateDirectory(Path.Combine(config, "secrets", "plugins"));
        File.WriteAllText(Path.Combine(config, "core.json"), "{\"server_name\":\"Before\"}");
        File.WriteAllText(Path.Combine(config, "secrets", "plugins", "x.json"), "{\"api_token\":\"plugin-secret-value\"}");
        using var database = CreateDatabase(dbPath);
        var service = new DatabaseBackupService(database, config);

        var archivePath = await service.CreateAsync(CancellationToken.None);

        using (var archive = ZipFile.OpenRead(archivePath))
        {
            Assert.NotNull(archive.GetEntry("config/core.json"));
            Assert.DoesNotContain(archive.Entries, entry => entry.FullName.Contains("x.json", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(archive.Entries, entry => entry.FullName.Contains("secrets", StringComparison.OrdinalIgnoreCase));
            foreach (var entry in archive.Entries.Where(entry => entry.FullName.StartsWith("config/", StringComparison.Ordinal)))
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
    public async Task SetupUpload_InspectsARealBackupAndRejectsSecretMaterial()
    {
        var config = Path.Combine(_root, "setup-upload-config");
        var backup = Path.Combine(_root, "setup-upload-backups");
        Directory.CreateDirectory(config);
        await File.WriteAllTextAsync(Path.Combine(config, "core.json"), "{}");
        var dbPath = Path.Combine(_root, "setup-upload.db");
        using var database = CreateDatabase(dbPath);
        var onboarding = new OnboardingRepository(database);
        var service = new DatabaseBackupService(database, config, backup);
        var archivePath = await service.CreateAsync(CancellationToken.None);

        await using (var stream = File.OpenRead(archivePath))
        {
            var inspection = await service.UploadAndInspectAsync(
                stream, "recovery.zip", onboarding, CancellationToken.None);
            Assert.Equal("recovery.zip", inspection.FileName);
            Assert.Equal("guid-blob-v6-shared-library-contributions", inspection.DatabaseEpoch);
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
    public async Task ScheduledRestore_ReplacesDatabaseAndConfigOnNextStartup()
    {
        var config = Path.Combine(_root, "restore-config");
        var dbPath = Path.Combine(_root, "restore.db");
        Directory.CreateDirectory(config);
        var corePath = Path.Combine(config, "core.json");
        File.WriteAllText(corePath, "{\"server_name\":\"Before\"}");

        string archiveName;
        using (var database = CreateDatabase(dbPath))
        {
            using (var connection = database.CreateConnection())
            {
                connection.Execute("CREATE TABLE backup_probe(value TEXT NOT NULL);");
                connection.Execute("INSERT INTO backup_probe(value) VALUES ('before');");
            }

            var service = new DatabaseBackupService(database, config);
            var archivePath = await service.CreateAsync(CancellationToken.None);
            archiveName = Path.GetFileName(archivePath);

            using var changed = database.CreateConnection();
            changed.Execute("INSERT INTO backup_probe(value) VALUES ('after');");
            File.WriteAllText(corePath, "{\"server_name\":\"After\"}");
            var result = service.ScheduleRestore(archiveName);
            Assert.True(result.Scheduled);
            Assert.True(result.RestartRequired);
        }

        DatabaseBackupService.ApplyPendingRestore(config, dbPath);

        using var restored = new SqliteConnection($"Data Source={dbPath};Pooling=False");
        restored.Open();
        Assert.Equal(["before"], restored.Query<string>("SELECT value FROM backup_probe ORDER BY rowid;").ToArray());
        Assert.Contains("Before", File.ReadAllText(corePath));
        Assert.NotEmpty(Directory.GetFiles(_root, "restore.db.pre-restore-*.bak"));
    }

    [Fact]
    public async Task ValidateRestore_VerifiesArchiveWithoutSchedulingRestore()
    {
        var config = Path.Combine(_root, "validate-config");
        var dbPath = Path.Combine(_root, "validate.db");
        Directory.CreateDirectory(config);
        File.WriteAllText(Path.Combine(config, "core.json"), "{}");

        using var database = CreateDatabase(dbPath);
        var service = new DatabaseBackupService(database, config);
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
        using var database = CreateDatabase(Path.Combine(_root, "safety.db"));
        var service = new DatabaseBackupService(database, config, backup);
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

    private static DatabaseConnection CreateDatabase(string path)
    {
        var database = new DatabaseConnection(path);
        database.InitializeSchema();
        database.RunStartupChecks();
        return database;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            try { Directory.Delete(_root, recursive: true); }
            catch (IOException) { }
        }
    }
}
