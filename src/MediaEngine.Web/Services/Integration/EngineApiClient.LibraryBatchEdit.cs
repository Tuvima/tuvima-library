using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MediaEngine.Contracts.Library;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Web.Services.Integration;

public sealed partial class EngineApiClient
{
    public async Task<LibraryBatchEditOutcome<LibraryBatchEditPreview>> PreviewLibraryBatchEditAsync(
        LibraryBatchEditPreviewRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        const string endpoint = "POST /library/batch-edit/preview";
        try
        {
            using var response = await _http.PostAsJsonAsync("/library/batch-edit/preview", request, ct);
            return await ReadBatchEditOutcomeAsync<LibraryBatchEditPreview>(response, endpoint, ct);
        }
        catch (OperationCanceledException)
        {
            return LibraryBatchEditOutcome<LibraryBatchEditPreview>.Failed(LibraryBatchEditOutcomeStatus.Unavailable);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or NotSupportedException)
        {
            return BatchEditFailure<LibraryBatchEditPreview>(endpoint, ex);
        }
    }

    public async Task<LibraryBatchEditOutcome<LibraryBatchEditResult>> ApplyLibraryBatchEditAsync(
        LibraryBatchEditRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        const string endpoint = "POST /library/batch-edit";
        try
        {
            using var response = await _http.PostAsJsonAsync("/library/batch-edit", request, ct);
            return await ReadBatchEditOutcomeAsync<LibraryBatchEditResult>(response, endpoint, ct);
        }
        catch (OperationCanceledException)
        {
            return LibraryBatchEditOutcome<LibraryBatchEditResult>.Failed(LibraryBatchEditOutcomeStatus.Unavailable);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or NotSupportedException)
        {
            return BatchEditFailure<LibraryBatchEditResult>(endpoint, ex);
        }
    }

    private LibraryBatchEditOutcome<T> BatchEditFailure<T>(string endpoint, Exception ex)
    {
        _logger.LogWarning(ex, "{Endpoint} failed", endpoint);
        RecordExceptionFailure(endpoint, ex);
        return LibraryBatchEditOutcome<T>.Failed(LibraryBatchEditOutcomeStatus.Unavailable);
    }

    // A refusal is an expected answer, not a failure of the Engine; the batch section shows its own words for each.
    private async Task<LibraryBatchEditOutcome<T>> ReadBatchEditOutcomeAsync<T>(HttpResponseMessage response, string endpoint, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
        {
            var value = await response.Content.ReadFromJsonAsync<T>(cancellationToken: ct);
            ClearFailure(endpoint);
            return value is null
                ? LibraryBatchEditOutcome<T>.Failed(LibraryBatchEditOutcomeStatus.Unavailable)
                : LibraryBatchEditOutcome<T>.Ok(value);
        }

        switch (response.StatusCode)
        {
            case HttpStatusCode.Forbidden:
                return LibraryBatchEditOutcome<T>.Failed(LibraryBatchEditOutcomeStatus.Forbidden, (await ReadProblemAsync(response, ct)).Detail);
            case HttpStatusCode.BadRequest:
                return LibraryBatchEditOutcome<T>.Failed(LibraryBatchEditOutcomeStatus.Invalid, (await ReadProblemAsync(response, ct)).Detail);
            default:
                await RecordHttpFailureAsync(endpoint, response, ct);
                return LibraryBatchEditOutcome<T>.Failed(LibraryBatchEditOutcomeStatus.Unavailable);
        }
    }
}
