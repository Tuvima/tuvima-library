using MediaEngine.Contracts.Backups;
using MediaEngine.Contracts.System;

namespace MediaEngine.Web.Services.Integration;

public partial interface IEngineApiClient
{
    /// <summary>GET /backup/settings: the schedule, retention, passphrase state and each destination. Null when the Engine could not be asked.</summary>
    Task<BackupSettingsResponse?> GetBackupSettingsAsync(CancellationToken ct = default);

    /// <summary>PUT /backup/settings: saves schedule, retention or destination choices. Null when it could not be saved.</summary>
    Task<BackupSettingsResponse?> UpdateBackupSettingsAsync(UpdateBackupSettingsRequest request, CancellationToken ct = default);

    /// <summary>POST /backup/passphrase: sets or changes the backup passphrase. Null when it was refused.</summary>
    Task<BackupSettingsResponse?> SetBackupPassphraseAsync(SetBackupPassphraseRequest request, CancellationToken ct = default);

    /// <summary>POST /backup/destinations: adds (or, with an id, changes) a destination. Null when it was refused.</summary>
    Task<BackupSettingsResponse?> SaveBackupDestinationAsync(SaveBackupDestinationRequest request, CancellationToken ct = default);

    /// <summary>DELETE /backup/destinations/{id}: stops using a destination that was added. False when it could not be removed.</summary>
    Task<bool> RemoveBackupDestinationAsync(string destinationId, CancellationToken ct = default);

    /// <summary>POST /backup/run: backs up now and copies to every switched-on destination.</summary>
    Task<BackupRunResponse?> RunBackupNowAsync(CancellationToken ct = default);

    /// <summary>POST /backup/destinations/{id}/test: checks a destination can be reached and written to.</summary>
    Task<BackupDestinationTestResponse?> TestBackupDestinationAsync(string destinationId, CancellationToken ct = default);

    /// <summary>GET /backup/destinations/{id}/backups: what a destination holds, newest first.</summary>
    Task<DestinationBackupsResponse?> GetDestinationBackupsAsync(string destinationId, CancellationToken ct = default);

    /// <summary>POST /backup/restore-from-destination: fetches, opens (with the passphrase) and stages a restore.</summary>
    Task<ScheduleRestoreResultDto?> RestoreFromDestinationAsync(RestoreFromDestinationRequest request, CancellationToken ct = default);
}
