using System.Net.Http.Json;
using MediaEngine.Contracts.Settings;
using MediaEngine.Contracts.Setup;

namespace MediaEngine.Web.Services.Integration;

public sealed partial class EngineApiClient
{
    public Task<SetupLocaleDto?> GetSetupLocaleAsync(string? setupSession, CancellationToken ct = default) =>
        SetupSendAsync<SetupLocaleDto>(HttpMethod.Get, "/setup/v1/locale", null, setupSession, ct);
    public Task<SetupLocaleDto?> SaveSetupLocaleAsync(SetupLocaleDto locale, string? setupSession, CancellationToken ct = default) =>
        SetupSendAsync<SetupLocaleDto>(HttpMethod.Put, "/setup/v1/locale", JsonContent.Create(locale), setupSession, ct);
    public Task<SetupStatusDto?> GetSetupStatusAsync(CancellationToken ct = default) =>
        SetupSendAsync<SetupStatusDto>(HttpMethod.Get, "/setup/v1/status", null, null, ct);

    public async Task<SetupBeginOutcome> BeginSetupAsync(SetupBeginRequest request, CancellationToken ct = default)
    {
        try
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, "/setup/v1/begin") { Content = JsonContent.Create(request) };
            message.Options.Set(DashboardEngineAuthenticationHandler.SuppressSessionToken, true);
            using var response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.IsSuccessStatusCode)
            {
                return new SetupBeginOutcome(await response.Content.ReadFromJsonAsync<SetupStartResponse>(cancellationToken: ct), null);
            }

            if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                var refusal = await response.Content.ReadFromJsonAsync<SetupBeginRefusalDto>(cancellationToken: ct);
                if (refusal is not null)
                {
                    return new SetupBeginOutcome(null, refusal);
                }
            }

            _logger.LogWarning("Setup request POST /setup/v1/begin failed with {Status}", response.StatusCode);
            return new SetupBeginOutcome(null, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return new SetupBeginOutcome(null, null); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Setup request POST /setup/v1/begin failed");
            return new SetupBeginOutcome(null, null);
        }
    }

    public Task<SetupPreflightDto?> RunSetupPreflightAsync(string? setupSession, CancellationToken ct = default) =>
        SetupSendAsync<SetupPreflightDto>(HttpMethod.Post, "/setup/v1/preflight", JsonContent.Create(new { }), setupSession, ct);

    public Task<SetupAdministratorResponse?> CreateSetupAdministratorAsync(SetupAdministratorRequest request, string setupSession, CancellationToken ct = default) =>
        SetupSendAsync<SetupAdministratorResponse>(HttpMethod.Post, "/setup/v1/administrator", JsonContent.Create(request), setupSession, ct);

    public Task<SetupMediaLocationsDto?> ValidateSetupMediaLocationsAsync(string? setupSession, CancellationToken ct = default) =>
        SetupSendAsync<SetupMediaLocationsDto>(HttpMethod.Post, "/setup/v1/media-locations/validate", JsonContent.Create(new { }), setupSession, ct);

    public Task<LibrariesConfigurationDto?> GetSetupLibrariesAsync(string? setupSession, CancellationToken ct = default) =>
        SetupSendAsync<LibrariesConfigurationDto>(HttpMethod.Get, "/setup/v1/libraries", null, setupSession, ct);

    public Task<LibrariesConfigurationDto?> UpdateSetupLibrariesAsync(UpdateLibrariesRequest request, string? setupSession, CancellationToken ct = default) =>
        SetupSendAsync<LibrariesConfigurationDto>(HttpMethod.Put, "/setup/v1/libraries", JsonContent.Create(request), setupSession, ct);

    public async Task<IReadOnlyList<ServerStorageLocationDto>> GetSetupServerFolderRootsAsync(string? setupSession, CancellationToken ct = default) =>
        await SetupSendAsync<List<ServerStorageLocationDto>>(HttpMethod.Get, "/setup/v1/server-folders/roots", null, setupSession, ct) ?? [];

    public Task<BrowseServerFoldersResultDto?> BrowseSetupServerFoldersAsync(BrowseServerFoldersRequest request, string? setupSession, CancellationToken ct = default) =>
        SetupSendAsync<BrowseServerFoldersResultDto>(HttpMethod.Post, "/setup/v1/server-folders/browse", JsonContent.Create(request), setupSession, ct);

    public Task<ServerFolderValidationResultDto?> ValidateSetupServerFolderAsync(ValidateServerFolderRequest request, string? setupSession, CancellationToken ct = default) =>
        SetupSendAsync<ServerFolderValidationResultDto>(HttpMethod.Post, "/setup/v1/server-folders/validate", JsonContent.Create(request), setupSession, ct);

    public Task<ProviderCredentialOperationResultDto?> TestSetupProviderCredentialsAsync(string name, ProviderCredentialWriteRequest request, string? setupSession, CancellationToken ct = default) =>
        SendSetupProviderCredentialRequestAsync(HttpMethod.Post, name, "credentials/test", request, setupSession, ct);

    public Task<ProviderCredentialOperationResultDto?> SaveSetupProviderCredentialsAsync(string name, ProviderCredentialWriteRequest request, string? setupSession, CancellationToken ct = default) =>
        SendSetupProviderCredentialRequestAsync(HttpMethod.Put, name, "credentials", request, setupSession, ct);

    private async Task<ProviderCredentialOperationResultDto?> SendSetupProviderCredentialRequestAsync(
        HttpMethod method, string name, string suffix, ProviderCredentialWriteRequest request,
        string? setupSession, CancellationToken ct)
    {
        using var message = new HttpRequestMessage(method,
            $"/setup/v1/providers/{Uri.EscapeDataString(name)}/{suffix}")
        {
            Content = JsonContent.Create(request),
        };
        if (!string.IsNullOrWhiteSpace(setupSession))
        {
            message.Headers.TryAddWithoutValidation("X-Tuvima-Setup-Session", setupSession);
            message.Options.Set(DashboardEngineAuthenticationHandler.SuppressSessionToken, true);
        }
        using var response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct);
        return await ReadProviderOperationResultAsync(response, setup: true, ct);
    }

    public Task<SetupStatusDto?> DecideSetupStepAsync(string stepKey, string status, string? detail, string? setupSession, CancellationToken ct = default) =>
        SetupSendAsync<SetupStatusDto>(HttpMethod.Post, $"/setup/v1/steps/{Uri.EscapeDataString(stepKey)}",
            JsonContent.Create(new SetupStepDecisionRequest { Status = status, Detail = detail }), setupSession, ct);

    public async Task<SetupBackupInspectionDto?> UploadSetupBackupAsync(Stream stream, string fileName, string setupSession, CancellationToken ct = default)
    {
        using var content = new MultipartFormDataContent();
        var file = new StreamContent(stream);
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/zip");
        content.Add(file, "backup", fileName);
        return await SetupSendAsync<SetupBackupInspectionDto>(HttpMethod.Post, "/setup/v1/restore/upload", content, setupSession, ct);
    }

    public Task<SetupRestoreConfirmationDto?> ConfirmSetupRestoreAsync(Guid operationId, string setupSession, CancellationToken ct = default) =>
        SetupSendAsync<SetupRestoreConfirmationDto>(HttpMethod.Post, $"/setup/v1/restore/{operationId:D}/confirm", JsonContent.Create(new { }), setupSession, ct);

    public Task<SetupReadinessDto?> GetSetupReadinessAsync(string? setupSession, CancellationToken ct = default) =>
        SetupSendAsync<SetupReadinessDto>(HttpMethod.Get, "/setup/v1/readiness", null, setupSession, ct);

    public Task<SetupStatusDto?> CompleteSetupAsync(string? setupSession, CancellationToken ct = default) =>
        SetupSendAsync<SetupStatusDto>(HttpMethod.Post, "/setup/v1/complete", JsonContent.Create(new { }), setupSession, ct);

    private async Task<T?> SetupSendAsync<T>(HttpMethod method, string path, HttpContent? content, string? setupSession, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(method, path) { Content = content };
            if (!string.IsNullOrWhiteSpace(setupSession))
            {
                request.Headers.TryAddWithoutValidation("X-Tuvima-Setup-Session", setupSession);
                request.Options.Set(DashboardEngineAuthenticationHandler.SuppressSessionToken, true);
            }
            else if (path is "/setup/v1/status" or "/setup/v1/begin")
            {
                request.Options.Set(DashboardEngineAuthenticationHandler.SuppressSessionToken, true);
            }

            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Setup request {Method} {Path} failed with {Status}", method, path, response.StatusCode);
                return default;
            }
            return await response.Content.ReadFromJsonAsync<T>(cancellationToken: ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return default; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Setup request {Method} {Path} failed", method, path);
            return default;
        }
    }
}
