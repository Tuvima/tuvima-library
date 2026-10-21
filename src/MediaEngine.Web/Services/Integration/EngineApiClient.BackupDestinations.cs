using MediaEngine.Contracts.Backups;
using MediaEngine.Contracts.System;

namespace MediaEngine.Web.Services.Integration;

public sealed partial class EngineApiClient
{
    public Task<BackupSettingsResponse?> GetBackupSettingsAsync(CancellationToken ct = default) =>
        GetAsync<BackupSettingsResponse>("GET /backup/settings", "/backup/settings", ct: ct);

    public Task<BackupSettingsResponse?> UpdateBackupSettingsAsync(UpdateBackupSettingsRequest request, CancellationToken ct = default) =>
        PutAsync<UpdateBackupSettingsRequest, BackupSettingsResponse>("PUT /backup/settings", "/backup/settings", request, ct: ct);

    public Task<BackupSettingsResponse?> SetBackupPassphraseAsync(SetBackupPassphraseRequest request, CancellationToken ct = default) =>
        PostAsync<SetBackupPassphraseRequest, BackupSettingsResponse>("POST /backup/passphrase", "/backup/passphrase", request, ct: ct);

    public Task<BackupRunResponse?> RunBackupNowAsync(CancellationToken ct = default) =>
        PostAsync<object, BackupRunResponse>("POST /backup/run", "/backup/run", new { }, ct: ct);

    public Task<BackupDestinationTestResponse?> TestBackupDestinationAsync(string destinationId, CancellationToken ct = default) =>
        PostAsync<object, BackupDestinationTestResponse>("POST /backup/destinations/{id}/test", $"/backup/destinations/{Uri.EscapeDataString(destinationId)}/test", new { }, ct: ct);

    public Task<DestinationBackupsResponse?> GetDestinationBackupsAsync(string destinationId, CancellationToken ct = default) =>
        GetAsync<DestinationBackupsResponse>("GET /backup/destinations/{id}/backups", $"/backup/destinations/{Uri.EscapeDataString(destinationId)}/backups", ct: ct);

    public Task<ScheduleRestoreResultDto?> RestoreFromDestinationAsync(RestoreFromDestinationRequest request, CancellationToken ct = default) =>
        PostAsync<RestoreFromDestinationRequest, ScheduleRestoreResultDto>("POST /backup/restore-from-destination", "/backup/restore-from-destination", request, ct: ct);
}
