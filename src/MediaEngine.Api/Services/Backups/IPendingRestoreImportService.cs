namespace MediaEngine.Api.Services.Backups;

/// <summary>Runs the import of a restored backup once, after the fresh data store has been built (backup and restore spec D3, D7).</summary>
public interface IPendingRestoreImportService
{
    /// <summary>Imports the staged backup if a restore is waiting. Never throws for a failed import; it logs, keeps the staged file and carries on.</summary>
    PendingRestoreImportOutcome ImportIfPending(CancellationToken ct = default);
}
