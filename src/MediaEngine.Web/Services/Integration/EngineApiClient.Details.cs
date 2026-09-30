using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using MediaEngine.Contracts.Details;
using MediaEngine.Contracts.Display;
using MediaEngine.Contracts.Paging;
using MediaEngine.Contracts.Playback;
using MediaEngine.Contracts.Settings;
using MediaEngine.Domain.Models;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Branding;
using MediaEngine.Web.Services.Integration.Clients;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Web.Services.Integration;

public sealed partial class EngineApiClient
{
    // -- GET /collections -------------------------------------------------------------

    public async Task<AuthSettingsDto?> GetAuthSettingsAsync(CancellationToken ct = default)
    {
        try
        {
            return await _http.GetFromJsonAsync<AuthSettingsDto>("/settings/security/auth", ct);
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GET /settings/security/auth failed");
            return null;
        }
    }

    public Task<AuthSettingsDto?> UpdateAuthSettingsAsync(
        UpdateAuthSettingsRequest request,
        CancellationToken ct = default) =>
        PutAsync<UpdateAuthSettingsRequest, AuthSettingsDto>(
            "PUT /settings/security/auth", "/settings/security/auth", request, ct: ct);

    public Task<AuthSettingsDto?> UpdateExternalAuthProviderAsync(
        string providerId,
        UpdateExternalAuthProviderRequest request,
        CancellationToken ct = default) =>
        PutAsync<UpdateExternalAuthProviderRequest, AuthSettingsDto>(
            "PUT /settings/security/auth/providers/{providerId}",
            $"/settings/security/auth/providers/{Uri.EscapeDataString(providerId)}", request, ct: ct);

