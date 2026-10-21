using MediaEngine.Contracts.Library;

namespace MediaEngine.Web.Services.Integration;

public partial interface IEngineApiClient
{
    /// <summary>
    /// POST /library/batch-edit/preview - how the selected items stand on each batch-editable field: the shared value or Mixed, list or
    /// single value, and which items cannot take a change. Changes nothing. The outcome tells 200, 400, 403 and unreachable apart.
    /// </summary>
    Task<LibraryBatchEditOutcome<LibraryBatchEditPreview>> PreviewLibraryBatchEditAsync(
        LibraryBatchEditPreviewRequest request, CancellationToken ct = default);

    /// <summary>
    /// POST /library/batch-edit - applies the field operations to every selected item in one request; an item that cannot take them goes
    /// to Needs Review (or is refused with a reason) and the rest still apply. The outcome tells 200, 400, 403 and unreachable apart.
    /// </summary>
    Task<LibraryBatchEditOutcome<LibraryBatchEditResult>> ApplyLibraryBatchEditAsync(
        LibraryBatchEditRequest request, CancellationToken ct = default);
}
