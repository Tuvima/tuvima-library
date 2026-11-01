using MediaEngine.Contracts.Collections;
using MediaEngine.Contracts.Import;

namespace MediaEngine.Web.Services.Integration;

public partial interface IEngineApiClient
{
    // ── Shared Import and imported lists (Collections and lists spec D17, 4b, 4d) ────────────────────────────────────

    /// <summary>POST /import/recognise - reads a file or pasted text and returns the preview for its kind. Nothing is saved.</summary>
    Task<ImportPreviewDto?> RecogniseImportAsync(ImportRecogniseRequest request, CancellationToken ct = default);

    /// <summary>POST /import/apply - applies a recognised file as the kind that was previewed, where that kind can be applied on its own.</summary>
    Task<ImportApplyResultDto?> ApplyImportAsync(ImportApplyRequest request, CancellationToken ct = default);

    /// <summary>POST /collections/preview-list - reads a list file and says how much of it the Library owns. Nothing is saved.</summary>
    Task<ListImportPreviewDto?> PreviewCollectionListAsync(ListImportPreviewRequest request, CancellationToken ct = default);

    /// <summary>POST /collections/{id}/list - makes an imported list the source of a collection.</summary>
    Task<ListImportResultDto?> ImportCollectionListAsync(Guid collectionId, ListImportRequest request, CancellationToken ct = default);

    /// <summary>GET /collections/{id}/list - where a collection's list came from and the titles that could not be matched.</summary>
    Task<CollectionListSourceDto?> GetCollectionListSourceAsync(Guid collectionId, CancellationToken ct = default);

    /// <summary>PUT /collections/{id}/list - changes the order, refresh and sync choices of an imported list.</summary>
    Task<bool> UpdateCollectionListSettingsAsync(Guid collectionId, ListSourceSettingsRequest request, CancellationToken ct = default);

    /// <summary>POST /collections/{id}/list/refresh - refreshes the list (with a newer file when given).</summary>
    Task<ListRefreshResultDto?> RefreshCollectionListAsync(Guid collectionId, ListRefreshRequest request, CancellationToken ct = default);

    /// <summary>PUT /collections/{id}/list/entries/{entryId} - picks the right title for a list entry, ignores it, or restores it.</summary>
    Task<bool> FixCollectionListEntryAsync(Guid collectionId, Guid entryId, ListEntryFixRequest request, CancellationToken ct = default);
}
