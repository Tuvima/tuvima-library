using MediaEngine.Contracts.Details;
using MediaEngine.Contracts.Editing;

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

    public Task<LibraryBatchEditOutcome<SupportingFileBulkUpdateResultDto>> PreviewSupportingFilesChangeAsync(
        Guid titleId, SupportingFileBulkUpdateRequest request, CancellationToken ct = default) =>
        PostBatchEditAsync<SupportingFileBulkUpdateRequest, SupportingFileBulkUpdateResultDto>(
            $"/api/v1/supporting-files/titles/{titleId:D}/preview", request, "POST /api/v1/supporting-files/titles/{id}/preview", ct);

    public Task<LibraryBatchEditOutcome<SupportingFileBulkDeleteResultDto>> DeleteSupportingFilesAsync(
        Guid titleId, SupportingFileBulkDeleteRequest request, CancellationToken ct = default) =>
        PostBatchEditAsync<SupportingFileBulkDeleteRequest, SupportingFileBulkDeleteResultDto>(
            $"/api/v1/supporting-files/titles/{titleId:D}/bulk-delete", request, "POST /api/v1/supporting-files/titles/{id}/bulk-delete", ct);
}
