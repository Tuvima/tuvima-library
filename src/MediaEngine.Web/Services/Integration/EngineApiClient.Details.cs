using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using MediaEngine.Contracts.Details;
using MediaEngine.Contracts.Display;
using MediaEngine.Contracts.Items;
using MediaEngine.Contracts.Metadata;
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
        SequenceWindowQuery? sequence = null,
        CancellationToken ct = default) =>
        // Background lookups (playback, sequence panels) must not leave a stale error in the shared failure state.
        (await LoadDetailPageCoreAsync(entityType, id, context, containerId, sequence, recordFailure: false, ct).ConfigureAwait(false)).Detail;

    public Task<DetailPageLoadResult> LoadDetailPageAsync(
        DetailEntityType entityType,
        Guid id,
        DetailPresentationContext context = DetailPresentationContext.Default,
        string? containerId = null,
        SequenceWindowQuery? sequence = null,
        CancellationToken ct = default) =>
        LoadDetailPageCoreAsync(entityType, id, context, containerId, sequence, recordFailure: true, ct);

    private async Task<DetailPageLoadResult> LoadDetailPageCoreAsync(
        DetailEntityType entityType,
        Guid id,
        DetailPresentationContext context,
        string? containerId,
        SequenceWindowQuery? sequence,
        bool recordFailure,
        CancellationToken ct)
    {
        const string endpoint = "GET /api/v1/details/{entityType}/{id}";
        try
        {
            var entity = Uri.EscapeDataString(entityType.ToString().ToLowerInvariant());
            var ctx = Uri.EscapeDataString(context.ToString().ToLowerInvariant());
            var query = new List<string> { $"context={ctx}" };
            if (!string.IsNullOrWhiteSpace(containerId))
            {
                query.Add($"containerId={Uri.EscapeDataString(containerId)}");
            }

            if (sequence is not null)
            {
                AddQuery(query, "sequenceGroup", sequence.Group);
                query.Add($"sequenceOffset={sequence.Offset}");
                query.Add($"sequenceLimit={sequence.Limit}");
                AddQuery(query, "sequenceCurrent", sequence.Current?.ToString("D"));
                if (sequence.Only)
                {
                    query.Add("sequenceOnly=true");
                }
            }

            using var response = await _http.GetAsync(
                $"/api/v1/details/{entity}/{id:D}?{string.Join('&', query)}", ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var failure = response.StatusCode switch
                {
                    HttpStatusCode.NotFound or HttpStatusCode.BadRequest => DetailPageLoadFailure.NotFound,
                    HttpStatusCode.Forbidden => DetailPageLoadFailure.NotAllowed,
                    // 401 is an expired or invalid sign-in/service credential, not a refusal; Retry can recover it.
                    _ => DetailPageLoadFailure.Unreachable,
                };
                if (recordFailure)
                {
                    await RecordHttpFailureAsync(endpoint, response, ct, logAsWarning: failure == DetailPageLoadFailure.Unreachable);
                }
                else
                {
                    _logger.LogWarning("GET /api/details/{EntityType}/{Id} returned {StatusCode}", entityType, id, (int)response.StatusCode);
                }

                return DetailPageLoadResult.Failed(failure);
            }

            var detail = await response.Content.ReadFromJsonAsync<DetailPageViewModel>(cancellationToken: ct).ConfigureAwait(false);
            if (recordFailure)
            {
                ClearFailure(endpoint);
            }

            return detail is null
                ? DetailPageLoadResult.Failed(DetailPageLoadFailure.NotFound)
                : DetailPageLoadResult.Loaded(NormalizeDetailArtwork(detail));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The caller superseded this load; it ignores the result.
            return DetailPageLoadResult.Failed(DetailPageLoadFailure.NotFound);
        }
        catch (Exception ex)
        {
            // Includes timeouts (TaskCanceledException not caused by ct) and connection failures.
            _logger.LogWarning(ex, "GET /api/details/{EntityType}/{Id} failed", entityType, id);
            if (recordFailure)
            {
                _failureState.RecordExceptionFailure(endpoint, ex);
            }

            return DetailPageLoadResult.Failed(DetailPageLoadFailure.Unreachable);
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

    public async Task<MediaEngine.Contracts.Items.ItemExplicitResponse?> GetItemExplicitAsync(Guid entityId, CancellationToken ct = default)
    {
        try
        {
            return await _http.GetFromJsonAsync<MediaEngine.Contracts.Items.ItemExplicitResponse>($"/library/items/{entityId}/explicit", ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException or TaskCanceledException)
        {
            _logger.LogWarning(ex, "GET /library/items/{EntityId}/explicit failed", entityId);
            return null;
        }
    }

    public Task<bool> SaveItemExplicitAsync(Guid entityId, bool isExplicit, CancellationToken ct = default) =>
        PutAsync(
            "PUT /library/items/{id}/explicit",
            $"/library/items/{entityId}/explicit",
            new MediaEngine.Contracts.Items.ItemExplicitRequest { IsExplicit = isExplicit },
            ct: ct);

    public async Task<MediaEngine.Contracts.Items.VideoOrderResponse?> GetVideoOrderAsync(Guid entityId, CancellationToken ct = default)
    {
        try
        {
            return await _http.GetFromJsonAsync<MediaEngine.Contracts.Items.VideoOrderResponse>($"/library/items/{entityId}/video-order", ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException or TaskCanceledException)
        {
            _logger.LogWarning(ex, "GET /library/items/{EntityId}/video-order failed", entityId);
            return null;
        }
    }

    public Task<bool> SaveVideoOrderAsync(Guid entityId, Guid parentId, IReadOnlyList<Guid> orderedIds, CancellationToken ct = default) =>
        PutAsync(
            "PUT /library/items/{id}/video-order",
            $"/library/items/{entityId}/video-order",
            new MediaEngine.Contracts.Items.VideoOrderRequest { ParentId = parentId, OrderedIds = orderedIds.ToList() },
            ct: ct);

    public Task<MediaEngine.Contracts.Items.VideoRenameResponse?> RenameVideoFilesToOrderAsync(Guid entityId, Guid parentId, CancellationToken ct = default) =>
        PostAsync<MediaEngine.Contracts.Items.VideoRenameRequest, MediaEngine.Contracts.Items.VideoRenameResponse>(
            "POST /library/items/{id}/video-rename",
            $"/library/items/{entityId}/video-rename",
            new MediaEngine.Contracts.Items.VideoRenameRequest { ParentId = parentId },
            ct: ct);

    public Task<MediaEngine.Contracts.Items.VideoSeriesStateResponse?> GetVideoSeriesAsync(Guid entityId, CancellationToken ct = default) =>
        GetAsync<MediaEngine.Contracts.Items.VideoSeriesStateResponse>(
            "GET /library/items/{id}/video-series", $"/library/items/{entityId}/video-series", ct: ct);

    public Task<bool> PromoteVideoToSeriesAsync(Guid entityId, Guid? seriesId, string? newSeriesName, CancellationToken ct = default) =>
        PutAsync(
            "PUT /library/items/{id}/video-series",
            $"/library/items/{entityId}/video-series",
            new MediaEngine.Contracts.Items.VideoSeriesPromoteRequest { ParentId = seriesId, NewSeriesName = newSeriesName },
            ct: ct);

    public Task<MediaEngine.Contracts.Items.VideoIdentifyStateResponse?> GetVideoIdentifyStateAsync(Guid entityId, CancellationToken ct = default) =>
        GetAsync<MediaEngine.Contracts.Items.VideoIdentifyStateResponse>(
            "GET /library/items/{id}/video-identify", $"/library/items/{entityId}/video-identify", ct: ct);

    public Task<MediaEngine.Contracts.Items.VideoIdentifyResponse?> IdentifyVideoFromLinkAsync(Guid entityId, string link, CancellationToken ct = default) =>
        PostAsync<MediaEngine.Contracts.Items.VideoIdentifyRequest, MediaEngine.Contracts.Items.VideoIdentifyResponse>(
            "POST /library/items/{id}/video-identify",
            $"/library/items/{entityId}/video-identify",
            new MediaEngine.Contracts.Items.VideoIdentifyRequest { Link = link },
            ct: ct);

    public Task<bool> RemoveVideoFromSeriesAsync(Guid entityId, CancellationToken ct = default) =>
        PostAsync("POST /library/items/{id}/video-series/remove", $"/library/items/{entityId}/video-series/remove", new { }, ct: ct);

    public Task<RecycleBinSummaryDto?> GetRecycleBinSummaryAsync(string? mediaType = null, CancellationToken ct = default) =>
        GetAsync<RecycleBinSummaryDto>(
            "GET /library/items/recycle-bin/summary",
            string.IsNullOrWhiteSpace(mediaType)
                ? "/library/items/recycle-bin/summary"
                : $"/library/items/recycle-bin/summary?media_type={Uri.EscapeDataString(mediaType)}",
            ct: ct);

    public Task<bool> AskToKeepFromRecycleBinAsync(Guid id, CancellationToken ct = default) =>
        PostAsync("POST /library/items/recycle-bin/{id}/ask-to-keep", $"/library/items/recycle-bin/{id}/ask-to-keep", new { }, ct: ct);

    public Task<bool> DeleteFromRecycleBinNowAsync(Guid id, CancellationToken ct = default) =>
        DeleteAsync("DELETE /library/items/recycle-bin/{id}", $"/library/items/recycle-bin/{id}", ct: ct);

    public Task<bool> RestoreFromRecycleBinAsync(Guid id, CancellationToken ct = default) =>
        PostAsync("POST /library/items/recycle-bin/{id}/restore", $"/library/items/recycle-bin/{id}/restore", new { }, ct: ct);

    public Task<bool> EmptyRecycleBinAsync(CancellationToken ct = default) =>
        PostAsync("POST /library/items/recycle-bin/empty", "/library/items/recycle-bin/empty", new { }, ct: ct);

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

    public Task<SeriesPlacementDto?> GetSeriesPlacementAsync(
        Guid entityId, string? query = null, CancellationToken ct = default) =>
        GetAsync<SeriesPlacementDto>("GET /metadata/{entityId}/series-placement",
            $"/metadata/{entityId}/series-placement",
            string.IsNullOrWhiteSpace(query) ? null : new Dictionary<string, string?> { ["q"] = query.Trim() },
            ct: ct);

    public Task<SeriesPlacementDto?> SaveSeriesPlacementAsync(
        Guid entityId, Guid? collectionId, bool locked, CancellationToken ct = default) =>
        PutAsync<SeriesPlacementChoiceRequestDto, SeriesPlacementDto>("PUT /metadata/{entityId}/series-placement",
            $"/metadata/{entityId}/series-placement",
            new SeriesPlacementChoiceRequestDto { CollectionId = collectionId, Locked = locked },
            ct: ct);

    public Task<MediaEditorWorkVersionSelectorDto?> GetMediaEditorWorkVersionsAsync(
        Guid entityId, CancellationToken ct = default) =>
        GetAsync<MediaEditorWorkVersionSelectorDto>("GET /metadata/{entityId}/work-versions",
            $"/metadata/{entityId}/work-versions", ct: ct);

    public async Task<MediaEditorOwnedChildSearchDto?> GetMediaEditorOwnedChildrenAsync(
        Guid entityId, string? query = null, int page = 1, int pageSize = 50,
        int? season = null, int? disc = null, int? volume = null,
        string? matchStatus = null, string? fileStatus = null, CancellationToken ct = default)
    {
        try
        {
            var parts = new List<string> { $"page={Math.Max(1, page)}", $"pageSize={Math.Clamp(pageSize, 1, 100)}" };
            if (!string.IsNullOrWhiteSpace(query))
            {
                parts.Add($"q={Uri.EscapeDataString(query.Trim())}");
            }
            if (season.HasValue)
            {
                parts.Add($"season={season.Value}");
            }
            if (disc.HasValue)
            {
                parts.Add($"disc={disc.Value}");
            }
            if (volume.HasValue)
            {
                parts.Add($"volume={volume.Value}");
            }
            if (!string.IsNullOrWhiteSpace(matchStatus))
            {
                parts.Add($"matchStatus={Uri.EscapeDataString(matchStatus)}");
            }
            if (!string.IsNullOrWhiteSpace(fileStatus))
            {
                parts.Add($"fileStatus={Uri.EscapeDataString(fileStatus)}");
            }
            return await _http.GetFromJsonAsync<MediaEditorOwnedChildSearchDto>($"/metadata/{entityId}/owned-children?{string.Join("&", parts)}", ct);
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GET /metadata/{EntityId}/owned-children failed", entityId);
            return null;
        }
    }

    public async Task<MediaEditorOwnedChildSelectionSnapshotDto?> GetMediaEditorOwnedChildSelectionSnapshotAsync(
        Guid entityId, string? query = null, int? season = null, int? disc = null, int? volume = null,
        string? matchStatus = null, string? fileStatus = null, CancellationToken ct = default)
    {
        try
        {
            LastError = null;
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(query))
            {
                parts.Add($"q={Uri.EscapeDataString(query.Trim())}");
            }
            if (season.HasValue)
            {
                parts.Add($"season={season.Value}");
            }
            if (disc.HasValue)
            {
                parts.Add($"disc={disc.Value}");
            }
            if (volume.HasValue)
            {
                parts.Add($"volume={volume.Value}");
            }
            if (!string.IsNullOrWhiteSpace(matchStatus))
            {
                parts.Add($"matchStatus={Uri.EscapeDataString(matchStatus)}");
            }
            if (!string.IsNullOrWhiteSpace(fileStatus))
            {
                parts.Add($"fileStatus={Uri.EscapeDataString(fileStatus)}");
            }
            var path = $"/metadata/{entityId}/owned-children/selection-snapshot";
            if (parts.Count > 0)
            {
                path += $"?{string.Join("&", parts)}";
            }
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            using var response = await _http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                LastError = await ReadMediaEditorPairingErrorAsync(response, ct);
                return null;
            }
            return await response.Content.ReadFromJsonAsync<MediaEditorOwnedChildSelectionSnapshotDto>(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return null; }
        catch (Exception ex)
        {
            LastError = ex.Message;
            _logger.LogWarning(ex, "GET /metadata/{EntityId}/owned-children/selection-snapshot failed", entityId);
            return null;
        }
    }

    private async Task<HttpResponseMessage> PostMediaEditorRequestAsync<T>(
        string path, T request, CancellationToken ct)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(request),
        };
        return await _http.SendAsync(message, ct);
    }

    public async Task<MediaEditorPairingPreviewDto?> PreviewMediaEditorPairingAsync(
        Guid entityId, MediaEditorPairingPreviewRequestDto request, CancellationToken ct = default)
    {
        try
        {
            LastError = null;
            using var response = await PostMediaEditorRequestAsync($"/metadata/{entityId}/pairing-preview", request, ct);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                var detail = body;
                try
                {
                    using var problem = JsonDocument.Parse(body);
                    if (problem.RootElement.TryGetProperty("detail", out var property))
                    {
                        detail = property.GetString() ?? body;
                    }
                }
                catch (JsonException) { }
                LastError = string.IsNullOrWhiteSpace(detail)
                    ? $"Pairing preview failed (HTTP {(int)response.StatusCode})."
                    : detail;
                return null;
            }

            return await response.Content.ReadFromJsonAsync<MediaEditorPairingPreviewDto>(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return null; }
        catch (Exception ex)
        {
            LastError = ex.Message;
            _logger.LogWarning(ex, "POST /metadata/{EntityId}/pairing-preview failed", entityId);
            return null;
        }
    }

    public async Task<MediaEditorPairingChildSearchDto?> SearchMediaEditorPairingChildrenAsync(
        Guid entityId, MediaEditorPairingChildSearchRequestDto request, CancellationToken ct = default)
    {
        try
        {
            LastError = null;
            using var response = await PostMediaEditorRequestAsync($"/metadata/{entityId}/pairing-children", request, ct);
            if (!response.IsSuccessStatusCode)
            {
                LastError = await ReadMediaEditorPairingErrorAsync(response, ct);
                return null;
            }
            return await response.Content.ReadFromJsonAsync<MediaEditorPairingChildSearchDto>(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return null; }
        catch (Exception ex)
        {
            LastError = ex.Message;
            _logger.LogWarning(ex, "POST /metadata/{EntityId}/pairing-children failed", entityId);
            return null;
        }
    }

    private static async Task<string> ReadMediaEditorPairingErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct);
        try
        {
            using var json = JsonDocument.Parse(body);
            if (json.RootElement.TryGetProperty("detail", out var detail)
                && detail.ValueKind == JsonValueKind.String)
            {
                return detail.GetString() ?? response.ReasonPhrase ?? "The episode search failed.";
            }
        }
        catch (JsonException) { }
        return string.IsNullOrWhiteSpace(body) ? response.ReasonPhrase ?? "The episode search failed." : body;
    }

    public async Task<MediaEditorFileCoverageDto?> GetMediaEditorFileCoverageAsync(
        Guid entityId, Guid assetId, CancellationToken ct = default)
    {
        try
        {
            LastError = null;
            using var response = await _http.GetAsync($"/metadata/{entityId}/file-coverage?assetId={assetId}", ct);
            if (!response.IsSuccessStatusCode)
            {
                // A file that is not a TV episode answers 404: the picker simply stays hidden.
                LastError = response.StatusCode == HttpStatusCode.NotFound
                    ? null
                    : await ReadMediaEditorPairingErrorAsync(response, ct);
                return null;
            }
            return await response.Content.ReadFromJsonAsync<MediaEditorFileCoverageDto>(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return null; }
        catch (Exception ex)
        {
            LastError = ex.Message;
            _logger.LogWarning(ex, "GET /metadata/{EntityId}/file-coverage failed", entityId);
            return null;
        }
    }

    public async Task<MediaEditorFileCoverageDto?> SaveMediaEditorFileCoverageAsync(
        Guid entityId, MediaEditorFileCoverageSaveRequestDto request, CancellationToken ct = default)
    {
        try
        {
            LastError = null;
            using var message = new HttpRequestMessage(HttpMethod.Put, $"/metadata/{entityId}/file-coverage")
            {
                Content = JsonContent.Create(request),
            };
            using var response = await _http.SendAsync(message, ct);
            if (!response.IsSuccessStatusCode)
            {
                LastError = await ReadMediaEditorPairingErrorAsync(response, ct);
                return null;
            }
            return await response.Content.ReadFromJsonAsync<MediaEditorFileCoverageDto>(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return null; }
        catch (Exception ex)
        {
            LastError = ex.Message;
            _logger.LogWarning(ex, "PUT /metadata/{EntityId}/file-coverage failed", entityId);
            return null;
        }
    }

    public async Task<MediaEditorPairingArtworkPreviewDto?> PreviewMediaEditorPairingArtworkAsync(
        Guid entityId, MediaEditorPairingArtworkPreviewRequestDto request, CancellationToken ct = default)
    {
        try
        {
            LastError = null;
            using var response = await PostMediaEditorRequestAsync($"/metadata/{entityId}/pairing-artwork-preview", request, ct);
            if (!response.IsSuccessStatusCode)
            {
                LastError = await ReadMediaEditorPairingErrorAsync(response, ct);
                return null;
            }
            return await response.Content.ReadFromJsonAsync<MediaEditorPairingArtworkPreviewDto>(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return null; }
        catch (Exception ex)
        {
            LastError = ex.Message;
            _logger.LogWarning(ex, "POST /metadata/{EntityId}/pairing-artwork-preview failed", entityId);
            return null;
        }
    }

    public async Task<MediaEditorPairingSharedArtworkPreviewDto?> PreviewMediaEditorPairingSharedArtworkAsync(
        Guid entityId, MediaEditorPairingSharedArtworkPreviewRequestDto request, CancellationToken ct = default)
    {
        try
        {
            LastError = null;
            using var response = await PostMediaEditorRequestAsync(
                $"/metadata/{entityId}/pairing-shared-artwork-preview", request, ct);
            if (!response.IsSuccessStatusCode)
            {
                LastError = await ReadMediaEditorPairingErrorAsync(response, ct);
                return null;
            }
            return await response.Content.ReadFromJsonAsync<MediaEditorPairingSharedArtworkPreviewDto>(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return null; }
        catch (Exception ex)
        {
            LastError = ex.Message;
            _logger.LogWarning(ex, "POST /metadata/{EntityId}/pairing-shared-artwork-preview failed", entityId);
            return null;
        }
    }

    public async Task<MediaEditorEditionCoverPreviewDto?> PreviewMediaEditorEditionCoverAsync(
        Guid entityId, MediaEditorEditionCoverPreviewRequestDto request, CancellationToken ct = default)
    {
        try
        {
            LastError = null;
            using var response = await PostMediaEditorRequestAsync(
                $"/metadata/{entityId}/edition-cover-preview", request, ct);
            if (!response.IsSuccessStatusCode)
            {
                LastError = await ReadMediaEditorPairingErrorAsync(response, ct);
                return null;
            }
            return await response.Content.ReadFromJsonAsync<MediaEditorEditionCoverPreviewDto>(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return null; }
        catch (Exception ex)
        {
            LastError = ex.Message;
            _logger.LogWarning(ex, "POST /metadata/{EntityId}/edition-cover-preview failed", entityId);
            return null;
        }
    }

    public async Task<MediaEditorEditionCoverSaveResultDto?> SaveMediaEditorEditionCoverAsync(
        Guid entityId, MediaEditorEditionCoverSaveRequestDto request, CancellationToken ct = default)
    {
        try
        {
            LastError = null;
            using var response = await PostMediaEditorRequestAsync(
                $"/metadata/{entityId}/edition-cover", request, ct);
            if (!response.IsSuccessStatusCode)
            {
                LastError = await ReadMediaEditorPairingErrorAsync(response, ct);
                return null;
            }
            return await response.Content.ReadFromJsonAsync<MediaEditorEditionCoverSaveResultDto>(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return null; }
        catch (Exception ex)
        {
            LastError = ex.Message;
            _logger.LogWarning(ex, "POST /metadata/{EntityId}/edition-cover failed", entityId);
            return null;
        }
    }

    public async Task<MediaEditorPairingSaveResultDto?> SaveMediaEditorPairingAsync(
        Guid entityId, MediaEditorPairingSaveRequestDto request, CancellationToken ct = default)
    {
        try
        {
            LastError = null;
            using var response = await PostMediaEditorRequestAsync($"/metadata/{entityId}/pairing-save", request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.Conflict)
            {
                try
                {
                    var receipt = JsonSerializer.Deserialize<MediaEditorPairingSaveResultDto>(body,
                        MediaEngine.Domain.Services.MediaEngineJson.Web);
                    if (!string.IsNullOrWhiteSpace(receipt?.Outcome))
                    {
                        return receipt;
                    }
                }
                catch (JsonException) { }
            }

            var detail = body;
            try
            {
                using var problem = JsonDocument.Parse(body);
                if (problem.RootElement.TryGetProperty("detail", out var property))
                {
                    detail = property.GetString() ?? body;
                }
            }
            catch (JsonException) { }
            LastError = string.IsNullOrWhiteSpace(detail)
                ? $"Pairing save failed (HTTP {(int)response.StatusCode})."
                : detail;
            return null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return null; }
        catch (Exception ex)
        {
            LastError = ex.Message;
            _logger.LogWarning(ex, "POST /metadata/{EntityId}/pairing-save failed", entityId);
            return null;
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