    public async Task<bool> DeleteExternalAuthProviderAsync(string providerId, CancellationToken ct = default)
    {
        try
        {
            using var response = await _http.DeleteAsync(
                $"/settings/security/auth/providers/{Uri.EscapeDataString(providerId)}", ct).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "DELETE authentication provider configuration failed");
            return false;
        }
    }

    public async Task<List<CollectionViewModel>> GetCollectionsAsync(CancellationToken ct = default)
    {
        try
        {
            var raw = await _http.GetFromJsonAsync<List<MediaEngine.Contracts.Collections.CollectionDto>>(
                "/collections", ct);
            return raw?.Select(MapCollection).ToList() ?? [];
        }
        catch (OperationCanceledException) { return []; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GET /collections failed");
            return [];
        }
    }

    // -- GET /library/works -----------------------------------------------------

    public async Task<List<WorkViewModel>> GetLibraryWorksAsync(int offset = 0, int limit = 500, CancellationToken ct = default)
    {
        const string endpoint = "GET /library/works";
        try
        {
            var safeOffset = Math.Max(0, offset);
            var safeLimit = Math.Clamp(limit <= 0 ? 500 : limit, 1, 500);
            var response = await _http.GetAsync($"/library/works?offset={safeOffset}&limit={safeLimit}", ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                await RecordHttpFailureAsync(endpoint, response, ct);
                return [];
            }

            var payload = await response.Content.ReadFromJsonAsync<
                PagedResponse<MediaEngine.Contracts.Collections.LibraryWorkListItemDto>>(
                cancellationToken: ct).ConfigureAwait(false);
            ClearFailure(endpoint);
            return payload?.Items.Select(MapLibraryWork).ToList() ?? [];
        }
        catch (OperationCanceledException) { return []; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GET /library/works failed");
            RecordExceptionFailure(endpoint, ex);
            return [];
        }
    }

    public async Task<DetailPageViewModel?> GetDetailPageAsync(
        DetailEntityType entityType,
        Guid id,
        DetailPresentationContext context = DetailPresentationContext.Default,
        string? containerId = null,
        Guid? profileId = null,
        CancellationToken ct = default)
    {
        try
        {
            var entity = Uri.EscapeDataString(entityType.ToString().ToLowerInvariant());
            var ctx = Uri.EscapeDataString(context.ToString().ToLowerInvariant());
            var query = new List<string> { $"context={ctx}" };
            if (!string.IsNullOrWhiteSpace(containerId))
            {
                query.Add($"containerId={Uri.EscapeDataString(containerId)}");
            }

            AddQuery(query, "profileId", profileId?.ToString("D"));
            var detail = await _http.GetFromJsonAsync<DetailPageViewModel>($"/api/v1/details/{entity}/{id:D}?{string.Join('&', query)}", ct);
            return detail is null ? null : NormalizeDetailArtwork(detail);
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GET /api/details/{EntityType}/{Id} failed", entityType, id);
            return null;
        }
    }

    public async Task<bool> SetDefaultSequenceAsync(
        DetailEntityType entityType,
        Guid id,
        string containerId,
        string? containerTitle = null,
        CancellationToken ct = default)
    {
        try
        {
            var entity = Uri.EscapeDataString(entityType.ToString().ToLowerInvariant());
            var response = await _http.PutAsJsonAsync(
                $"/api/v1/details/{entity}/{id:D}/sequence-default",
                new SetDefaultSequenceRequest { ContainerId = containerId, ContainerTitle = containerTitle },
                ct);
            return response.IsSuccessStatusCode;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PUT /api/details/{EntityType}/{Id}/sequence-default failed", entityType, id);
            return false;
        }
    }

    // -- Item preferences (/library/items/{entityId}/preferences) ----

    public async Task<bool> SaveItemPreferencesAsync(
        Guid entityId, Dictionary<string, string> fields, CancellationToken ct = default)
    {
        try
        {
            var body = new { fields };
            var resp = await _http.PutAsJsonAsync($"/library/items/{entityId}/preferences", body, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var detail = await resp.Content.ReadAsStringAsync(ct);
                _logger.LogWarning("PUT /library/items/{EntityId}/preferences returned {Status}: {Detail}",
                    entityId, (int)resp.StatusCode, detail);
                LastError = $"HTTP {(int)resp.StatusCode}: {detail}";
            }
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PUT /library/items/{EntityId}/preferences failed", entityId);
            LastError = ex.Message;
            return false;
        }
    }

    public Task<bool> SaveCollectionDisplayOverridesAsync(Guid collectionId, Dictionary<string, string> fields, CancellationToken ct = default)
        => SaveDisplayOverridesAsync($"/collections/{collectionId}/display-overrides", collectionId, fields, ct);

    public Task<bool> SaveItemDisplayOverridesAsync(Guid entityId, Dictionary<string, string> fields, CancellationToken ct = default)
        => SaveDisplayOverridesAsync($"/library/items/{entityId}/display-overrides", entityId, fields, ct);

    private async Task<bool> SaveDisplayOverridesAsync(string route,
        Guid entityId, Dictionary<string, string> fields, CancellationToken ct = default)
    {
        try
        {
            var body = new { fields };
            var resp = await _http.PutAsJsonAsync(route, body, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var detail = await resp.Content.ReadAsStringAsync(ct);
                _logger.LogWarning("PUT /library/items/{EntityId}/display-overrides returned {Status}: {Detail}",
                    entityId, (int)resp.StatusCode, detail);
                LastError = $"HTTP {(int)resp.StatusCode}: {detail}";
            }

            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PUT /library/items/{EntityId}/display-overrides failed", entityId);
            LastError = ex.Message;
            return false;
        }
    }

    // -- Cover Art Upload --------------------------------------------------

    public async Task<bool> UploadCoverAsync(
        Guid entityId, Stream fileStream, string fileName, CancellationToken ct = default)
    {
        try
        {
            using var content = new MultipartFormDataContent();
            var streamContent = new StreamContent(fileStream);
            content.Add(streamContent, "file", fileName);

            var resp = await _http.PostAsync($"/metadata/{entityId}/cover", content, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var detail = await resp.Content.ReadAsStringAsync(ct);
                _logger.LogWarning("POST /metadata/{EntityId}/cover returned {Status}: {Detail}",
                    entityId, (int)resp.StatusCode, detail);
                LastError = $"HTTP {(int)resp.StatusCode}: {detail}";
            }
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "POST /metadata/{EntityId}/cover failed", entityId);
            LastError = ex.Message;
            return false;
        }
    }

    public async Task<MediaEditorContextDto?> GetMediaEditorContextAsync(Guid entityId, CancellationToken ct = default)
    {
        try
        {
            return await _http.GetFromJsonAsync<MediaEditorContextDto>($"/metadata/{entityId}/editor-context", ct);
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GET /metadata/{EntityId}/editor-context failed", entityId);
            return null;
        }
    }

    public async Task<MediaEditorNavigatorDto?> GetMediaEditorNavigatorAsync(Guid entityId, CancellationToken ct = default)
    {
        try
        {
            return await _http.GetFromJsonAsync<MediaEditorNavigatorDto>($"/metadata/{entityId}/navigator", ct);
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GET /metadata/{EntityId}/navigator failed", entityId);
            return null;
        }
    }

    public async Task<MediaEditorOwnedChildSearchDto?> GetMediaEditorOwnedChildrenAsync(
        Guid entityId, string? query = null, int page = 1, int pageSize = 50,
        int? season = null, int? disc = null, int? volume = null,
        string? matchStatus = null, string? fileStatus = null, CancellationToken ct = default)
    {
        try
        {
            var parts = new List<string> { $"page={Math.Max(1, page)}", $"pageSize={Math.Clamp(pageSize, 1, 100)}" };
            if (!string.IsNullOrWhiteSpace(query)) parts.Add($"q={Uri.EscapeDataString(query.Trim())}");
            if (season.HasValue) parts.Add($"season={season.Value}");
            if (disc.HasValue) parts.Add($"disc={disc.Value}");
            if (volume.HasValue) parts.Add($"volume={volume.Value}");
            if (!string.IsNullOrWhiteSpace(matchStatus)) parts.Add($"matchStatus={Uri.EscapeDataString(matchStatus)}");
            if (!string.IsNullOrWhiteSpace(fileStatus)) parts.Add($"fileStatus={Uri.EscapeDataString(fileStatus)}");
            return await _http.GetFromJsonAsync<MediaEditorOwnedChildSearchDto>($"/metadata/{entityId}/owned-children?{string.Join("&", parts)}", ct);
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GET /metadata/{EntityId}/owned-children failed", entityId);
            return null;
        }
    }

    public async Task<List<MediaEditorMembershipSuggestionDto>> GetMediaEditorMembershipSuggestionsAsync(
        Guid entityId,
        string field,
        string? query = null,
        string? source = null,
        Guid? parentEntityId = null,
        string? parentValue = null,
        CancellationToken ct = default)
    {
        try
        {
            var queryParts = new List<string> { $"field={Uri.EscapeDataString(field)}" };
            if (!string.IsNullOrWhiteSpace(query))
            {
                queryParts.Add($"query={Uri.EscapeDataString(query)}");
            }

            if (!string.IsNullOrWhiteSpace(source))
            {
                queryParts.Add($"source={Uri.EscapeDataString(source)}");
            }

            if (parentEntityId.HasValue)
            {
                queryParts.Add($"parentEntityId={Uri.EscapeDataString(parentEntityId.Value.ToString())}");
            }

            if (!string.IsNullOrWhiteSpace(parentValue))
            {
                queryParts.Add($"parentValue={Uri.EscapeDataString(parentValue)}");
            }

            var url = $"/metadata/{entityId}/membership-suggestions?{string.Join("&", queryParts)}";
            return await _http.GetFromJsonAsync<List<MediaEditorMembershipSuggestionDto>>(url, ct) ?? [];
        }
        catch (OperationCanceledException) { return []; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GET /metadata/{EntityId}/membership-suggestions failed", entityId);
            return [];
        }
    }

    public async Task<MediaEditorMembershipPreviewDto?> PreviewMediaEditorMembershipAsync(
        Guid entityId,
        MediaEditorMembershipPreviewRequestDto request,
        CancellationToken ct = default)
    {
        try
        {
            var response = await _http.PostAsJsonAsync($"/metadata/{entityId}/membership-preview", request, ct);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<MediaEditorMembershipPreviewDto>(ct);
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "POST /metadata/{EntityId}/membership-preview failed", entityId);
            return null;
        }
    }

    public async Task<MediaEditorMembershipPreviewDto?> ApplyMediaEditorMembershipAsync(
        Guid entityId,
        MediaEditorMembershipPreviewRequestDto request,
        CancellationToken ct = default)
    {
        try
        {
            var response = await _http.PostAsJsonAsync($"/metadata/{entityId}/membership-apply", request, ct);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<MediaEditorMembershipPreviewDto>(ct);
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "POST /metadata/{EntityId}/membership-apply failed", entityId);
            return null;
        }
    }

    public async Task<bool> UploadScopeArtworkVariantAsync(
        Guid entityId,
        string scopeId,
        string assetType,
        Stream fileStream,
        string fileName,
        CancellationToken ct = default)
    {
        try
        {
            using var content = new MultipartFormDataContent();
            var streamContent = new StreamContent(fileStream);
            content.Add(streamContent, "file", fileName);

            var encodedScope = Uri.EscapeDataString(scopeId);
            var encodedType = Uri.EscapeDataString(assetType);
            var resp = await _http.PostAsync($"/metadata/{entityId}/artwork/{encodedScope}/{encodedType}", content, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var detail = await resp.Content.ReadAsStringAsync(ct);
                _logger.LogWarning("POST /metadata/{EntityId}/artwork/{ScopeId}/{AssetType} returned {Status}: {Detail}",
                    entityId, scopeId, assetType, (int)resp.StatusCode, detail);
                LastError = $"HTTP {(int)resp.StatusCode}: {detail}";
            }

            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "POST /metadata/{EntityId}/artwork/{ScopeId}/{AssetType} failed", entityId, scopeId, assetType);
            LastError = ex.Message;
            return false;
        }
    }

    public async Task<bool> UploadScopeArtworkFromUrlAsync(
        Guid entityId,
        string scopeId,
        string assetType,
        string imageUrl,
        CancellationToken ct = default)
    {
        try
        {
            var encodedScope = Uri.EscapeDataString(scopeId);
            var encodedType = Uri.EscapeDataString(assetType);
            var response = await _http.PostAsJsonAsync(
                $"/metadata/{entityId}/artwork/{encodedScope}/{encodedType}/from-url",
                new { image_url = imageUrl },
                ct);

            if (!response.IsSuccessStatusCode)
            {
                var detail = await response.Content.ReadAsStringAsync(ct);
                _logger.LogWarning(
                    "POST /metadata/{EntityId}/artwork/{ScopeId}/{AssetType}/from-url returned {Status}: {Detail}",
                    entityId,
                    scopeId,
                    assetType,
                    (int)response.StatusCode,
                    detail);
                LastError = $"HTTP {(int)response.StatusCode}: {detail}";
            }

            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "POST /metadata/{EntityId}/artwork/{ScopeId}/{AssetType}/from-url failed",
                entityId,
                scopeId,
                assetType);
            LastError = ex.Message;
            return false;
        }
    }

    public async Task<bool> SetPreferredArtworkAsync(Guid variantId, CancellationToken ct = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Put, $"/metadata/artwork/{variantId}/preferred")
            {
                Content = JsonContent.Create(new { }),
            };

            var response = await _http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                var detail = await response.Content.ReadAsStringAsync(ct);
                _logger.LogWarning("PUT /metadata/artwork/{VariantId}/preferred returned {Status}: {Detail}",
                    variantId, (int)response.StatusCode, detail);
                LastError = $"HTTP {(int)response.StatusCode}: {detail}";
            }

            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PUT /metadata/artwork/{VariantId}/preferred failed", variantId);
            LastError = ex.Message;
            return false;
        }
    }

    public async Task<bool> DeleteArtworkAsync(Guid variantId, CancellationToken ct = default)
    {
        try
        {
            var response = await _http.DeleteAsync($"/metadata/artwork/{variantId}", ct);
            if (!response.IsSuccessStatusCode)
            {
                var detail = await response.Content.ReadAsStringAsync(ct);
                _logger.LogWarning("DELETE /metadata/artwork/{VariantId} returned {Status}: {Detail}",
                    variantId, (int)response.StatusCode, detail);
                LastError = $"HTTP {(int)response.StatusCode}: {detail}";
            }

            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "DELETE /metadata/artwork/{VariantId} failed", variantId);
            LastError = ex.Message;
            return false;
        }
    }

    // -- GET /collections/parents -----------------------------------------------------

    public async Task<List<CollectionViewModel>> GetParentCollectionsAsync(CancellationToken ct = default)
    {
        try
        {
            var raw = await _http.GetFromJsonAsync<List<MediaEngine.Contracts.Collections.ParentCollectionDto>>(
                "/collections/parents", ct);
            return raw?.Select(MapParentCollection).ToList() ?? [];
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GET /collections/parents failed");
            LastError = ex.Message;
            return [];
        }
    }

    // -- GET /collections/{id}/children -----------------------------------------------

    public async Task<List<CollectionViewModel>> GetChildCollectionsAsync(
        Guid parentCollectionId, CancellationToken ct = default)
    {
        try
        {
            var raw = await _http.GetFromJsonAsync<List<CollectionChildSummary>>(
                $"/collections/{parentCollectionId}/children", ct);
            return raw?.Select(child => CollectionViewModel.FromApiDto(
                child.id,
                null,
                child.createdAt,
                [],
                child.displayName,
                child.parentCollectionId,
                null,
                0)).ToList() ?? [];
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GET /collections/{ParentCollectionId}/children failed", parentCollectionId);
            LastError = ex.Message;
            return [];
        }
    }

    // -- GET /collections/{id}/parent -------------------------------------------------

    public async Task<CollectionViewModel?> GetParentCollectionAsync(
        Guid collectionId, CancellationToken ct = default)
    {
        try
        {
            var resp = await _http.GetAsync($"/collections/{collectionId}/parent", ct);
            if (resp.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }

            resp.EnsureSuccessStatusCode();
            var raw = await resp.Content.ReadFromJsonAsync<CollectionParentResponse>(cancellationToken: ct);
            return raw?.parentCollection is { } parent
                ? CollectionViewModel.FromApiDto(
                    parent.id,
                    null,
                    parent.createdAt,
                    [],
                    parent.displayName,
                    null,
                    null,
                    0)
                : null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GET /collections/{CollectionId}/parent failed", collectionId);
            LastError = ex.Message;
            return null;
        }
    }

    /// <summary>
    /// Most recent error message from the last failed API call.
    /// Useful for surfacing diagnostic details in the UI.
    /// Cleared on next successful call.
    /// </summary>

}
