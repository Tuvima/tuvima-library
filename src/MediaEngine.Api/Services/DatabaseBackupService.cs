using System.IO.Compression;
using System.Text.Json;
using MediaEngine.Api.Services.Vault;
using MediaEngine.Contracts.Setup;
using MediaEngine.Contracts.System;
using MediaEngine.Storage;
using MediaEngine.Storage.Contracts;
using Microsoft.Data.Sqlite;

namespace MediaEngine.Api.Services;

/// <summary>
/// Creates consistent SQLite + non-secret configuration archives and stages a
/// selected archive for atomic application before the next Engine startup.
/// </summary>
public sealed class DatabaseBackupService
{
    private const string PendingRestoreFileName = ".restore-pending.json";
    private const long MaxSetupUploadBytes = 2L * 1024 * 1024 * 1024;
    private const long MaxUncompressedBytes = 16L * 1024 * 1024 * 1024;
    private const int MaxArchiveEntries = 10_000;
    private readonly IDatabaseConnection _database;
    private readonly string _configDirectory;
    private readonly string _backupDirectory;

    public DatabaseBackupService(
        IDatabaseConnection database,
        string configDirectory,
        string? backupDirectory = null)
    {
        _database = database;
        _configDirectory = Path.GetFullPath(configDirectory);
        _backupDirectory = ResolveBackupDirectory(_configDirectory, backupDirectory);
    }

    /// <summary>The folder finished backups are written to (the "local" backup destination).</summary>
    public string BackupDirectory => _backupDirectory;

    public IReadOnlyList<BackupArchiveDto> List()
    {
        if (!Directory.Exists(_backupDirectory))
        {
            return [];
        }

        return Directory.EnumerateFiles(_backupDirectory, "tuvima-backup-*.zip", SearchOption.TopDirectoryOnly)
            .Select(path => new FileInfo(path))
            .OrderByDescending(info => info.CreationTimeUtc)
            .Select(info => new BackupArchiveDto(
                info.Name,
                new DateTimeOffset(info.CreationTimeUtc, TimeSpan.Zero),
                info.Length))
            .ToList();
    }

