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
}

/// <summary>
/// The answer to a change to a Documents list or a document's details: the new value, or the plain-language reason
/// the Engine gave (for example that a name already exists).
/// </summary>
public sealed record ViewCatalogueResult<T>(T? Value, string? Error = null)
{
    public bool Success => Error is null && Value is not null;
}
