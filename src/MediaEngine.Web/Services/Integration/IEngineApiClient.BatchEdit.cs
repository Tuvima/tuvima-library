using MediaEngine.Contracts.Library;

namespace MediaEngine.Web.Services.Integration;

public partial interface IEngineApiClient
{
    /// <summary>POST /library/batch-edit/preview - what the current values are across the ticked rows (so the section can say "Mixed"). Changes nothing.</summary>
    Task<LibraryBatchEditPreview?> PreviewBatchEditAsync(LibraryBatchEditRequest request, CancellationToken ct = default);

    /// <summary>POST /library/batch-edit - applies the changes (with Add, Replace or Remove for list fields) to the ticked rows; rows that fail go to Needs Review.</summary>
    Task<LibraryBatchEditResult?> ApplyBatchEditAsync(LibraryBatchEditRequest request, CancellationToken ct = default);
}