    public async Task<string> CreateAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(_backupDirectory);
        var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss");
        var archivePath = UniquePath(Path.Combine(_backupDirectory, $"tuvima-backup-{stamp}.zip"));
        var stagingDirectory = Path.Combine(_backupDirectory, $".building-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stagingDirectory);

        try
        {
            var databaseCopy = Path.Combine(stagingDirectory, "library.db");
            await _database.AcquireWriteLockAsync(ct).ConfigureAwait(false);
            try
            {
                ct.ThrowIfCancellationRequested();
                using var source = _database.CreateConnection();
                using var destination = SqliteBackupTarget.Create(databaseCopy);
                source.BackupDatabase(destination);
            }
            finally
            {
                _database.ReleaseWriteLock();
            }

            // The Vault catalogue is removed from the COPY only; the live data store is never touched.
            var vault = VaultBackupScrubber.Scrub(databaseCopy, _configDirectory);

            using var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create);
            archive.CreateEntryFromFile(databaseCopy, "database/library.db", CompressionLevel.SmallestSize);
            foreach (var path in EnumerateSafeConfigFiles())
            {
                ct.ThrowIfCancellationRequested();
                var relative = Path.GetRelativePath(_configDirectory, path).Replace('\\', '/');
                archive.CreateEntryFromFile(path, $"config/{relative}", CompressionLevel.SmallestSize);
            }

            var manifest = archive.CreateEntry("manifest.json", CompressionLevel.SmallestSize);
            BackupManifestWriter.Write(manifest, vault);
            return archivePath;
        }
        catch
        {
            if (File.Exists(archivePath))
            {
                File.Delete(archivePath);
            }

            throw;
        }
        finally
        {
            if (Directory.Exists(stagingDirectory))
            {
                Directory.Delete(stagingDirectory, recursive: true);
            }
        }
    }

    public ScheduleRestoreResultDto ScheduleRestore(string fileName)
    {
        var archivePath = ResolveArchive(fileName);
        var stagingDirectory = Path.Combine(_backupDirectory, $"pending-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stagingDirectory);
        try
        {
            ValidateAndStageArchive(archivePath, stagingDirectory);

            var markerPath = Path.Combine(_configDirectory, PendingRestoreFileName);
            if (File.Exists(markerPath))
            {
                throw new InvalidOperationException("A restore is already scheduled. Restart the Engine before scheduling another restore.");
            }

            File.WriteAllText(markerPath, JsonSerializer.Serialize(new PendingRestore(
                Path.GetFullPath(stagingDirectory),
                Path.GetFileName(archivePath),
                DateTimeOffset.UtcNow)));
            return new ScheduleRestoreResultDto(
                true,
                true,
                "Restore validated and scheduled. Restart the Engine to apply it. The current database and configuration will be retained as pre-restore backups."
                + VaultRestoreNote.For(Path.Combine(stagingDirectory, "library.db")));
        }
        catch
        {
            if (Directory.Exists(stagingDirectory))
            {
                Directory.Delete(stagingDirectory, recursive: true);
            }

            throw;
        }
    }

    public async Task<SetupBackupInspectionDto> UploadAndInspectAsync(
        Stream upload,
        string originalFileName,
        OnboardingRepository onboarding,
        CancellationToken ct)
    {
        if (!originalFileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Library backups must be ZIP archives.");
        }

        Directory.CreateDirectory(_backupDirectory);
        var operationId = Guid.NewGuid();
        var archivePath = Path.Combine(
            _backupDirectory,
            $"tuvima-backup-upload-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{operationId:N}.zip");
        try
        {
            await using (var destination = new FileStream(
                archivePath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var buffer = new byte[128 * 1024];
                long total = 0;
                while (true)
                {
                    var read = await upload.ReadAsync(buffer, ct).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    total += read;
                    if (total > MaxSetupUploadBytes)
                    {
                        throw new InvalidDataException("The backup exceeds the 2 GB setup upload limit.");
                    }

                    await destination.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                }
                await destination.FlushAsync(ct).ConfigureAwait(false);
            }

            var inspection = InspectArchive(operationId, archivePath, originalFileName);
            onboarding.SaveRestoreOperation(
                operationId, archivePath, Path.GetFileName(originalFileName),
                inspection.ManifestVersion, inspection.DatabaseEpoch,
                JsonSerializer.Serialize(inspection));
            return inspection;
        }
        catch
        {
            if (File.Exists(archivePath))
            {
                File.Delete(archivePath);
            }

            throw;
        }
    }

    public RestoreValidationResultDto ValidateRestore(string fileName)
    {
        var archivePath = ResolveArchive(fileName);
        var stagingDirectory = Path.Combine(_backupDirectory, $".restore-drill-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stagingDirectory);
        try
        {
            ValidateAndStageArchive(archivePath, stagingDirectory);
            return new RestoreValidationResultDto(
                true,
                Path.GetFileName(archivePath),
                DateTimeOffset.UtcNow,
                "Restore test passed. The database and non-secret configuration can be staged; no restore was scheduled.");
        }
        finally
        {
            if (Directory.Exists(stagingDirectory))
            {
                Directory.Delete(stagingDirectory, recursive: true);
            }
        }
    }

    public ScheduleRestoreResultDto ConfirmUploadedRestore(Guid operationId, OnboardingRepository onboarding)
    {
        var operation = onboarding.GetRestoreOperation(operationId)
            ?? throw new FileNotFoundException("The inspected backup operation was not found.");
        if (!string.Equals(operation.Status, "inspected", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The backup operation is no longer awaiting confirmation.");
        }

        var fileName = Path.GetFileName(operation.ArchivePath);
        var result = ScheduleRestore(fileName);
        onboarding.MarkRestoreScheduled(operationId);
        return result;
    }

    private static SetupBackupInspectionDto InspectArchive(Guid operationId, string archivePath, string originalFileName)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        if (archive.Entries.Count == 0 || archive.Entries.Count > MaxArchiveEntries)
        {
            throw new InvalidDataException("The backup contains an unsupported number of entries.");
        }

        long uncompressed = 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            var normalized = entry.FullName.Replace('\\', '/');
            if (normalized.StartsWith("/", StringComparison.Ordinal)
                || normalized.Contains("../", StringComparison.Ordinal)
                || !seen.Add(normalized))
            {
                throw new InvalidDataException("The backup contains an unsafe or duplicate entry path.");
            }

            if (IsForbiddenRestoreEntry(normalized))
            {
                throw new InvalidDataException($"The backup contains forbidden host secret material: {normalized}");
            }

            checked { uncompressed += entry.Length; }
            if (uncompressed > MaxUncompressedBytes)
            {
                throw new InvalidDataException("The expanded backup exceeds the 16 GB safety limit.");
            }

            if (entry.CompressedLength > 0 && entry.Length / Math.Max(1, entry.CompressedLength) > 250)
            {
                throw new InvalidDataException("The backup contains an unsafe compression ratio.");
            }
        }

        var manifestEntry = archive.GetEntry("manifest.json")
            ?? throw new InvalidDataException("The backup does not contain manifest.json.");
        var databaseEntry = archive.GetEntry("database/library.db")
            ?? throw new InvalidDataException("The backup does not contain database/library.db.");
        if (manifestEntry.Length > 1024 * 1024)
        {
            throw new InvalidDataException("The backup manifest is unexpectedly large.");
        }

        string manifestVersion;
        string databaseEpoch;
        DateTimeOffset? createdAt = null;
        using (var document = ZipArchiveJson.Parse(manifestEntry))
        {
            var root = document.RootElement;
            manifestVersion = root.TryGetProperty("schema_version", out var version) ? version.GetString() ?? "unknown" : "unknown";
            databaseEpoch = root.TryGetProperty("database_epoch", out var epoch) ? epoch.GetString() ?? "unknown" : "unknown";
            if (root.TryGetProperty("created_at", out var created) && created.TryGetDateTimeOffset(out var parsed))
            {
                createdAt = parsed;
            }

            if (root.TryGetProperty("includes_secrets", out var includesSecrets) && includesSecrets.ValueKind == JsonValueKind.True)
            {
                throw new InvalidDataException("Backups containing secrets cannot be restored during setup.");
            }
        }
        if (!string.Equals(manifestVersion, "1.0", StringComparison.Ordinal)
            && !string.Equals(manifestVersion, "2.0", StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Backup manifest version '{manifestVersion}' is unsupported.");
        }

        if (!string.Equals(databaseEpoch, "guid-blob-v6-shared-library-contributions", StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Backup database epoch '{databaseEpoch}' is unsupported.");
        }

        var verifyPath = Path.Combine(Path.GetDirectoryName(archivePath)!, $".verify-{operationId:N}.db");
        try
        {
            databaseEntry.ExtractToFile(verifyPath);
            SqliteBackupTarget.Verify(verifyPath);
        }
        finally
        {
            if (File.Exists(verifyPath))
            {
                File.Delete(verifyPath);
            }
        }

        var configCount = archive.Entries.Count(entry => entry.FullName.StartsWith("config/", StringComparison.Ordinal));
        return new SetupBackupInspectionDto(
            operationId, Path.GetFileName(originalFileName), createdAt, manifestVersion,
            databaseEpoch, new FileInfo(archivePath).Length, uncompressed, archive.Entries.Count,
            configCount,
            "Provider credentials, active sessions, downloaded models, artwork caches, the Vault catalogue, and original media are not restored.");
    }

    private static bool IsForbiddenRestoreEntry(string relative) =>
        relative.StartsWith("config/secrets/", StringComparison.OrdinalIgnoreCase)
        || relative.StartsWith("config/.secrets/", StringComparison.OrdinalIgnoreCase)
        || relative.StartsWith("config/.keys/", StringComparison.OrdinalIgnoreCase)
        || relative.EndsWith(".restore-pending.json", StringComparison.OrdinalIgnoreCase);

    public string ResolveArchive(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) || !string.Equals(fileName, Path.GetFileName(fileName), StringComparison.Ordinal))
        {
            throw new ArgumentException("Backup file name is invalid.", nameof(fileName));
        }

        var path = SafeChildPath(_backupDirectory, fileName);
        if (!File.Exists(path) || !fileName.StartsWith("tuvima-backup-", StringComparison.Ordinal) || !fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            throw new FileNotFoundException("Backup archive was not found.", fileName);
        }

        return path;
    }

    public static void ApplyPendingRestore(
        string configDirectory,
        string databasePath,
        string? backupDirectory = null)
    {
        var fullConfigDirectory = Path.GetFullPath(configDirectory);
        var markerPath = Path.Combine(fullConfigDirectory, PendingRestoreFileName);
        if (!File.Exists(markerPath))
        {
            return;
        }

        var marker = JsonSerializer.Deserialize<PendingRestore>(File.ReadAllText(markerPath))
            ?? throw new InvalidOperationException("Pending restore marker is invalid.");
        var resolvedBackupDirectory = ResolveBackupDirectory(fullConfigDirectory, backupDirectory);
        var stagedDirectory = SafeChildPath(
            resolvedBackupDirectory,
            Path.GetRelativePath(resolvedBackupDirectory, marker.StagingDirectory));
        var stagedDatabase = Path.Combine(stagedDirectory, "library.db");
        SqliteBackupTarget.Verify(stagedDatabase);

        var targetDatabase = Path.GetFullPath(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(targetDatabase)!);
        DatabaseConnection.ReleasePooledConnections(databasePath);
        var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss");
        if (File.Exists(targetDatabase))
        {
            File.Copy(targetDatabase, $"{targetDatabase}.pre-restore-{stamp}.bak", overwrite: false);
        }

        File.Copy(stagedDatabase, targetDatabase, overwrite: true);
        DeleteIfPresent(targetDatabase + "-wal");
        DeleteIfPresent(targetDatabase + "-shm");

        var stagedConfig = Path.Combine(stagedDirectory, "config");
        if (Directory.Exists(stagedConfig))
        {
            foreach (var source in Directory.EnumerateFiles(stagedConfig, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(stagedConfig, source);
                var destination = SafeChildPath(fullConfigDirectory, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                if (File.Exists(destination))
                {
                    File.Copy(destination, $"{destination}.pre-restore-{stamp}.bak", overwrite: false);
                }

                File.Copy(source, destination, overwrite: true);
            }
        }

        File.Delete(markerPath);
        Directory.Delete(stagedDirectory, recursive: true);
    }

    private IEnumerable<string> EnumerateSafeConfigFiles() =>
        Directory.EnumerateFiles(_configDirectory, "*", SearchOption.AllDirectories)
            .Where(path =>
            {
                var relative = Path.GetRelativePath(_configDirectory, path).Replace('\\', '/');
                return !relative.StartsWith("backups/", StringComparison.OrdinalIgnoreCase)
                    && !relative.StartsWith("secrets/", StringComparison.OrdinalIgnoreCase)
                    && !relative.StartsWith(".secrets/", StringComparison.OrdinalIgnoreCase)
                    && !relative.StartsWith(".keys/", StringComparison.OrdinalIgnoreCase)
                    && !relative.EndsWith(".bak", StringComparison.OrdinalIgnoreCase)
                    && !relative.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(relative, PendingRestoreFileName, StringComparison.OrdinalIgnoreCase);
            });

    private static void ValidateAndStageArchive(string archivePath, string stagingDirectory)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        if (archive.Entries.Count > MaxArchiveEntries)
        {
            throw new InvalidDataException($"The backup contains more than {MaxArchiveEntries:N0} entries.");
        }

        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            var normalized = entry.FullName.Replace('\\', '/');
            if (normalized.StartsWith("/", StringComparison.Ordinal)
                || normalized.Contains("../", StringComparison.Ordinal)
                || !seenPaths.Add(normalized))
            {
                throw new InvalidDataException("The backup contains an unsafe or duplicate entry path.");
            }

            if (IsForbiddenRestoreEntry(normalized))
            {
                throw new InvalidDataException($"The backup contains forbidden host secret material: {normalized}");
            }

            if (entry.CompressedLength > 0 && entry.Length / Math.Max(1, entry.CompressedLength) > 250)
            {
                throw new InvalidDataException("The backup contains an unsafe compression ratio.");
            }
        }

        var uncompressedBytes = archive.Entries.Sum(entry => entry.Length);
        if (uncompressedBytes > MaxUncompressedBytes)
        {
            throw new InvalidDataException("The backup expands beyond the 16 GB restore limit.");
        }

        var databaseEntry = archive.GetEntry("database/library.db")
            ?? throw new InvalidOperationException("The backup does not contain database/library.db.");
        var stagedDatabase = Path.Combine(stagingDirectory, "library.db");
        databaseEntry.ExtractToFile(stagedDatabase);
        SqliteBackupTarget.Verify(stagedDatabase);

        var stagedConfig = Path.Combine(stagingDirectory, "config");
        foreach (var entry in archive.Entries.Where(entry => entry.FullName.StartsWith("config/", StringComparison.Ordinal)))
        {
            if (string.IsNullOrEmpty(entry.Name))
            {
                continue;
            }

            var relative = entry.FullName["config/".Length..].Replace('/', Path.DirectorySeparatorChar);
            var destination = SafeChildPath(stagedConfig, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            entry.ExtractToFile(destination);
        }
    }

    private static string ResolveBackupDirectory(string fullConfigDirectory, string? configuredDirectory) =>
        string.IsNullOrWhiteSpace(configuredDirectory)
            ? Path.Combine(fullConfigDirectory, "backups")
            : Path.GetFullPath(configuredDirectory);

    private static string SafeChildPath(string parent, string relative)
    {
        var fullParent = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var candidate = Path.GetFullPath(Path.Combine(fullParent, relative));
        if (!candidate.StartsWith(fullParent, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Backup path escapes the managed backup directory.");
        }

        return candidate;
    }

    private static string UniquePath(string path)
    {
        if (!File.Exists(path))
        {
            return path;
        }

        return Path.Combine(
            Path.GetDirectoryName(path)!,
            $"{Path.GetFileNameWithoutExtension(path)}-{Guid.NewGuid().ToString("N")[..8]}{Path.GetExtension(path)}");
    }

    private static void DeleteIfPresent(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private sealed record PendingRestore(string StagingDirectory, string BackupFileName, DateTimeOffset ScheduledAt);
}
