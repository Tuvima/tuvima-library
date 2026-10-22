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
/// <param name="StagingDirectory">The staging folder.</param>
/// <param name="BackupFileName">The file name the person chose, recorded with the restore.</param>
/// <param name="ScheduledAt">When the restore was scheduled.</param>
/// <param name="RestoreId">The id the restore is recorded under once imported, so an import that committed before a crash is recognised.</param>
/// <param name="PreRestoreCopy">Where the current data store will be put aside; written before anything is moved. Null while it is not yet decided or when there is no data store.</param>
internal sealed record PendingRestore(string StagingDirectory, string BackupFileName, DateTimeOffset ScheduledAt, Guid RestoreId = default, string? PreRestoreCopy = null);

/// <summary>A restore whose settings are in place and whose data still has to be imported into the fresh data store.</summary>
/// <param name="StagedArchive">The checked backup file.</param>
/// <param name="BackupFileName">The file name the person chose, recorded with the restore.</param>
/// <param name="ScheduledAt">When the restore was scheduled.</param>
/// <param name="PreRestoreCopy">The previous data store, put aside as a copy; null when there was none.</param>
/// <param name="RestoreId">The id the restore is recorded under; when it already exists in the data store the import committed earlier.</param>
internal sealed record PendingRestoreImport(string StagedArchive, string BackupFileName, DateTimeOffset ScheduledAt, string? PreRestoreCopy, Guid RestoreId = default);
