using System.Net.Http.Json;
using System.Text.Json;
using MediaEngine.Contracts.Library;

namespace MediaEngine.Web.Services.Integration;

public sealed partial class EngineApiClient
{
    public async Task<LibraryBatchEditPreview?> PreviewBatchEditAsync(LibraryBatchEditRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        const string endpoint = "POST /library/batch-edit/preview";
        try
        {
            using var response = await _http.PostAsJsonAsync("library/batch-edit/preview", request, ct);
            response.EnsureSuccessStatusCode();
            ClearFailure(endpoint);
            return await response.Content.ReadFromJsonAsync<LibraryBatchEditPreview>(cancellationToken: ct);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or NotSupportedException)
        {
            _logger.LogWarning(ex, "{Endpoint} failed", endpoint);
            RecordExceptionFailure(endpoint, ex);
            return null;
        }
    }

    public async Task<LibraryBatchEditResult?> ApplyBatchEditAsync(LibraryBatchEditRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        const string endpoint = "POST /library/batch-edit";
        try
        {
            using var response = await _http.PostAsJsonAsync("library/batch-edit", request, ct);
            response.EnsureSuccessStatusCode();
            ClearFailure(endpoint);
            return await response.Content.ReadFromJsonAsync<LibraryBatchEditResult>(cancellationToken: ct);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or NotSupportedException)
        {
            _logger.LogWarning(ex, "{Endpoint} failed", endpoint);
            RecordExceptionFailure(endpoint, ex);
            return null;
        }
    }
}
