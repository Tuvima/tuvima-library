using System.Net.Http.Json;
using MediaEngine.Contracts.LocalAssets;
using MediaEngine.Domain.PersonalMedia;

namespace MediaEngine.Web.Services.Integration;

public sealed partial class EngineApiClient
{
    public Task<ViewDocumentCatalogueDto?> GetViewCatalogueAsync(
        ViewScopeKind? scope = null, Guid? scopeProfileId = null, IReadOnlyList<string>? kinds = null, CancellationToken ct = default)
    {
        var query = new List<string>();
        AddQuery(query, "scope", scope.HasValue ? ScopeValue(scope.Value) : null);
        AddQuery(query, "scopeProfileId", scope is { } scopeKind && scopeKind.CarriesProfileId() ? scopeProfileId?.ToString("D") : null);
        foreach (var kind in kinds ?? [])
        {
            AddQuery(query, "kind", kind);
        }

        var path = query.Count == 0 ? "/view/catalogue" : $"/view/catalogue?{string.Join('&', query)}";
        return GetAsync<ViewDocumentCatalogueDto>("GET /view/catalogue", path, ct: ct);
    }

    public Task<LocalAssetDocumentInfoDto?> GetViewDocumentDetailsAsync(
        Guid itemId, ViewScopeKind scope, Guid? scopeProfileId = null, CancellationToken ct = default) =>
        GetAsync<LocalAssetDocumentInfoDto>("GET /view/items/{id}/document-details", $"/view/items/{itemId:D}/document-details", new Dictionary<string, string?>
        {
            ["scope"] = ScopeValue(scope),
            ["scopeProfileId"] = scope.CarriesProfileId() ? scopeProfileId?.ToString("D") : null,
        }, ct: ct);

    public Task<ViewCatalogueResult<LocalAssetDocumentInfoDto>> UpdateViewDocumentDetailsAsync(
        Guid itemId, UpdateViewDocumentDetailsRequest request, CancellationToken ct = default) =>
        SendCatalogueAsync<LocalAssetDocumentInfoDto>(HttpMethod.Put, "PUT /view/items/{id}/document-details", $"/view/items/{itemId:D}/document-details", request, ct);

    public Task<ViewCatalogueResult<ViewTagDto>> CreateViewTagAsync(ViewCatalogueEntryRequest request, CancellationToken ct = default) =>
        SendCatalogueAsync<ViewTagDto>(HttpMethod.Post, "POST /view/tags", "/view/tags", request, ct);
    public Task<ViewCatalogueResult<ViewTagDto>> UpdateViewTagAsync(Guid id, ViewCatalogueEntryRequest request, CancellationToken ct = default) =>
        SendCatalogueAsync<ViewTagDto>(HttpMethod.Put, "PUT /view/tags/{id}", $"/view/tags/{id:D}", request, ct);
    public Task<ViewCatalogueResult<bool>> DeleteViewTagAsync(Guid id, CancellationToken ct = default) =>
        DeleteCatalogueAsync("DELETE /view/tags/{id}", $"/view/tags/{id:D}", ct);

    public Task<ViewCatalogueResult<ViewCorrespondentDto>> CreateViewCorrespondentAsync(ViewCatalogueEntryRequest request, CancellationToken ct = default) =>
        SendCatalogueAsync<ViewCorrespondentDto>(HttpMethod.Post, "POST /view/correspondents", "/view/correspondents", request, ct);
    public Task<ViewCatalogueResult<ViewCorrespondentDto>> UpdateViewCorrespondentAsync(Guid id, ViewCatalogueEntryRequest request, CancellationToken ct = default) =>
        SendCatalogueAsync<ViewCorrespondentDto>(HttpMethod.Put, "PUT /view/correspondents/{id}", $"/view/correspondents/{id:D}", request, ct);
    public Task<ViewCatalogueResult<bool>> DeleteViewCorrespondentAsync(Guid id, CancellationToken ct = default) =>
        DeleteCatalogueAsync("DELETE /view/correspondents/{id}", $"/view/correspondents/{id:D}", ct);

    public Task<ViewCatalogueResult<ViewDocumentTypeDto>> CreateViewDocumentTypeAsync(ViewCatalogueEntryRequest request, CancellationToken ct = default) =>
        SendCatalogueAsync<ViewDocumentTypeDto>(HttpMethod.Post, "POST /view/document-types", "/view/document-types", request, ct);
    public Task<ViewCatalogueResult<ViewDocumentTypeDto>> UpdateViewDocumentTypeAsync(Guid id, ViewCatalogueEntryRequest request, CancellationToken ct = default) =>
        SendCatalogueAsync<ViewDocumentTypeDto>(HttpMethod.Put, "PUT /view/document-types/{id}", $"/view/document-types/{id:D}", request, ct);
    public Task<ViewCatalogueResult<bool>> DeleteViewDocumentTypeAsync(Guid id, CancellationToken ct = default) =>
        DeleteCatalogueAsync("DELETE /view/document-types/{id}", $"/view/document-types/{id:D}", ct);

    public Task<ViewCatalogueResult<ViewDocumentFieldDto>> CreateViewDocumentFieldAsync(ViewDocumentFieldRequest request, CancellationToken ct = default) =>
        SendCatalogueAsync<ViewDocumentFieldDto>(HttpMethod.Post, "POST /view/document-fields", "/view/document-fields", request, ct);
    public Task<ViewCatalogueResult<ViewDocumentFieldDto>> UpdateViewDocumentFieldAsync(Guid id, ViewDocumentFieldRequest request, CancellationToken ct = default) =>
        SendCatalogueAsync<ViewDocumentFieldDto>(HttpMethod.Put, "PUT /view/document-fields/{id}", $"/view/document-fields/{id:D}", request, ct);
    public Task<ViewCatalogueResult<bool>> DeleteViewDocumentFieldAsync(Guid id, CancellationToken ct = default) =>
        DeleteCatalogueAsync("DELETE /view/document-fields/{id}", $"/view/document-fields/{id:D}", ct);

    /// <summary>Sends a change and returns the new value, or the Engine's own reason (such as "a tag named X already exists").</summary>
    private async Task<ViewCatalogueResult<T>> SendCatalogueAsync<T>(
        HttpMethod method, string endpoint, string path, object body, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(method, path) { Content = JsonContent.Create(body) };
            using var response = await _http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                var error = await ReadViewErrorAsync(response, ct);
                await RecordHttpFailureAsync(endpoint, response, ct);
                return new ViewCatalogueResult<T>(default, error);
            }

            ClearFailure(endpoint);
            var value = await response.Content.ReadFromJsonAsync<T>(cancellationToken: ct);
            return value is null
                ? new ViewCatalogueResult<T>(default, "The Engine returned nothing.")
                : new ViewCatalogueResult<T>(value);
        }
        catch (OperationCanceledException) { return new ViewCatalogueResult<T>(default, "The request was canceled."); }
        catch (Exception ex)
        {
            RecordExceptionFailure(endpoint, ex, true);
            return new ViewCatalogueResult<T>(default, "The Dashboard could not reach the Engine.");
        }
    }

    private async Task<ViewCatalogueResult<bool>> DeleteCatalogueAsync(string endpoint, string path, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Delete, path);
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
}
