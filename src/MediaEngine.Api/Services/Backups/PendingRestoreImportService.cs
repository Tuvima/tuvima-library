using System.IO.Compression;
using System.Text.Json;
using MediaEngine.Storage.Backup;

namespace MediaEngine.Api.Services.Backups;

/// <summary>What the start-up step did with a restore that was waiting to be imported.</summary>
public enum PendingRestoreImportOutcome
{
    /// <summary>No restore was waiting.</summary>
    NothingPending,

    /// <summary>The backup was imported.</summary>
    Imported,

    /// <summary>The import failed; the Engine carries on with the fresh data store.</summary>
    Failed,
}

/// <summary>Runs the import of a restored backup once, after the fresh data store has been built (backup and restore spec D3, D16).</summary>
public interface IPendingRestoreImportService
{
    /// <summary>Imports the staged backup if a restore is waiting. Never throws for a failed import; it logs, keeps the staged file and carries on.</summary>
    PendingRestoreImportOutcome ImportIfPending(CancellationToken ct = default);
}

/// <summary>
/// After <see cref="DatabaseBackupService.ApplyPendingRestore"/> has put the old data store aside, the Engine starts on a fresh one. This
/// reads the staged backup file and puts it into that store (<see cref="BackupRestoreRepository.Import"/>): settings straight back, a
/// person's own state into the waiting area. On success the staged folder and the marker are removed. On failure the marker is renamed so
/// the import is not tried again on every start, and the log names where the previous data store was put aside (never its contents).
/// </summary>
public sealed class PendingRestoreImportService : IPendingRestoreImportService
{
    private readonly BackupRestoreRepository _restore;
    private readonly IBackupPortableValueCodec _codec;
    private readonly string _configDirectory;
    private readonly TimeProvider _clock;
    private readonly ILogger<PendingRestoreImportService> _logger;

    public PendingRestoreImportService(
        BackupRestoreRepository restore,
        IBackupPortableValueCodec codec,
        string configDirectory,
        TimeProvider clock,
        ILogger<PendingRestoreImportService> logger)
    {
        _restore = restore;
        _codec = codec;
        _configDirectory = Path.GetFullPath(configDirectory);
        _clock = clock;
        _logger = logger;
    }

    /// <inheritdoc/>
    public PendingRestoreImportOutcome ImportIfPending(CancellationToken ct = default)
    {
        var markerPath = Path.Combine(_configDirectory, RestoreMarkers.ImportFileName);
        if (!File.Exists(markerPath))
        {
            return PendingRestoreImportOutcome.NothingPending;
        }

        PendingRestoreImport? marker = null;
        try
        {
            marker = JsonSerializer.Deserialize<PendingRestoreImport>(File.ReadAllText(markerPath))
                ?? throw new InvalidDataException("The restore marker is empty.");
            var result = ImportStaged(marker, ct);
            CleanUp(marker, markerPath);
            _logger.LogInformation(
                "Restore imported: {Applied} settings and rows put back, {Waiting} items waiting for their titles, {Dropped} rows left out, {Portable} protected values sealed again.",
                result.AppliedRows,
                result.WaitingRows,
                result.RowsDropped,
                result.PortableValuesRestored);
            return PendingRestoreImportOutcome.Imported;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The kind of error only: the reason can name a title or a profile, which a log never does.
            _logger.LogError(
                "Restore import failed ({Type}). Your previous Library data was put aside at {PreRestoreCopy}; the Engine started on a fresh Library. The staged backup was kept.",
                ex.GetType().Name,
                marker?.PreRestoreCopy ?? "(there was none)");
            KeepMarkerAsFailed(markerPath);
            return PendingRestoreImportOutcome.Failed;
        }
    }

    private BackupRestoreResult ImportStaged(PendingRestoreImport marker, CancellationToken ct)
    {
        if (!File.Exists(marker.StagedArchive))
        {
            throw new FileNotFoundException("The staged backup file is missing.");
        }

        using var archive = ZipFile.OpenRead(marker.StagedArchive);
        var export = BackupArchiveFormat.ReadBackupFile(archive);
        var createdAt = RestoreArchiveValidator.ReadCreatedAt(archive);
        return _restore.Import(export, marker.BackupFileName, createdAt, _codec, _clock.GetUtcNow(), ct);
    }

    private void CleanUp(PendingRestoreImport marker, string markerPath)
    {
        var stagingDirectory = Path.GetDirectoryName(Path.GetFullPath(marker.StagedArchive));
        File.Delete(markerPath);

        // Only a staging folder this feature made is removed.
        if (stagingDirectory is not null
            && string.Equals(Path.GetFileName(marker.StagedArchive), RestoreMarkers.StagedArchiveFileName, StringComparison.Ordinal)
            && Path.GetFileName(stagingDirectory).StartsWith("pending-", StringComparison.Ordinal)
            && Directory.Exists(stagingDirectory))
        {
            Directory.Delete(stagingDirectory, recursive: true);
        }
    }

    private void KeepMarkerAsFailed(string markerPath)
    {
        var failedPath = Path.Combine(_configDirectory, RestoreMarkers.ImportFailedFileName);
        try
        {
            File.Move(markerPath, failedPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Without the rename the import would be tried again on every start; say so, since that is the thing to fix.
            _logger.LogError("The restore marker could not be renamed ({Type}); the import may be tried again at the next start.", ex.GetType().Name);
        }
    }
}
