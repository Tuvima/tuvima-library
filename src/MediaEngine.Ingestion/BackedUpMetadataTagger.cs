using Microsoft.Extensions.Logging;

namespace MediaEngine.Ingestion;

/// <summary>
/// Shared backup-before-modify template for <see cref="Contracts.IMetadataTagger"/>
/// implementations (<see cref="AudioMetadataTagger"/>, <see cref="VideoMetadataTagger"/>,
/// <see cref="ComicMetadataTagger"/>, <see cref="EpubMetadataTagger"/>).
///
/// Before this base class existed, each tagger hand-rolled its own copy of the
/// backup/mutate/restore flow, including its own private <c>RestoreBackup</c>
/// method. Three of the four used <c>RestoreBackup(string filePath, string backupPath)</c>
/// while one used the inverted <c>RestoreBackup(string backup, string original)</c> —
/// a copy-paste between them could silently restore a file backwards. Centralizing
/// the flow here removes the footgun: there is exactly one restore implementation,
/// and its parameters are named unambiguously.
///
/// Spec: "If a metadata write-back operation fails, the system MUST attempt to
/// restore the file from a temporary backup or mark the asset as Write-Failed."
/// </summary>
public abstract class BackedUpMetadataTagger
{
    /// <summary>
    /// Suffix appended to a file path to form its temporary backup path during a
    /// write-back operation. <c>AutoOrganizeService</c> also references this
    /// literal directly (<c>*.tuvima.bak</c>) to sweep orphaned backups left
    /// behind when a tagger fails partway through a write — that sweep is
    /// unrelated to this class and is left untouched.
    /// </summary>
    public const string BackupSuffix = ".tuvima.bak";

    private readonly ILogger _logger;
    private readonly string _taggerName;

    /// <param name="logger">The concrete tagger's typed logger, used only for the
    /// shared CRITICAL restore-failure message. Callers keep their own typed
    /// logger field for their tagger-specific info/warning/error messages.</param>
    /// <param name="taggerName">Short display name used in the restore-failure
    /// log line (e.g. "AudioTagger"), matching each tagger's existing log prefix.</param>
    protected BackedUpMetadataTagger(ILogger logger, string taggerName)
    {
        _logger = logger;
        _taggerName = taggerName;
    }

    /// <summary>
    /// Synchronous backup/mutate/restore template, for taggers whose underlying
    /// library (TagLibSharp) only exposes synchronous save APIs.
    ///
    /// Copies <paramref name="filePath"/> to its <see cref="BackupSuffix"/> backup,
    /// then runs <paramref name="mutate"/>. If <paramref name="mutate"/> throws,
    /// <paramref name="onFailure"/> is invoked to log the tagger-specific failure
    /// message, the backup is restored over the original file, and the original
    /// exception is rethrown unchanged. Deleting the backup on success is each
    /// caller's own responsibility (some taggers skip it in specific branches,
    /// e.g. an unsupported-file early-out) — this method only guarantees
    /// restore-on-failure.
    /// </summary>
    protected void WithBackup(
        string filePath,
        Action mutate,
        Action<Exception> onFailure)
    {
        var backupPath = filePath + BackupSuffix;
        var backupCreated = false;
        try
        {
            EnsureBackupCapacity(filePath);
            // A prior failed write may have left the only recoverable original
            // at this path. Never overwrite that evidence with a new attempt.
            File.Copy(filePath, backupPath, overwrite: false);
            backupCreated = true;

            mutate();
        }
        catch (Exception ex)
        {
            onFailure(ex);
            if (backupCreated)
                RestoreBackup(sourceBackupPath: backupPath, destinationOriginalPath: filePath);
            throw;
        }
    }

    /// <summary>
    /// Async counterpart of <see cref="WithBackup"/>, for taggers that genuinely
    /// await I/O while patching the file (ZIP-based EPUB/CBZ archives).
    /// Same backup/restore/rethrow contract as <see cref="WithBackup"/>.
    /// </summary>
    protected async Task WithBackupAsync(
        string filePath,
        Func<Task> mutate,
        Action<Exception> onFailure)
    {
        var backupPath = filePath + BackupSuffix;
        var backupCreated = false;
        try
        {
            EnsureBackupCapacity(filePath);
            File.Copy(filePath, backupPath, overwrite: false);
            backupCreated = true;

            await mutate().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            onFailure(ex);
            if (backupCreated)
                RestoreBackup(sourceBackupPath: backupPath, destinationOriginalPath: filePath);
            throw;
        }
    }

    private static void EnsureBackupCapacity(string filePath)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(filePath));
        if (string.IsNullOrWhiteSpace(root)) return;

        try
        {
            var drive = new DriveInfo(root);
            if (!drive.IsReady) return;
            const long reserve = 16L * 1024 * 1024;
            var required = new FileInfo(filePath).Length;
            if (drive.AvailableFreeSpace < required || drive.AvailableFreeSpace - required < reserve)
                throw new IOException("There is not enough free space to make a recoverable media backup.");
        }
        catch (ArgumentException)
        {
            // Some network and virtual filesystems cannot report capacity.
            // File.Copy remains the fail-closed check before mutation begins.
        }
    }

    /// <summary>
    /// Restores <paramref name="destinationOriginalPath"/> from
    /// <paramref name="sourceBackupPath"/> if the backup still exists. This is
    /// the single, unambiguous restore implementation shared by every tagger —
    /// the named parameters make the direction impossible to invert by accident.
    /// The backup file is intentionally left in place afterwards (not deleted);
    /// <c>AutoOrganizeService</c>'s staging sweep is what cleans up backups left
    /// behind by a failed write.
    /// </summary>
    private void RestoreBackup(string sourceBackupPath, string destinationOriginalPath)
    {
        try
        {
            if (File.Exists(sourceBackupPath))
            {
                File.Copy(sourceBackupPath, destinationOriginalPath, overwrite: true);
            }
        }
        catch (Exception restoreEx)
        {
            _logger.LogCritical(restoreEx,
                "{TaggerName}: CRITICAL — backup restore also failed for {Path}",
                _taggerName, destinationOriginalPath);
        }
    }
}
