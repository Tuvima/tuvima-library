using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using MediaEngine.Api.Services.Backups;
using MediaEngine.Contracts.Setup;
using MediaEngine.Contracts.System;
using MediaEngine.Storage;
using MediaEngine.Storage.Backup;

namespace MediaEngine.Api.Services;

/// <summary>
/// Creates backups (the safe configuration files plus the settings and a person's own state, never the catalogue, media files or
/// secrets), checks and stages a selected backup for the next Engine start-up, and applies a staged restore before the data store opens:
/// the current data store is put aside as a pre-restore copy and the start-up builds a fresh one, into which
/// <see cref="PendingRestoreImportService"/> imports the backup.
/// </summary>
public sealed class DatabaseBackupService
{
    private const long MaxSetupUploadBytes = 2L * 1024 * 1024 * 1024;
    private readonly PersonalStateBackupRepository _export;
    private readonly string _configDirectory;
    private readonly string _backupDirectory;

    public DatabaseBackupService(
        PersonalStateBackupRepository export,
        string configDirectory,
        string? backupDirectory = null)
    {
        _export = export;
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

        try
        {
            // The export reads one consistent snapshot of its own, so nothing is locked and the Library keeps working.
            var export = await Task.Run(() => _export.Export(BackupScope.WholeLibrary, ct), ct).ConfigureAwait(false);

            using var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create);
            BackupArchiveFormat.Write(archive, export);
            foreach (var path in EnumerateSafeConfigFiles())
            {
                ct.ThrowIfCancellationRequested();
                var relative = Path.GetRelativePath(_configDirectory, path).Replace('\\', '/');
                archive.CreateEntryFromFile(path, $"config/{relative}", CompressionLevel.SmallestSize);
            }

            var manifest = archive.CreateEntry("manifest.json", CompressionLevel.SmallestSize);
            BackupManifestWriter.Write(manifest, export.Contents);
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
    }

    public ScheduleRestoreResultDto ScheduleRestore(string fileName)
    {
        var archivePath = ResolveArchive(fileName);
        var stagingDirectory = Path.Combine(_backupDirectory, $"pending-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stagingDirectory);
        try
        {
            ValidateAndStageArchive(archivePath, stagingDirectory);

            var markerPath = Path.Combine(_configDirectory, RestoreMarkers.PendingFileName);
            if (File.Exists(markerPath) || File.Exists(Path.Combine(_configDirectory, RestoreMarkers.ImportFileName)))
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
                "Restore checked and scheduled. Restart the Engine to apply it. Your current Library is kept as a pre-restore copy.");
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
                "Restore test passed. The settings and personal state in this backup can be read; no restore was scheduled.");
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
        var info = RestoreArchiveValidator.Validate(archivePath);
        return new SetupBackupInspectionDto(
            operationId, Path.GetFileName(originalFileName), info.CreatedAt, info.ManifestVersion,
            info.DatabaseEpoch, new FileInfo(archivePath).Length, info.UncompressedBytes, info.EntryCount,
            info.ConfigurationFileCount,
            Summarise(info));
    }

    /// <summary>A plain sentence about what the backup holds, from the manifest's counts. It names no title and no profile.</summary>
    internal static string Summarise(RestoreArchiveInfo info)
    {
        var holds = info.Profiles > 0
            ? $"Holds {info.Profiles.ToString("N0", CultureInfo.InvariantCulture)} {(info.Profiles == 1 ? "profile" : "profiles")} and your settings"
            : "Holds your settings";
        var items = info.ItemsOfPersonalState > 0
            ? $", with {info.ItemsOfPersonalState.ToString("N0", CultureInfo.InvariantCulture)} {(info.ItemsOfPersonalState == 1 ? "item" : "items")} of progress, bookmarks and favourites that come back as {(info.ItemsOfPersonalState == 1 ? "its title is" : "their titles are")} found again"
            : string.Empty;
        return $"{holds}{items}. Media files and folders are not included.";
    }

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
        var markerPath = Path.Combine(fullConfigDirectory, RestoreMarkers.PendingFileName);
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
        var stagedArchive = Path.Combine(stagedDirectory, RestoreMarkers.StagedArchiveFileName);
        if (!File.Exists(stagedArchive))
        {
            throw new FileNotFoundException("The staged backup file is missing, so nothing was restored.", stagedArchive);
        }

        // Checked again before anything is moved, so a backup the import would refuse never costs the person their Library.
        RestoreArchiveValidator.Validate(stagedArchive);

        var targetDatabase = Path.GetFullPath(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(targetDatabase)!);
        DatabaseConnection.ReleasePooledConnections(databasePath);
        var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss");

        // The current data store is put aside whole (file, write-ahead log and shared memory), never deleted; the start-up then builds a fresh one.
        var preRestoreCopy = PutDataStoreAside(targetDatabase, stamp);

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

        // The marker is replaced, not deleted, so a stop before the import still imports it at the next start.
        File.WriteAllText(
            Path.Combine(fullConfigDirectory, RestoreMarkers.ImportFileName),
            JsonSerializer.Serialize(new PendingRestoreImport(stagedArchive, marker.BackupFileName, marker.ScheduledAt, preRestoreCopy)));
        File.Delete(markerPath);
    }

    /// <summary>
    /// Moves the data store file and its write-ahead log and shared-memory files to <c>&lt;db&gt;.pre-restore-&lt;stamp&gt;.bak</c> (and the
    /// same name with <c>-wal</c> and <c>-shm</c>). If any move fails the ones already made are undone and the exception is thrown, so
    /// the data store is left exactly as it was. Returns the path of the copy, or null when there was no data store.
    /// </summary>
    private static string? PutDataStoreAside(string targetDatabase, string stamp)
    {
        if (!File.Exists(targetDatabase))
        {
            return null;
        }

        var copy = UniquePath($"{targetDatabase}.pre-restore-{stamp}.bak");
        var moves = new List<(string From, string To)> { (targetDatabase, copy) };
        foreach (var suffix in new[] { "-wal", "-shm" })
        {
            if (File.Exists(targetDatabase + suffix))
            {
                moves.Add((targetDatabase + suffix, copy + suffix));
            }
        }

        var done = new List<(string From, string To)>();
        try
        {
            foreach (var move in moves)
            {
                File.Move(move.From, move.To, overwrite: false);
                done.Add(move);
            }
        }
        catch
        {
            for (var i = done.Count - 1; i >= 0; i--)
            {
                File.Move(done[i].To, done[i].From, overwrite: false);
            }

            throw;
        }

        return copy;
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
                    && !relative.StartsWith(".restore-", StringComparison.OrdinalIgnoreCase);
            });

    /// <summary>
    /// Copies the backup file into the staging folder, checks the copy (so what is checked is what will be imported) and unpacks its
    /// configuration files beside it.
    /// </summary>
    private static RestoreArchiveInfo ValidateAndStageArchive(string archivePath, string stagingDirectory)
    {
        var stagedArchive = Path.Combine(stagingDirectory, RestoreMarkers.StagedArchiveFileName);
        File.Copy(archivePath, stagedArchive, overwrite: false);
        var info = RestoreArchiveValidator.Validate(stagedArchive);

        using var archive = ZipFile.OpenRead(stagedArchive);
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

        return info;
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

}
