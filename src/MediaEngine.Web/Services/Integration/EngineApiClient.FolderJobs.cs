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

    public Task<FolderMergePreviewResponse?> PreviewFolderMergeAsync(
        FolderMergePreviewRequest request, CancellationToken ct = default) =>
        PostAsync<FolderMergePreviewRequest, FolderMergePreviewResponse>(
            "POST /settings/libraries/folders/merge/preview",
            "/settings/libraries/folders/merge/preview",
            request,
            ct: ct);

    public Task<FolderJobStatusDto?> MergeFolderAsync(FolderMergeRequest request, CancellationToken ct = default) =>
        PostAsync<FolderMergeRequest, FolderJobStatusDto>(
            "POST /settings/libraries/folders/merge",
            "/settings/libraries/folders/merge",
            request,
            ct: ct);

    public Task<FolderKindChangeResult<FolderKindChangePreviewResponse>> PreviewFolderKindChangeAsync(
        string sourceId, FolderKindChangeRequest request, CancellationToken ct = default) =>
        SendFolderKindChangeAsync<FolderKindChangePreviewResponse>(
            "POST /settings/libraries/folders/{id}/change-kind/preview",
            $"/settings/libraries/folders/{Uri.EscapeDataString(sourceId)}/change-kind/preview",
            request,
            ct);

    public Task<FolderKindChangeResult<FolderJobStatusDto>> ChangeFolderKindAsync(
        string sourceId, FolderKindChangeRequest request, CancellationToken ct = default) =>
        SendFolderKindChangeAsync<FolderJobStatusDto>(
            "POST /settings/libraries/folders/{id}/change-kind",
            $"/settings/libraries/folders/{Uri.EscapeDataString(sourceId)}/change-kind",
            request,
            ct);

    // A 400, 404 or 409 is the Engine explaining a refusal in a sentence; anything else is a failure to ask at all.
    private async Task<FolderKindChangeResult<T>> SendFolderKindChangeAsync<T>(
        string endpoint, string path, FolderKindChangeRequest body, CancellationToken ct)
        where T : class
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, path)
            {
                Content = System.Net.Http.Json.JsonContent.Create(body),
            };
            using var response = await _http.SendAsync(request, ct);
            if (response.StatusCode is System.Net.HttpStatusCode.BadRequest or System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.Conflict)
            {
                var sentence = await ReadViewErrorAsync(response, ct);
                ClearFailure(endpoint);
                return new FolderKindChangeResult<T>(null, Refusal: sentence);
            }

            if (!response.IsSuccessStatusCode)
            {
                await RecordHttpFailureAsync(endpoint, response, ct);
                return new FolderKindChangeResult<T>(null, Failure: "Library couldn't ask the Engine about this change.");
            }

            ClearFailure(endpoint);
            var value = await response.Content.ReadFromJsonAsync<T>(cancellationToken: ct);
            return value is null
                ? new FolderKindChangeResult<T>(null, Failure: "The Engine returned nothing.")
                : new FolderKindChangeResult<T>(value);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new FolderKindChangeResult<T>(null, Failure: "The request was canceled.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or NotSupportedException)
        {
            RecordExceptionFailure(endpoint, ex, true);
            return new FolderKindChangeResult<T>(null, Failure: "The Dashboard could not reach the Engine.");
        }
    }

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
