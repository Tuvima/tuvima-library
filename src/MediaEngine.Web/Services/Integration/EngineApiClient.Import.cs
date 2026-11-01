using MediaEngine.Contracts.Collections;
using MediaEngine.Contracts.Import;

namespace MediaEngine.Web.Services.Integration;

public sealed partial class EngineApiClient
{
    public Task<ImportPreviewDto?> RecogniseImportAsync(ImportRecogniseRequest request, CancellationToken ct = default) =>
        PostAsync<ImportRecogniseRequest, ImportPreviewDto>(
            "POST /import/recognise", "/import/recognise", request, logAsWarning: false, ct: ct);

    public Task<ImportApplyResultDto?> ApplyImportAsync(ImportApplyRequest request, CancellationToken ct = default) =>
        PostAsync<ImportApplyRequest, ImportApplyResultDto>(
            "POST /import/apply", "/import/apply", request, logAsWarning: false, ct: ct);

    public Task<ListImportPreviewDto?> PreviewCollectionListAsync(ListImportPreviewRequest request, CancellationToken ct = default) =>
        PostAsync<ListImportPreviewRequest, ListImportPreviewDto>(
            "POST /collections/preview-list", "/collections/preview-list", request, logAsWarning: false, ct: ct);

    public Task<ListImportResultDto?> ImportCollectionListAsync(Guid collectionId, ListImportRequest request, CancellationToken ct = default) =>
        PostAsync<ListImportRequest, ListImportResultDto>(
            "POST /collections/{id}/list", $"/collections/{collectionId:D}/list", request, logAsWarning: false, ct: ct);

    public Task<CollectionListSourceDto?> GetCollectionListSourceAsync(Guid collectionId, CancellationToken ct = default) =>
        GetAsync<CollectionListSourceDto>(
            "GET /collections/{id}/list", $"/collections/{collectionId:D}/list", ct: ct);

    public Task<bool> UpdateCollectionListSettingsAsync(Guid collectionId, ListSourceSettingsRequest request, CancellationToken ct = default) =>
        PutAsync("PUT /collections/{id}/list", $"/collections/{collectionId:D}/list", request, logAsWarning: false, ct: ct);

    public Task<ListRefreshResultDto?> RefreshCollectionListAsync(Guid collectionId, ListRefreshRequest request, CancellationToken ct = default) =>
        PostAsync<ListRefreshRequest, ListRefreshResultDto>(
            "POST /collections/{id}/list/refresh", $"/collections/{collectionId:D}/list/refresh", request, logAsWarning: false, ct: ct);

    public Task<bool> FixCollectionListEntryAsync(Guid collectionId, Guid entryId, ListEntryFixRequest request, CancellationToken ct = default) =>
        PutAsync(
            "PUT /collections/{id}/list/entries/{entryId}",
            $"/collections/{collectionId:D}/list/entries/{entryId:D}", request, logAsWarning: false, ct: ct);
}
