using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MediaEngine.Contracts.Library;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Web.Services.Integration;

public sealed partial class EngineApiClient
{
    public async Task<LibraryMoveOutcome<LibraryMoveDestinationsResponse>> GetLibraryMoveDestinationsAsync(
        Guid itemId, string? query, CancellationToken ct = default)
    {
        var endpoint = $"GET /library/items/{itemId:D}/move-destinations";
        var url = $"/library/items/{itemId:D}/move-destinations"
            + (string.IsNullOrWhiteSpace(query) ? string.Empty : $"?query={Uri.EscapeDataString(query.Trim())}");
        try
        {
            using var response = await _http.GetAsync(url, ct);
            return await ReadMoveOutcomeAsync<LibraryMoveDestinationsResponse>(response, endpoint, ct);
        }
        catch (OperationCanceledException)
        {
            return LibraryMoveOutcome<LibraryMoveDestinationsResponse>.Failed(LibraryMoveOutcomeStatus.Unavailable);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or NotSupportedException)
        {
            return MoveFailure<LibraryMoveDestinationsResponse>(endpoint, ex);
        }
    }

    public async Task<LibraryMoveOutcome<LibraryMovePreview>> PreviewLibraryMoveAsync(
        LibraryMovePreviewRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        const string endpoint = "POST /library/items/move-preview";
        try
        {
            using var response = await _http.PostAsJsonAsync("/library/items/move-preview", request, ct);
            return await ReadMoveOutcomeAsync<LibraryMovePreview>(response, endpoint, ct);
        }
        catch (OperationCanceledException)
        {
            return LibraryMoveOutcome<LibraryMovePreview>.Failed(LibraryMoveOutcomeStatus.Unavailable);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or NotSupportedException)
        {
            return MoveFailure<LibraryMovePreview>(endpoint, ex);
        }
    }

    public async Task<LibraryMoveOutcome<LibraryMoveCommitResult>> MoveLibraryItemsAsync(
        LibraryMoveCommitRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        const string endpoint = "POST /library/items/move";
        try
        {
            using var response = await _http.PostAsJsonAsync("/library/items/move", request, ct);
            return await ReadMoveOutcomeAsync<LibraryMoveCommitResult>(response, endpoint, ct);
        }
        catch (OperationCanceledException)
        {
            return LibraryMoveOutcome<LibraryMoveCommitResult>.Failed(LibraryMoveOutcomeStatus.Unavailable);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or NotSupportedException)
        {
            return MoveFailure<LibraryMoveCommitResult>(endpoint, ex);
        }
    }

    private LibraryMoveOutcome<T> MoveFailure<T>(string endpoint, Exception ex)
    {
        _logger.LogWarning(ex, "{Endpoint} failed", endpoint);
        RecordExceptionFailure(endpoint, ex);
        return LibraryMoveOutcome<T>.Failed(LibraryMoveOutcomeStatus.Unavailable);
    }

    // A refusal is an expected answer, not a failure of the Engine; the Match step shows its own words for each.
    private async Task<LibraryMoveOutcome<T>> ReadMoveOutcomeAsync<T>(HttpResponseMessage response, string endpoint, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
        {
            var value = await response.Content.ReadFromJsonAsync<T>(cancellationToken: ct);
            ClearFailure(endpoint);
            return value is null
                ? LibraryMoveOutcome<T>.Failed(LibraryMoveOutcomeStatus.Unavailable)
                : LibraryMoveOutcome<T>.Ok(value);
        }

        switch (response.StatusCode)
        {
            case HttpStatusCode.Conflict:
                return await ReadMoveConflictAsync<T>(response, ct);
            case HttpStatusCode.Forbidden:
                return LibraryMoveOutcome<T>.Failed(LibraryMoveOutcomeStatus.Forbidden, (await ReadProblemAsync(response, ct)).Detail);
            case HttpStatusCode.NotFound:
                return LibraryMoveOutcome<T>.Failed(LibraryMoveOutcomeStatus.NotFound, (await ReadProblemAsync(response, ct)).Detail);
            case HttpStatusCode.BadRequest:
                return LibraryMoveOutcome<T>.Failed(LibraryMoveOutcomeStatus.Invalid, (await ReadProblemAsync(response, ct)).Detail);
            default:
                await RecordHttpFailureAsync(endpoint, response, ct);
                return LibraryMoveOutcome<T>.Failed(LibraryMoveOutcomeStatus.Unavailable);
        }
    }

    // The typed 409 carries the fresh preview; a bare 409 means the review expired or the editing session changed.
    private static async Task<LibraryMoveOutcome<T>> ReadMoveConflictAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct);
        try
        {
            var conflict = JsonSerializer.Deserialize<LibraryMoveConflictDto>(body, JsonSerializerOptions.Web);
            if (conflict is { Preview.PreviewToken.Length: > 0 })
            {
                return LibraryMoveOutcome<T>.Conflict(conflict.Preview, conflict.Message);
            }
        }
        catch (JsonException)
        {
            // Not the typed body: it is the plain "expired" answer below.
        }

        return LibraryMoveOutcome<T>.Failed(LibraryMoveOutcomeStatus.Expired, ReadProblemDetail(body));
    }

    private static string? ReadProblemDetail(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("detail", out var detail)
                && detail.ValueKind == JsonValueKind.String
                    ? detail.GetString()
                    : null;
        }
        catch (JsonException)
        {
            // A refusal without a readable body is still a refusal; the status alone decides what the page shows.
            return null;
        }
    }
}
