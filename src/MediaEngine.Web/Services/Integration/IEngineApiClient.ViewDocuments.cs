using MediaEngine.Contracts.LocalAssets;
using MediaEngine.Domain.PersonalMedia;

namespace MediaEngine.Web.Services.Integration;

public partial interface IEngineApiClient
{
    /// <summary>
    /// The tags, correspondents, document types and custom fields this person can see. With a scope the counts
    /// describe that scope (what the page shows); without one the lists cover everything they can open.
    /// </summary>
    Task<ViewDocumentCatalogueDto?> GetViewCatalogueAsync(ViewScopeKind? scope = null, Guid? scopeProfileId = null, IReadOnlyList<string>? kinds = null, CancellationToken ct = default);
    Task<LocalAssetDocumentInfoDto?> GetViewDocumentDetailsAsync(Guid itemId, ViewScopeKind scope, Guid? scopeProfileId = null, CancellationToken ct = default);
    Task<ViewCatalogueResult<LocalAssetDocumentInfoDto>> UpdateViewDocumentDetailsAsync(Guid itemId, UpdateViewDocumentDetailsRequest request, CancellationToken ct = default);
    Task<ViewCatalogueResult<ViewTagDto>> CreateViewTagAsync(ViewCatalogueEntryRequest request, CancellationToken ct = default);
    Task<ViewCatalogueResult<ViewTagDto>> UpdateViewTagAsync(Guid id, ViewCatalogueEntryRequest request, CancellationToken ct = default);
    Task<ViewCatalogueResult<bool>> DeleteViewTagAsync(Guid id, CancellationToken ct = default);
    Task<ViewCatalogueResult<ViewCorrespondentDto>> CreateViewCorrespondentAsync(ViewCatalogueEntryRequest request, CancellationToken ct = default);
    Task<ViewCatalogueResult<ViewCorrespondentDto>> UpdateViewCorrespondentAsync(Guid id, ViewCatalogueEntryRequest request, CancellationToken ct = default);
    Task<ViewCatalogueResult<bool>> DeleteViewCorrespondentAsync(Guid id, CancellationToken ct = default);
    Task<ViewCatalogueResult<ViewDocumentTypeDto>> CreateViewDocumentTypeAsync(ViewCatalogueEntryRequest request, CancellationToken ct = default);
    Task<ViewCatalogueResult<ViewDocumentTypeDto>> UpdateViewDocumentTypeAsync(Guid id, ViewCatalogueEntryRequest request, CancellationToken ct = default);
    Task<ViewCatalogueResult<bool>> DeleteViewDocumentTypeAsync(Guid id, CancellationToken ct = default);
    Task<ViewCatalogueResult<ViewDocumentFieldDto>> CreateViewDocumentFieldAsync(ViewDocumentFieldRequest request, CancellationToken ct = default);
    Task<ViewCatalogueResult<ViewDocumentFieldDto>> UpdateViewDocumentFieldAsync(Guid id, ViewDocumentFieldRequest request, CancellationToken ct = default);
    Task<ViewCatalogueResult<bool>> DeleteViewDocumentFieldAsync(Guid id, CancellationToken ct = default);

    /// <summary>Applies one bulk edit to the selected documents and returns what happened to each.</summary>
    Task<ViewCatalogueResult<ViewDocumentBulkResultDto>> BulkEditViewDocumentsAsync(ViewDocumentBulkRequest request, CancellationToken ct = default);

    /// <summary>The CSV of the list the options describe (at most 10,000 rows), or the reason it could not be made.</summary>
    Task<ViewDocumentExportResult> ExportViewDocumentsAsync(ViewAssetQueryOptions options, CancellationToken ct = default);

    /// <summary>How this profile likes the Documents list ordered; null for the default order or when it could not be read.</summary>
    Task<string?> GetViewDocumentsSortAsync(CancellationToken ct = default);

    Task<bool> SetViewDocumentsSortAsync(string? sort, CancellationToken ct = default);
}

/// <summary>A finished export: the file, or the reason there is none. <see cref="Truncated"/> means the list was cut at the row cap.</summary>
public sealed record ViewDocumentExportResult(byte[]? Bytes, string FileName, bool Truncated, string? Error = null)
{
    public bool Success => Error is null && Bytes is not null;
}

/// <summary>
/// The answer to a change to a Documents list or a document's details: the new value, or the plain-language reason
/// the Engine gave (for example that a name already exists).
/// </summary>
public sealed record ViewCatalogueResult<T>(T? Value, string? Error = null)
{
    public bool Success => Error is null && Value is not null;
}
