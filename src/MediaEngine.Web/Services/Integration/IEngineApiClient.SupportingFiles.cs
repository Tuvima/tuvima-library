using MediaEngine.Contracts.Details;
using MediaEngine.Contracts.Editing;

namespace MediaEngine.Web.Services.Integration;

public partial interface IEngineApiClient
{
    /// <summary>
    /// GET /metadata/{titleId}/supporting-files/{fileId}/editor - one supporting file for the editor: its values as set and as read from the
    /// folders, the roles, groups and places the form offers, and the owner's header with the file marked current. Null when the file is not
    /// one of the item's own, or the read fails.
    /// </summary>
    Task<EditorSupportingFileDto?> GetSupportingFileEditorAsync(Guid titleId, Guid fileId, CancellationToken ct = default);

    /// <summary>
    /// GET /metadata/{titleId}/supporting-files/{fileId}/suggest-details - what the file's document says about itself (title, subject, first
    /// lines), read on this computer. Null when the file cannot be read.
    /// </summary>
    Task<EditorSuggestDetailsDto?> GetSupportingFileSuggestDetailsAsync(Guid titleId, Guid fileId, CancellationToken ct = default);

    /// <summary>
    /// POST /api/v1/supporting-files/titles/{titleId}/preview - checks a change to supporting files and plans the file moves, writing nothing:
    /// the amber footer line and the Review rows. The outcome tells a refusal (400, 403) from the Engine being unreachable.
    /// </summary>
    Task<LibraryBatchEditOutcome<SupportingFileBulkUpdateResultDto>> PreviewSupportingFilesChangeAsync(
        Guid titleId, SupportingFileBulkUpdateRequest request, CancellationToken ct = default);

    /// <summary>
    /// POST /api/v1/supporting-files/titles/{titleId}/bulk-delete - removes supporting files from the Library at once; the files on disk follow
    /// the library's file handling (a Read-only folder keeps them). The outcome tells a refusal from the Engine being unreachable.
    /// </summary>
    Task<LibraryBatchEditOutcome<SupportingFileBulkDeleteResultDto>> DeleteSupportingFilesAsync(
        Guid titleId, SupportingFileBulkDeleteRequest request, CancellationToken ct = default);
}
