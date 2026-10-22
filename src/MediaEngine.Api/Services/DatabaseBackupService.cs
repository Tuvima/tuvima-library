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
                DateTimeOffset.UtcNow,
                Guid.NewGuid())));
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

        // Where the current data store goes is written into the marker before anything is moved, so a crash at any point still says
        // where the person's Library is. A name already in the marker is reused: an earlier attempt may have moved part of the store.
        var preRestoreCopy = marker.PreRestoreCopy
            ?? (File.Exists(targetDatabase) ? UniquePath($"{targetDatabase}.pre-restore-{stamp}.bak") : null);
        if (preRestoreCopy is not null && !string.Equals(preRestoreCopy, marker.PreRestoreCopy, StringComparison.Ordinal))
        {
            marker = marker with { PreRestoreCopy = preRestoreCopy };
            WriteMarker(markerPath, JsonSerializer.Serialize(marker));
        }

        // The current data store is put aside whole (file, write-ahead log and shared memory), never deleted; the start-up then builds a fresh one.
        // Write-ahead log or shared-memory files left behind by a store that is gone are put aside too, so SQLite never replays them into the new store.
        var aside = preRestoreCopy ?? $"{targetDatabase}.orphaned-{stamp}";
        var moves = PutDataStoreAside(targetDatabase, aside);

        var configCopies = new List<(string Destination, string? Original)>();
        try
        {
            var stagedConfig = Path.Combine(stagedDirectory, "config");
            if (Directory.Exists(stagedConfig))
            {
                foreach (var source in Directory.EnumerateFiles(stagedConfig, "*", SearchOption.AllDirectories).OrderBy(path => path, StringComparer.Ordinal))
                {
                    var relative = Path.GetRelativePath(stagedConfig, source);
                    var destination = SafeChildPath(fullConfigDirectory, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    if (File.Exists(destination))
                    {
                        var original = $"{destination}.pre-restore-{stamp}.bak";
                        File.Copy(destination, original, overwrite: false);
                        configCopies.Add((destination, original));
                        File.Copy(source, destination, overwrite: true);
                    }
                    else
                    {
                        File.Copy(source, destination, overwrite: false);
                        configCopies.Add((destination, null));
                    }
                }
            }

            // The marker is replaced, not deleted, so a stop before the import still imports it at the next start.
            WriteMarker(
                Path.Combine(fullConfigDirectory, RestoreMarkers.ImportFileName),
                JsonSerializer.Serialize(new PendingRestoreImport(stagedArchive, marker.BackupFileName, marker.ScheduledAt, preRestoreCopy, marker.RestoreId)));
        }
        catch
        {
            // Put everything back as it was: the configuration files already replaced, then the data store. The pending marker stays.
            UndoRestore(fullConfigDirectory, configCopies, moves, preRestoreCopy);
            throw;
        }

        File.Delete(markerPath);
    }

    /// <summary>
    /// Moves the data store file and its write-ahead log and shared-memory files to <paramref name="aside"/> (and the same name with
    /// <c>-wal</c> and <c>-shm</c>). A file whose source is missing or whose target already exists is left alone, so running it again after
    /// a stop part-way finishes the job. If a move fails the ones made are undone and the exception is thrown, so the data store is
    /// left exactly as it was. Returns the moves made.
    /// </summary>
    private static List<(string From, string To)> PutDataStoreAside(string targetDatabase, string aside)
    {
        var done = new List<(string From, string To)>();
        try
        {
            foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
            {
                var from = targetDatabase + suffix;
                var to = aside + suffix;
                if (File.Exists(from) && !File.Exists(to))
                {
                    File.Move(from, to, overwrite: false);
                    done.Add((from, to));
                }
            }
        }
        catch
        {
            MoveBack(done);
            throw;
        }

        return done;
    }

    private static void MoveBack(List<(string From, string To)> moves)
    {
        for (var i = moves.Count - 1; i >= 0; i--)
        {
            File.Move(moves[i].To, moves[i].From, overwrite: false);
        }
    }

    /// <summary>
    /// Undoes a restore that failed after the data store was put aside: replaced configuration files go back from their
    /// <c>.pre-restore</c> copies (files the restore created are removed) and the data store moves back. A failure here is reported with
    /// where the Library's data is, so nothing is silently lost.
    /// </summary>
    private static void UndoRestore(
        string configDirectory,
        List<(string Destination, string? Original)> configCopies,
        List<(string From, string To)> moves,
        string? preRestoreCopy)
    {
        try
        {
            for (var i = configCopies.Count - 1; i >= 0; i--)
            {
                var (destination, original) = configCopies[i];
                if (original is null)
                {
                    File.Delete(destination);
                }
                else if (File.Exists(original))
                {
                    File.Move(original, destination, overwrite: true);
                }
            }

            MoveBack(moves);
            File.Delete(Path.Combine(configDirectory, RestoreMarkers.ImportFileName));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new IOException(
                $"The restore could not be undone after it failed. Your previous Library data is at {preRestoreCopy ?? "(there was none)"}, and replaced configuration files are kept beside the originals as .pre-restore copies.",
                ex);
        }
    }

    /// <summary>Writes a marker so a stop in the middle never leaves half of one.</summary>
    private static void WriteMarker(string path, string json)
    {
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, json);
        File.Move(temporary, path, overwrite: true);
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
