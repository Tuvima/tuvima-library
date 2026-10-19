using MediaEngine.Contracts.Settings;
using MediaEngine.Contracts.System;

namespace MediaEngine.Web.Services.Integration;

public sealed partial class EngineApiClient
{
    public async Task<IReadOnlyList<BackupArchiveDto>> GetBackupsAsync(CancellationToken ct = default)
    {
        try
        {
            return await GetAsync<List<BackupArchiveDto>>(
                "GET /system/backups", "/system/backups", static () => [], ct: ct);
        }
        catch (OperationCanceledException) { return []; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GET /system/backups failed");
            return [];
        }
    }

    // Null means the Engine could not be asked (unreachable, refused or failed); the failure is already recorded
    // and logged by GetAsync. A brand-new install answers step 0, which is a real answer and not null.
    public Task<DataStoreVersionDto?> GetDataStoreVersionAsync(CancellationToken ct = default) =>
        GetAsync<DataStoreVersionDto>("GET /system/data-store-version", "/system/data-store-version", ct: ct);

    public Task<SystemResourcesDto?> GetSystemResourcesAsync(CancellationToken ct = default) =>
        GetAsync<SystemResourcesDto>("GET /system/resources", "/system/resources", ct: ct);

    public Task<BackupArchiveDto?> CreateBackupAsync(CancellationToken ct = default) =>
        PostAsync<CreateBackupRequest, BackupArchiveDto>(
            "POST /system/backups", "/system/backups", new CreateBackupRequest(), ct: ct);

    public async Task<byte[]?> DownloadBackupAsync(string fileName, CancellationToken ct = default)
    {
        try
        {
            var encoded = Uri.EscapeDataString(fileName);
            return await _http.GetByteArrayAsync($"/system/backups/{encoded}", ct);
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GET /system/backups/{FileName} failed", fileName);
            return null;
        }
    }

    public Task<ScheduleRestoreResultDto?> ScheduleRestoreAsync(string fileName, CancellationToken ct = default) =>
        PostAsync<ScheduleRestoreRequest, ScheduleRestoreResultDto>(
            "POST /system/backups/restore",
            "/system/backups/restore",
            new ScheduleRestoreRequest(fileName),
            ct: ct);

    public Task<RestoreValidationResultDto?> ValidateRestoreAsync(string fileName, CancellationToken ct = default) =>
        PostAsync<ScheduleRestoreRequest, RestoreValidationResultDto>(
            "POST /system/backups/validate",
            "/system/backups/validate",
            new ScheduleRestoreRequest(fileName),
            ct: ct);
}
