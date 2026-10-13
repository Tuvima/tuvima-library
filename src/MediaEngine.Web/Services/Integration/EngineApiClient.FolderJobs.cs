using MediaEngine.Contracts.Settings;

namespace MediaEngine.Web.Services.Integration;

public sealed partial class EngineApiClient
{
    public Task<FolderConversionPreviewResponse?> PreviewFolderConversionAsync(
        FolderConversionPreviewRequest request, CancellationToken ct = default) =>
        PostAsync<FolderConversionPreviewRequest, FolderConversionPreviewResponse>(
            "POST /settings/libraries/folders/conversion/preview",
            "/settings/libraries/folders/conversion/preview",
            request,
            ct: ct);

    public Task<FolderJobStatusDto?> ConvertFolderAsync(string sourceId, CancellationToken ct = default) =>
        PostAsync<FolderConvertRequest, FolderJobStatusDto>(
            "POST /settings/libraries/folders/convert",
            "/settings/libraries/folders/convert",
            new FolderConvertRequest { SourceId = sourceId },
            ct: ct);

    public async Task<IReadOnlyList<FolderJobStatusDto>> GetFolderJobsAsync(CancellationToken ct = default) =>
        await GetAsync<List<FolderJobStatusDto>>(
            "GET /settings/libraries/folders/jobs",
            "/settings/libraries/folders/jobs",
            static () => [],
            logAsWarning: false,
            ct: ct);

    public Task<FolderJobStatusDto?> ControlFolderJobAsync(string operationId, string action, CancellationToken ct = default) =>
        PostAsync<FolderJobControlRequest, FolderJobStatusDto>(
            "POST /settings/libraries/folders/jobs/{id}/control",
            $"/settings/libraries/folders/jobs/{Uri.EscapeDataString(operationId)}/control",
            new FolderJobControlRequest { Action = action },
            ct: ct);
}
