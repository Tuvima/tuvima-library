using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MediaEngine.Contracts.Items;

namespace MediaEngine.Web.Services.Integration;

public sealed partial class EngineApiClient
{
    public Task<ItemMoveOutcome<ItemMoveDestinationsResponse>> FindItemMoveDestinationsAsync(
        Guid workId, string? query, CancellationToken ct = default) =>
        PostItemMoveAsync<ItemMoveDestinationsResponse>(
            $"/library/items/{workId:D}/move-destinations",
            new ItemMoveDestinationsRequest { WorkId = workId, Query = query }, ct);

    public Task<ItemMoveOutcome<ItemMovePreview>> GetItemMovePreviewAsync(
        Guid workId, Guid destinationId, Guid? entryId, string? mode, CancellationToken ct = default) =>
        PostItemMoveAsync<ItemMovePreview>(
            $"/library/items/{workId:D}/move-preview",
            new ItemMovePreviewRequest { WorkId = workId, DestinationId = destinationId, EntryId = entryId, Mode = mode }, ct);

    public Task<ItemMoveOutcome<ItemMoveResultDto>> MoveItemAsync(ItemMoveRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return PostItemMoveAsync<ItemMoveResultDto>($"/library/items/{request.WorkId:D}/move", request, ct);
    }

    private async Task<ItemMoveOutcome<T>> PostItemMoveAsync<T>(string path, object body, CancellationToken ct) where T : class
    {
        var endpoint = "POST " + path;
        try
        {
            using var response = await _http.PostAsJsonAsync(path, body, ct);
            if (response.IsSuccessStatusCode)
            {
                var value = await response.Content.ReadFromJsonAsync<T>(cancellationToken: ct);
                ClearFailure(endpoint);
                return value is null
                    ? ItemMoveOutcome<T>.Failed(ItemMoveOutcomeStatus.Unavailable)
                    : ItemMoveOutcome<T>.Ok(value);
            }

            // A refusal is an expected answer; the Match step shows the Engine's own words for each.
            switch (response.StatusCode)
            {
                case HttpStatusCode.Conflict:
                    var conflict = await response.Content.ReadFromJsonAsync<ItemMoveConflictDto>(cancellationToken: ct);
                    return ItemMoveOutcome<T>.Conflict(conflict?.Preview, conflict?.Message);
                case HttpStatusCode.Forbidden:
                    return ItemMoveOutcome<T>.Failed(ItemMoveOutcomeStatus.Forbidden, (await ReadProblemAsync(response, ct)).Detail);
                case HttpStatusCode.NotFound:
                    return ItemMoveOutcome<T>.Failed(ItemMoveOutcomeStatus.NotFound, (await ReadProblemAsync(response, ct)).Detail);
                case HttpStatusCode.BadRequest:
                    return ItemMoveOutcome<T>.Failed(ItemMoveOutcomeStatus.Refused, (await ReadProblemAsync(response, ct)).Detail);
                default:
                    await RecordHttpFailureAsync(endpoint, response, ct);
                    return ItemMoveOutcome<T>.Failed(ItemMoveOutcomeStatus.Unavailable);
            }
        }
        catch (OperationCanceledException)
        {
            return ItemMoveOutcome<T>.Failed(ItemMoveOutcomeStatus.Unavailable);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or NotSupportedException)
        {
            _logger.LogWarning(ex, "{Endpoint} failed", endpoint);
            RecordExceptionFailure(endpoint, ex);
            return ItemMoveOutcome<T>.Failed(ItemMoveOutcomeStatus.Unavailable);
        }
    }
}
