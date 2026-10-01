using System.Net;
using System.Net.Http.Json;
using MediaEngine.Contracts.Metadata;

namespace MediaEngine.Web.Services.Integration;

public sealed partial class EngineApiClient
{
    public async Task<(MediaEditorSelectionHistoryDto? History, string? Error)> GetMediaEditorSelectionHistoryAsync(
        Guid parentEntityId, IReadOnlyList<Guid> assetIds, CancellationToken ct = default)
    {
        try
        {
            using var response = await PostMediaEditorRequestAsync(
                $"/metadata/{parentEntityId:D}/owned-children/history",
                new MediaEditorSelectionHistoryRequestDto(assetIds), ct);
            if (!response.IsSuccessStatusCode)
            {
                return (null, response.StatusCode switch
                {
                    HttpStatusCode.Forbidden => "You do not have permission to view history for these files.",
                    HttpStatusCode.NotFound => "One or more selected files are no longer available here. Refresh the selection.",
                    HttpStatusCode.BadRequest => "The selected files could not be reviewed. Refresh the selection.",
                    HttpStatusCode.ServiceUnavailable => "Selected-file history is temporarily unavailable. Try again later.",
                    _ => "Selected-file history could not be loaded. Try again later.",
                });
            }

            var history = await response.Content.ReadFromJsonAsync<MediaEditorSelectionHistoryDto>(cancellationToken: ct);
            return history is null
                ? (null, "Selected-file history returned no data. Try again later.")
                : (history, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return (null, "Selected-file history loading was cancelled.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "POST /metadata/{ParentEntityId}/owned-children/history failed", parentEntityId);
            return (null, "Selected-file history could not be loaded. Try again later.");
        }
    }
}
