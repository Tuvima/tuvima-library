using MediaEngine.Contracts.LocalAssets;
using MediaEngine.Domain.PersonalMedia;

namespace MediaEngine.Web.Services.Integration;

public sealed partial class EngineApiClient
{
    public Task<ViewDocumentInboxSummaryDto?> GetViewDocumentInboxSummaryAsync(
        ViewScopeKind scope, Guid? scopeProfileId = null, CancellationToken ct = default) =>
        GetAsync<ViewDocumentInboxSummaryDto>("GET /view/documents/inbox-summary", "/view/documents/inbox-summary", new Dictionary<string, string?>
        {
            ["scope"] = ScopeValue(scope),
            ["scopeProfileId"] = scope.CarriesProfileId() ? scopeProfileId?.ToString("D") : null,
        }, ct: ct);

    public Task<ViewCatalogueResult<LocalAssetDocumentInfoDto>> FileViewDocumentFromInboxAsync(
        Guid itemId, FileViewDocumentFromInboxRequest request, CancellationToken ct = default) =>
        SendCatalogueAsync<LocalAssetDocumentInfoDto>(
            HttpMethod.Post, "POST /view/items/{id}/inbox/file", $"/view/items/{itemId:D}/inbox/file", request, ct);

    public Task<ViewDocumentRulesDto?> GetViewDocumentRulesAsync(CancellationToken ct = default) =>
        GetAsync<ViewDocumentRulesDto>("GET /view/document-rules", "/view/document-rules", ct: ct);

    public Task<ViewCatalogueResult<ViewDocumentRuleDto>> CreateViewDocumentRuleAsync(
        SaveViewDocumentRuleRequest request, CancellationToken ct = default) =>
        SendCatalogueAsync<ViewDocumentRuleDto>(HttpMethod.Post, "POST /view/document-rules", "/view/document-rules", request, ct);

    public Task<ViewCatalogueResult<ViewDocumentRuleDto>> UpdateViewDocumentRuleAsync(
        Guid id, SaveViewDocumentRuleRequest request, CancellationToken ct = default) =>
        SendCatalogueAsync<ViewDocumentRuleDto>(HttpMethod.Put, "PUT /view/document-rules/{id}", $"/view/document-rules/{id:D}", request, ct);

    public Task<ViewCatalogueResult<bool>> DeleteViewDocumentRuleAsync(Guid id, CancellationToken ct = default) =>
        DeleteCatalogueAsync("DELETE /view/document-rules/{id}", $"/view/document-rules/{id:D}", ct);

    public async Task<ViewCatalogueResult<bool>> ReorderViewDocumentRulesAsync(
        IReadOnlyList<Guid> ruleIds, CancellationToken ct = default)
    {
        const string endpoint = "PUT /view/document-rules/order";
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Put, "/view/document-rules/order")
            {
                Content = System.Net.Http.Json.JsonContent.Create(new ReorderViewDocumentRulesRequest(ruleIds)),
            };
            using var response = await _http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                var error = await ReadViewErrorAsync(response, ct);
                await RecordHttpFailureAsync(endpoint, response, ct);
                return new ViewCatalogueResult<bool>(false, error);
            }

            ClearFailure(endpoint);
            return new ViewCatalogueResult<bool>(true);
        }
        catch (OperationCanceledException) { return new ViewCatalogueResult<bool>(false, "The request was canceled."); }
        catch (Exception ex)
        {
            RecordExceptionFailure(endpoint, ex, true);
            return new ViewCatalogueResult<bool>(false, "The Dashboard could not reach the Engine.");
        }
    }

    public Task<ViewCatalogueResult<ViewDocumentRuleTestDto>> TestViewDocumentRuleAsync(
        SaveViewDocumentRuleRequest request, CancellationToken ct = default) =>
        SendCatalogueAsync<ViewDocumentRuleTestDto>(HttpMethod.Post, "POST /view/document-rules/test", "/view/document-rules/test", request, ct);

    public Task<ViewCatalogueResult<ViewDocumentRulesApplyResultDto>> ApplyViewDocumentRulesAsync(CancellationToken ct = default) =>
        SendCatalogueAsync<ViewDocumentRulesApplyResultDto>(
            HttpMethod.Post, "POST /view/document-rules/apply", "/view/document-rules/apply", new { }, ct);
}
