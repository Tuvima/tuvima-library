using MediaEngine.Contracts.Details;
using MediaEngine.Contracts.Editing;
using MediaEngine.Web.Models.ViewDTOs;

namespace MediaEngine.Web.Services.Integration;

public sealed partial class EngineApiClient
{
    public Task<EditorSupportingFileDto?> GetSupportingFileEditorAsync(Guid titleId, Guid fileId, CancellationToken ct = default) =>
        GetAsync<EditorSupportingFileDto>(
            "GET /metadata/{id}/supporting-files/{fileId}/editor", $"/metadata/{titleId:D}/supporting-files/{fileId:D}/editor", ct: ct);

    public Task<EditorSuggestDetailsDto?> GetSupportingFileSuggestDetailsAsync(Guid titleId, Guid fileId, CancellationToken ct = default) =>
        GetAsync<EditorSuggestDetailsDto>(
            "GET /metadata/{id}/supporting-files/{fileId}/suggest-details",
            $"/metadata/{titleId:D}/supporting-files/{fileId:D}/suggest-details",
            logAsWarning: false,
            ct: ct);

    public async Task<(List<LibraryItemHistoryDto> Items, string? Error)> GetSupportingFileHistoryAsync(Guid titleId, Guid fileId, CancellationToken ct = default)
    {
        var rows = await GetAsync<List<MediaEngine.Contracts.Items.LibraryItemHistoryDto>>(
            "GET /metadata/{id}/supporting-files/{fileId}/history", $"/metadata/{titleId:D}/supporting-files/{fileId:D}/history", ct: ct);
        return rows is null
            ? ([], "History could not be loaded. Try again later.")
            : ([.. rows.Select(item => new LibraryItemHistoryDto
            {
                Id = item.Id,
                EntityId = item.EntityId,
                OccurredAt = item.OccurredAt,
                EventType = item.EventType,
                Label = item.Label,
                Detail = item.Detail,
                Category = item.Category,
                ActorLabel = item.ActorLabel,
            })], null);
    }

    public Task<LibraryBatchEditOutcome<SupportingFileBulkUpdateResultDto>> PreviewSupportingFilesChangeAsync(
        Guid titleId, SupportingFileBulkUpdateRequest request, CancellationToken ct = default) =>
        PostBatchEditAsync<SupportingFileBulkUpdateRequest, SupportingFileBulkUpdateResultDto>(
            $"/api/v1/supporting-files/titles/{titleId:D}/preview", request, "POST /api/v1/supporting-files/titles/{id}/preview", ct);

    public Task<LibraryBatchEditOutcome<SupportingFileBulkDeleteResultDto>> DeleteSupportingFilesAsync(
        Guid titleId, SupportingFileBulkDeleteRequest request, CancellationToken ct = default) =>
        PostBatchEditAsync<SupportingFileBulkDeleteRequest, SupportingFileBulkDeleteResultDto>(
            $"/api/v1/supporting-files/titles/{titleId:D}/bulk-delete", request, "POST /api/v1/supporting-files/titles/{id}/bulk-delete", ct);
}
