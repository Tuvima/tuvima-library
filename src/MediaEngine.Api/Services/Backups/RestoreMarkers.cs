namespace MediaEngine.Api.Services.Backups;

/// <summary>
/// The marker files a restore leaves in the configuration folder. <c>.restore-pending.json</c> is written when a restore is scheduled;
/// the next start-up swaps it for <c>.restore-import.json</c> once the old data store has been put aside; the import then deletes it, or
/// renames it to <c>.restore-import-failed.json</c> so a failed import is not retried in a loop.
/// </summary>
internal static class RestoreMarkers
{
    public const string PendingFileName = ".restore-pending.json";
    public const string ImportFileName = ".restore-import.json";
    public const string ImportFailedFileName = ".restore-import-failed.json";

    /// <summary>The checked copy of the backup file inside its staging folder.</summary>
    public const string StagedArchiveFileName = "backup.zip";
}

/// <summary>A scheduled restore: the staging folder holding the checked backup file and its configuration files.</summary>
internal sealed record PendingRestore(string StagingDirectory, string BackupFileName, DateTimeOffset ScheduledAt);

/// <summary>A restore whose settings are in place and whose data still has to be imported into the fresh data store.</summary>
/// <param name="StagedArchive">The checked backup file.</param>
/// <param name="BackupFileName">The file name the person chose, recorded with the restore.</param>
/// <param name="ScheduledAt">When the restore was scheduled.</param>
/// <param name="PreRestoreCopy">The previous data store, put aside as a copy; null when there was none.</param>
internal sealed record PendingRestoreImport(string StagedArchive, string BackupFileName, DateTimeOffset ScheduledAt, string? PreRestoreCopy);
