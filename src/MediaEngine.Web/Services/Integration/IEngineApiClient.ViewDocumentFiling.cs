using MediaEngine.Contracts.LocalAssets;
using MediaEngine.Domain.PersonalMedia;

namespace MediaEngine.Web.Services.Integration;

public partial interface IEngineApiClient
{
    /// <summary>How many documents wait in the Inbox of the scope being looked at; null when the Engine could not say.</summary>
    Task<ViewDocumentInboxSummaryDto?> GetViewDocumentInboxSummaryAsync(ViewScopeKind scope, Guid? scopeProfileId = null, CancellationToken ct = default);

    /// <summary>Files one Inbox document, optionally taking its suggestions. Returns the document's new details.</summary>
    Task<ViewCatalogueResult<LocalAssetDocumentInfoDto>> FileViewDocumentFromInboxAsync(Guid itemId, FileViewDocumentFromInboxRequest request, CancellationToken ct = default);

    /// <summary>The person's own auto-filing rules in running order, with the sources a rule can name.</summary>
    Task<ViewDocumentRulesDto?> GetViewDocumentRulesAsync(CancellationToken ct = default);
    Task<ViewCatalogueResult<ViewDocumentRuleDto>> CreateViewDocumentRuleAsync(SaveViewDocumentRuleRequest request, CancellationToken ct = default);
    Task<ViewCatalogueResult<ViewDocumentRuleDto>> UpdateViewDocumentRuleAsync(Guid id, SaveViewDocumentRuleRequest request, CancellationToken ct = default);
    Task<ViewCatalogueResult<bool>> DeleteViewDocumentRuleAsync(Guid id, CancellationToken ct = default);

    /// <summary>Saves a new running order. Every rule id of the person, first to last.</summary>
    Task<ViewCatalogueResult<bool>> ReorderViewDocumentRulesAsync(IReadOnlyList<Guid> ruleIds, CancellationToken ct = default);

    /// <summary>Lists up to ten of the person's documents a rule (saved or not) would match.</summary>
    Task<ViewCatalogueResult<ViewDocumentRuleTestDto>> TestViewDocumentRuleAsync(SaveViewDocumentRuleRequest request, CancellationToken ct = default);

    /// <summary>Queues the person's rules to run on their existing documents.</summary>
    Task<ViewCatalogueResult<ViewDocumentRulesApplyResultDto>> ApplyViewDocumentRulesAsync(CancellationToken ct = default);
}
