using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using MediaEngine.Contracts.Details;
using MediaEngine.Contracts.Display;
using MediaEngine.Contracts.Paging;
using MediaEngine.Contracts.Playback;
using MediaEngine.Contracts.Profiles;
using MediaEngine.Contracts.Settings;
using MediaEngine.Contracts.System;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Branding;
using MediaEngine.Web.Services.Integration.Clients;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Web.Services.Integration;

/// <summary>
/// Strongly-typed HTTP client for the Engine API.
/// Registered as a circuit-scoped client in Program.cs so profile-bound View
/// assertions remain isolated to the active Dashboard session.
/// </summary>
public sealed partial class EngineApiClient : IEngineApiClient, IDisposable
{
    private readonly HttpClient _http;
    private readonly IActiveProfileAccessor? _progressProfile;
    private readonly ILogger<EngineApiClient> _logger;
    private readonly StreamingServiceLogoResolver _streamingServiceLogos;
    private readonly EngineApiFailureState _failureState;
    private readonly SystemClient _systemClient;
    private readonly ProviderClient _providerClient;

    public EngineApiClient(
        HttpClient http,
        ILogger<EngineApiClient> logger,
        StreamingServiceLogoResolver? streamingServiceLogos = null,
        ILoggerFactory? loggerFactory = null,
        EngineApiFailureState? failureState = null,
        IActiveProfileAccessor? progressProfile = null)
    {
        _http = http;
        _progressProfile = progressProfile;
        _logger = logger;
        _streamingServiceLogos = streamingServiceLogos ?? new StreamingServiceLogoResolver();
        _failureState = failureState ?? new EngineApiFailureState();
        var factory = loggerFactory ?? NullLoggerFactory.Instance;
        _systemClient = new SystemClient(_http, factory.CreateLogger<SystemClient>(), _failureState);
        _providerClient = new ProviderClient(_http, factory.CreateLogger<ProviderClient>(), _failureState);
    }

    public string ToAbsoluteEngineUrl(string value) => AbsoluteUrl(value);

    public void Dispose() => _http.Dispose();

    public async Task<IReadOnlyList<PluginSummaryResponse>> GetPluginsAsync(CancellationToken ct = default)
    {
        try
        {
            return await _http.GetFromJsonAsync<List<PluginSummaryResponse>>("/plugins", ct) ?? [];
        }
        catch (OperationCanceledException) { return []; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GET /plugins failed");
            return [];
        }
    }

    public async Task<ApprovedPluginCatalogDto?> GetApprovedPluginCatalogAsync(CancellationToken ct = default)
    {
        try
        {
            return await _http.GetFromJsonAsync<ApprovedPluginCatalogDto>("/plugins/approved", ct);
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GET /plugins/approved failed");
            return null;
        }
    }

    public async Task<bool> SetPluginEnabledAsync(string pluginId, bool enabled, CancellationToken ct = default)
    {
        try
        {
            var encoded = Uri.EscapeDataString(pluginId);
            using var response = await _http.PostAsJsonAsync($"/plugins/{encoded}/{(enabled ? "enable" : "disable")}", new { }, ct);
            return response.IsSuccessStatusCode;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "POST /plugins/{PluginId}/enable|disable failed", pluginId);
            return false;
        }
    }

    public async Task<bool> SavePluginSettingsAsync(string pluginId, Dictionary<string, JsonElement> settings, CancellationToken ct = default)
    {
        try
        {
            var encoded = Uri.EscapeDataString(pluginId);
            using var response = await _http.PutAsJsonAsync($"/plugins/{encoded}/settings", settings, ct);
            return response.IsSuccessStatusCode;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PUT /plugins/{PluginId}/settings failed", pluginId);
            return false;
        }
    }

    public async Task<string?> GetPluginManifestJsonAsync(string pluginId, CancellationToken ct = default)
    {
        try
        {
            var encoded = Uri.EscapeDataString(pluginId);
            var result = await _http.GetFromJsonAsync<PluginManifestJsonResponse>($"/plugins/{encoded}/manifest", ct);
            return result?.json;
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GET /plugins/{PluginId}/manifest failed", pluginId);
            return null;
        }
    }

    public async Task<bool> SavePluginManifestJsonAsync(string pluginId, string json, CancellationToken ct = default)
    {
        try
        {
            var encoded = Uri.EscapeDataString(pluginId);
            using var response = await _http.PutAsJsonAsync($"/plugins/{encoded}/manifest", new { json }, ct);
            return response.IsSuccessStatusCode;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PUT /plugins/{PluginId}/manifest failed", pluginId);
            return false;
        }
    }

    public async Task<bool> DeletePluginAsync(string pluginId, CancellationToken ct = default)
    {
        try
        {
            var encoded = Uri.EscapeDataString(pluginId);
            using var response = await _http.DeleteAsync($"/plugins/{encoded}", ct);
            return response.IsSuccessStatusCode;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "DELETE /plugins/{PluginId} failed", pluginId);
            return false;
        }
    }

    public async Task<PluginHealthResponse?> CheckPluginHealthAsync(string pluginId, CancellationToken ct = default)
    {
        try
        {
            var encoded = Uri.EscapeDataString(pluginId);
            using var response = await _http.PostAsJsonAsync($"/plugins/{encoded}/health", new { }, ct);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            return await response.Content.ReadFromJsonAsync<PluginHealthResponse>(cancellationToken: ct);
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "POST /plugins/{PluginId}/health failed", pluginId);
            return null;
        }
    }

    public async Task<IReadOnlyList<OperationDto>> GetPluginJobsAsync(string pluginId, CancellationToken ct = default)
    {
        try
        {
            var encoded = Uri.EscapeDataString(pluginId);
            return await _http.GetFromJsonAsync<List<OperationDto>>($"/plugins/{encoded}/jobs", ct) ?? [];
        }
        catch (OperationCanceledException) { return []; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GET /plugins/{PluginId}/jobs failed", pluginId);
            return [];
        }
    }

    public async Task<IReadOnlyList<PluginJobSnapshot>> RunPluginSegmentDetectionJobsAsync(CancellationToken ct = default)
    {
        try
        {
            using var response = await _http.PostAsJsonAsync("/plugins/jobs/segment-detection/run", new { }, ct);
            if (!response.IsSuccessStatusCode)
            {
                return [];
            }

            return await response.Content.ReadFromJsonAsync<List<PluginJobSnapshot>>(cancellationToken: ct) ?? [];
        }
        catch (OperationCanceledException) { return []; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "POST /plugins/jobs/segment-detection/run failed");
            return [];
        }
    }

    public async Task<SystemStatusViewModel?> GetSystemStatusAsync(CancellationToken ct = default)
        => await _systemClient.GetSystemStatusAsync(ct);

    public async Task<StartupReadinessResponse?> GetStartupReadinessAsync(CancellationToken ct = default)
        => await _systemClient.GetStartupReadinessAsync(ct);

    public async Task<IReadOnlyList<SystemActivityOperationViewModel>> GetSystemActivityOperationsAsync(CancellationToken ct = default)
    {
        try
        {
            var response = await _http.GetFromJsonAsync<List<MediaEngine.Contracts.System.SystemActivityOperationDto>>(
                "/system/activity-status",
                ct);
            return response?.Select(SystemActivityOperationViewModel.FromContract).ToList() ?? [];
        }
        catch (OperationCanceledException)
        {
            return [];
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "GET /system/activity-status failed");
            return [];
        }
    }

    public async Task<UniversalSearchResponseDto?> GetUniversalSearchAsync(
        string query,
        int? limit = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Trim().Length < 2)
        {
            return new UniversalSearchResponseDto(string.Empty, null, [], 0);
        }

        try
        {
            var values = new List<string>
            {
                $"q={Uri.EscapeDataString(query.Trim())}",
            };
            if (limit is > 0)
            {
                values.Add($"limit={Math.Clamp(limit.Value, 6, 80)}");
            }

            LastError = null;
            return await _http.GetFromJsonAsync<UniversalSearchResponseDto>(
                $"/api/v1/display/search?{string.Join("&", values)}",
                ct);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            _logger.LogWarning(ex, "GET /api/v1/display/search failed");
            return null;
        }
    }

    public async Task<TasteProfileBuildResponse?> GetTasteProfileAsync(Guid id, CancellationToken ct = default)
    {
        try
        {
            return await _http.GetFromJsonAsync<TasteProfileBuildResponse>($"/profiles/{id}/taste", ct);
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GET /profiles/{Id}/taste failed", id);
            return null;
        }
    }

    // -- Review queue (/review) -------------------------------------------

    public async Task<List<ReviewItemViewModel>> GetPendingReviewsAsync(
        int limit = 50, CancellationToken ct = default)
    {
        const string endpoint = "GET /review/pending";
        try
        {
            var response = await _http.GetAsync($"/review/pending?limit={limit}", ct);
            if (!response.IsSuccessStatusCode)
            {
                await RecordHttpFailureAsync(endpoint, response, ct);
                return [];
            }

            var raw = await response.Content.ReadFromJsonAsync<List<MediaEngine.Contracts.Review.ReviewItemDto>>(
                cancellationToken: ct);
            ClearFailure(endpoint);
            return raw?.Select(item => item.ToViewModel()).ToList() ?? [];
        }
        catch (OperationCanceledException) { return []; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GET /review/pending failed");
            RecordExceptionFailure(endpoint, ex);
            return [];
        }
    }

    public async Task<ReviewItemViewModel?> GetReviewItemAsync(
        Guid id, CancellationToken ct = default)
    {
        try
        {
            var item = await _http.GetFromJsonAsync<MediaEngine.Contracts.Review.ReviewItemDto>(
                $"/review/{id}", ct);
            return item?.ToViewModel();
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GET /review/{Id} failed", id);
            return null;
        }
    }

    public async Task<int> GetReviewCountAsync(CancellationToken ct = default)
    {
        const string endpoint = "GET /review/count";
        try
        {
            var response = await _http.GetAsync("/review/count", ct);
            if (!response.IsSuccessStatusCode)
            {
                await RecordHttpFailureAsync(endpoint, response, ct, logAsWarning: false);
                return 0;
            }

            var raw = await response.Content.ReadFromJsonAsync<MediaEngine.Contracts.Review.ReviewCountResponse>(
                cancellationToken: ct);
            ClearFailure(endpoint);
            return raw?.PendingCount ?? 0;
        }
        catch (OperationCanceledException) { return 0; }
        catch (Exception ex)
        {
            // Debug level: this is polled for the badge count.
            _logger.LogDebug(ex, "GET /review/count failed");
            RecordExceptionFailure(endpoint, ex, logAsWarning: false);
            return 0;
        }
    }

    public async Task<bool> ResolveReviewItemAsync(
        Guid id, ReviewResolveRequestDto request, CancellationToken ct = default)
    {
        try
        {
            var resp = await _http.PostAsJsonAsync($"/review/{id}/resolve", request, ct);
            return resp.IsSuccessStatusCode;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "POST /review/{Id}/resolve failed", id);
            return false;
        }
    }

    public async Task<bool> DismissReviewItemAsync(Guid id, CancellationToken ct = default)
    {
        try
        {
            var resp = await _http.PostAsJsonAsync($"/review/{id}/dismiss", new { }, ct);
            return resp.IsSuccessStatusCode;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "POST /review/{Id}/dismiss failed", id);
            return false;
        }
    }

    public async Task<bool> SkipUniverseAsync(Guid id, CancellationToken ct = default)
    {
        try
        {
            var resp = await _http.PostAsJsonAsync($"/review/{id}/skip-universe", new { }, ct);
            return resp.IsSuccessStatusCode;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "POST /review/{Id}/skip-universe failed", id);
            return false;
        }
    }

    public async Task<bool> ReclassifyMediaTypeAsync(
        Guid entityId, string mediaType, CancellationToken ct = default)
    {
        try
        {
            var resp = await _http.PostAsJsonAsync(
                $"/metadata/{entityId}/reclassify",
                new { media_type = mediaType }, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var error = await resp.Content.ReadAsStringAsync(ct);
                LastError = string.IsNullOrWhiteSpace(error)
                    ? $"POST /metadata/.../reclassify failed: {resp.StatusCode}"
                    : $"POST /metadata/.../reclassify failed: {resp.StatusCode} - {error}";
                return false;
            }

            return resp.IsSuccessStatusCode;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "POST /metadata/{EntityId}/reclassify failed", entityId);
            return false;
        }
    }

    // -- Persons by Collection (/persons/by-collection) ---------------------------------

    // -- Universe Graph (Chronicle Explorer) -----------------------------------

    public async Task<UniverseGraphResponse?> GetUniverseGraphAsync(
        string qid,
        int? timelineYear = null,
        string? types = null,
        string? center = null,
        int? depth = null,
        bool includeSupplementalLore = false,
        CancellationToken ct = default)
    {
        try
        {
            var url = $"universe/{Uri.EscapeDataString(qid)}/graph";
            var queryParams = new List<string>();
            if (timelineYear.HasValue)
            {
                queryParams.Add($"timeline_year={timelineYear.Value}");
            }

            if (!string.IsNullOrWhiteSpace(types))
            {
                queryParams.Add($"types={Uri.EscapeDataString(types)}");
            }

            if (!string.IsNullOrWhiteSpace(center))
            {
                queryParams.Add($"center={Uri.EscapeDataString(center)}");
            }

            if (depth.HasValue)
            {
                queryParams.Add($"depth={depth.Value}");
            }

            if (includeSupplementalLore)
            {
                queryParams.Add("include_supplemental_lore=true");
            }

            if (queryParams.Count > 0)
            {
                url += "?" + string.Join("&", queryParams);
            }

            return await _http.GetFromJsonAsync<UniverseGraphResponse>(url, ct);
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GET /universe/{Qid}/graph failed", qid);
            LastError = ex.Message;
            return null;
        }
    }

    public async Task<IReadOnlyList<LoreDeltaResultDto>> CheckLoreDeltaAsync(
        string qid, CancellationToken ct = default)
    {
        try
        {
            var raw = await _http.GetFromJsonAsync<List<LoreDeltaResultDto>>(
                $"universe/{Uri.EscapeDataString(qid)}/lore-delta", ct);
            return raw ?? [];
        }
        catch (OperationCanceledException) { return []; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GET /universe/{Qid}/lore-delta failed", qid);
            LastError = ex.Message;
            return [];
        }
    }

    public async Task<IReadOnlyList<UniverseLoreSourceViewModel>> GetUniverseLoreSourcesAsync(
        string qid, CancellationToken ct = default)
    {
        try
        {
            return await _http.GetFromJsonAsync<List<UniverseLoreSourceViewModel>>(
                $"universe/{Uri.EscapeDataString(qid)}/lore-sources", ct) ?? [];
        }
        catch (OperationCanceledException) { return []; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GET /universe/{Qid}/lore-sources failed", qid);
            LastError = ex.Message;
            return [];
        }
    }

    public async Task<IReadOnlyList<UniverseLoreSourceViewModel>> DiscoverUniverseLoreSourcesAsync(
        string qid, CancellationToken ct = default)
    {
        try
        {
            using var response = await _http.PostAsJsonAsync(
                $"universe/{Uri.EscapeDataString(qid)}/lore-sources/discover", new { }, ct);
            if (!response.IsSuccessStatusCode)
            {
                return [];
            }

            return await response.Content.ReadFromJsonAsync<List<UniverseLoreSourceViewModel>>(cancellationToken: ct) ?? [];
        }
        catch (OperationCanceledException) { return []; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "POST /universe/{Qid}/lore-sources/discover failed", qid);
            LastError = ex.Message;
            return [];
        }
    }

    public async Task<UniverseLoreSourceViewModel?> AddUniverseLoreSourceAsync(
        string qid, UniverseLoreManualSourceRequest request, CancellationToken ct = default)
    {
        try
        {
            using var response = await _http.PostAsJsonAsync(
                $"universe/{Uri.EscapeDataString(qid)}/lore-sources/manual", request, ct);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            return await response.Content.ReadFromJsonAsync<UniverseLoreSourceViewModel>(cancellationToken: ct);
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "POST /universe/{Qid}/lore-sources/manual failed", qid);
            LastError = ex.Message;
            return null;
        }
    }

    public Task<IReadOnlyList<UniverseLoreSourceViewModel>> ApproveUniverseLoreSourceAsync(
        string qid, Guid sourceId, CancellationToken ct = default) =>
        SetUniverseLoreSourceStatusAsync(qid, sourceId, "approve", ct);

    public Task<IReadOnlyList<UniverseLoreSourceViewModel>> RejectUniverseLoreSourceAsync(
        string qid, Guid sourceId, CancellationToken ct = default) =>
        SetUniverseLoreSourceStatusAsync(qid, sourceId, "reject", ct);

    public async Task<UniverseLoreEnrichmentSummaryViewModel?> EnrichUniverseLoreAsync(
        string qid, CancellationToken ct = default)
    {
        try
        {
            using var response = await _http.PostAsJsonAsync(
                $"universe/{Uri.EscapeDataString(qid)}/lore/enrich", new { }, ct);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            return await response.Content.ReadFromJsonAsync<UniverseLoreEnrichmentSummaryViewModel>(cancellationToken: ct);
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "POST /universe/{Qid}/lore/enrich failed", qid);
            LastError = ex.Message;
            return null;
        }
    }

    private async Task<IReadOnlyList<UniverseLoreSourceViewModel>> SetUniverseLoreSourceStatusAsync(
        string qid,
        Guid sourceId,
        string statusAction,
        CancellationToken ct)
    {
        try
        {
            using var response = await _http.PostAsJsonAsync(
                $"universe/{Uri.EscapeDataString(qid)}/lore-sources/{sourceId:D}/{statusAction}", new { }, ct);
            if (!response.IsSuccessStatusCode)
            {
                return [];
            }

            return await response.Content.ReadFromJsonAsync<List<UniverseLoreSourceViewModel>>(cancellationToken: ct) ?? [];
        }
        catch (OperationCanceledException) { return []; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "POST /universe/{Qid}/lore-sources/{SourceId}/{StatusAction} failed", qid, sourceId, statusAction);
            LastError = ex.Message;
            return [];
        }
    }

    public async Task<IReadOnlyList<NarrativeRootDto>> GetUniversesAsync(CancellationToken ct = default)
    {
        try
        {
            var raw = await _http.GetFromJsonAsync<List<NarrativeRootDto>>("universes", ct);
            return raw ?? [];
        }
        catch (OperationCanceledException) { return []; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GET /universes failed");
            LastError = ex.Message;
            return [];
        }
    }

    public async Task<DeepEnrichResponse?> TriggerDeepEnrichAsync(string entityQid, int depth = 2, CancellationToken ct = default)
    {
        try
        {
            var response = await _http.PostAsync(
                $"universe/entity/{Uri.EscapeDataString(entityQid)}/deep-enrich?depth={depth}",
                null, ct);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            return await response.Content.ReadFromJsonAsync<DeepEnrichResponse>(cancellationToken: ct);
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "POST /universe/entity/{Qid}/deep-enrich failed", entityQid);
            LastError = ex.Message;
            return null;
        }
    }

    // -- Universe Explorer (Phase 2 modes) ------------------------------------

    public async Task<UniverseCastResponse?> GetUniverseCastAsync(string qid, CancellationToken ct = default)
    {
        try
        {
            return await _http.GetFromJsonAsync<UniverseCastResponse>(
                $"universe/{Uri.EscapeDataString(qid)}/cast", ct);
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GET /universe/{Qid}/cast failed", qid);
            LastError = ex.Message;
            return null;
        }
    }

    public async Task<UniverseAdaptationsResponse?> GetUniverseAdaptationsAsync(string qid, CancellationToken ct = default)
    {
        try
        {
            return await _http.GetFromJsonAsync<UniverseAdaptationsResponse>(
                $"universe/{Uri.EscapeDataString(qid)}/adaptations", ct);
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GET /universe/{Qid}/adaptations failed", qid);
            LastError = ex.Message;
            return null;
        }
    }

    public async Task<UniversePathsResponse?> FindPathsAsync(
        string qid, string fromQid, string toQid, int maxHops = 4, CancellationToken ct = default)
    {
        try
        {
            var url = $"universe/{Uri.EscapeDataString(qid)}/paths" +
                      $"?from={Uri.EscapeDataString(fromQid)}" +
                      $"&to={Uri.EscapeDataString(toQid)}" +
                      $"&maxHops={maxHops}";
            return await _http.GetFromJsonAsync<UniversePathsResponse>(url, ct);
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GET /universe/{Qid}/paths failed", qid);
            LastError = ex.Message;
            return null;
        }
    }

    public async Task<FamilyTreeResponse?> GetFamilyTreeAsync(
        string qid, string characterQid, int generations = 3, CancellationToken ct = default)
    {
        try
        {
            var url = $"universe/{Uri.EscapeDataString(qid)}/family-tree" +
                      $"?character={Uri.EscapeDataString(characterQid)}" +
                      $"&generations={generations}";
            return await _http.GetFromJsonAsync<FamilyTreeResponse>(url, ct);
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GET /universe/{Qid}/family-tree failed", qid);
            LastError = ex.Message;
            return null;
        }
    }

    // -- Search (/search) -----------------------------------------------------

    public async Task<SearchUniverseResponseDto?> SearchUniverseAsync(
        string query, string mediaType, int maxCandidates = 5,
        string? localAuthor = null, CancellationToken ct = default)
    {
        try
        {
            var payload = new SearchUniverseRequestDto
            {
                Query = query,
                MediaType = mediaType,
                MaxCandidates = maxCandidates,
                LocalAuthor = localAuthor,
            };
            var resp = await _http.PostAsJsonAsync("/search/universe", payload, ct);
            if (!resp.IsSuccessStatusCode)
            {
                LastError = $"POST /search/universe failed: {resp.StatusCode}";
                return null;
            }
            return await resp.Content.ReadFromJsonAsync<SearchUniverseResponseDto>(ct);
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            LastError = ex.Message;
            _logger.LogWarning(ex, "POST /search/universe failed");
            return null;
        }
    }

    public async Task<SearchRetailResponseDto?> SearchRetailAsync(
        string query, string mediaType, int maxCandidates = 5,
        string? localTitle = null, string? localAuthor = null, string? localYear = null,
        Dictionary<string, string>? fileHints = null,
        Dictionary<string, string>? searchFields = null,
        CancellationToken ct = default)
    {
        try
        {
            var payload = new SearchRetailRequestDto
            {
                Query = query,
                MediaType = mediaType,
                MaxCandidates = maxCandidates,
                LocalTitle = localTitle,
                LocalAuthor = localAuthor,
                LocalYear = localYear,
                FileHints = fileHints,
                SearchFields = searchFields,
            };
            var resp = await _http.PostAsJsonAsync("/search/retail", payload, ct);
            if (!resp.IsSuccessStatusCode)
            {
                LastError = $"POST /search/retail failed: {resp.StatusCode}";
                return null;
            }
            return await resp.Content.ReadFromJsonAsync<SearchRetailResponseDto>(ct);
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            LastError = ex.Message;
            _logger.LogWarning(ex, "POST /search/retail failed");
            return null;
        }
    }

    public async Task<SearchResolveResponseDto?> SearchResolveAsync(
        string query, string mediaType, int maxCandidates,
        Dictionary<string, string>? fileHints, CancellationToken ct = default)
    {
        try
        {
            var payload = new SearchResolveRequestDto
            {
                Query = query,
                MediaType = mediaType,
                MaxCandidates = maxCandidates,
                FileHints = fileHints,
            };
            var resp = await _http.PostAsJsonAsync("/search/resolve", payload, ct);
            if (!resp.IsSuccessStatusCode)
            {
                LastError = $"POST /search/resolve failed: {resp.StatusCode}";
                return null;
            }
            return await resp.Content.ReadFromJsonAsync<SearchResolveResponseDto>(ct);
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            LastError = ex.Message;
            _logger.LogWarning(ex, "POST /search/resolve failed");
            return null;
        }
    }

    public async Task<RetailCandidateDetailDto?> GetRetailCandidateDetailAsync(
        RetailCandidateDetailRequestDto request, CancellationToken ct = default)
    {
        try
        {
            var resp = await _http.PostAsJsonAsync("/search/retail/detail", request, ct);
            if (!resp.IsSuccessStatusCode)
            {
                LastError = $"POST /search/retail/detail failed: {resp.StatusCode}";
                return null;
            }
            return await resp.Content.ReadFromJsonAsync<RetailCandidateDetailDto>(ct);
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            LastError = ex.Message;
            _logger.LogWarning(ex, "POST /search/retail/detail failed");
            return null;
        }
    }

    public async Task<ApplyMatchResponseDto?> ApplyLibraryItemMatchAsync(
        Guid entityId, ApplyMatchRequestDto request,
        CancellationToken ct = default)
    {
        try
        {
            var resp = await _http.PostAsJsonAsync(
                $"/library/items/{entityId}/apply-match", request, ct);
            if (!resp.IsSuccessStatusCode)
            {
                LastError = $"POST /library/items/.../apply-match failed: {resp.StatusCode}";
                return null;
            }
            return await resp.Content.ReadFromJsonAsync<ApplyMatchResponseDto>(ct);
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            LastError = ex.Message;
            _logger.LogWarning(ex, "POST /library/items/{EntityId}/apply-match failed", entityId);
            return null;
        }
    }

    public async Task<ItemCanonicalSearchResponseDto?> SearchItemCanonicalAsync(
        Guid entityId, ItemCanonicalSearchRequestDto request, CancellationToken ct = default)
    {
        try
        {
            var resp = await _http.PostAsJsonAsync($"/library/items/{entityId}/canonical-search", request, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var error = await resp.Content.ReadAsStringAsync(ct);
                LastError = string.IsNullOrWhiteSpace(error)
                    ? $"POST /library/items/.../canonical-search failed: {resp.StatusCode}"
                    : $"POST /library/items/.../canonical-search failed: {resp.StatusCode} - {error}";
                return null;
            }

            return await resp.Content.ReadFromJsonAsync<ItemCanonicalSearchResponseDto>(ct);
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            LastError = ex.Message;
            _logger.LogWarning(ex, "POST /library/items/{EntityId}/canonical-search failed", entityId);
            return null;
        }
    }

    public async Task<ItemCanonicalApplyResponseDto?> ApplyItemCanonicalAsync(
        Guid entityId, ItemCanonicalApplyRequestDto request, CancellationToken ct = default)
    {
        try
        {
            var resp = await _http.PostAsJsonAsync($"/library/items/{entityId}/canonical-apply", request, ct);
            if (!resp.IsSuccessStatusCode)
            {
                LastError = $"POST /library/items/.../canonical-apply failed: {resp.StatusCode}";
                return null;
            }

            return await resp.Content.ReadFromJsonAsync<ItemCanonicalApplyResponseDto>(ct);
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            LastError = ex.Message;
            _logger.LogWarning(ex, "POST /library/items/{EntityId}/canonical-apply failed", entityId);
            return null;
        }
    }

    public async Task<ItemCanonicalApplyResponseDto?> ReplaceRetailMatchAsync(
        Guid entityId, ReplaceRetailMatchRequestDto request, CancellationToken ct = default)
    {
        try
        {
            var resp = await _http.PostAsJsonAsync($"/library/items/{entityId}/retail-match", request, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var error = await resp.Content.ReadAsStringAsync(ct);
                LastError = string.IsNullOrWhiteSpace(error)
                    ? $"POST /library/items/.../retail-match failed: {resp.StatusCode}"
                    : $"POST /library/items/.../retail-match failed: {resp.StatusCode} - {error}";
                return null;
            }

            return await resp.Content.ReadFromJsonAsync<ItemCanonicalApplyResponseDto>(ct);
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            LastError = ex.Message;
            _logger.LogWarning(ex, "POST /library/items/{EntityId}/retail-match failed", entityId);
            return null;
        }
    }

    public Task<RetailMatchMovePreviewDto?> PreviewRetailMatchAsync(
        Guid entityId, ReplaceRetailMatchRequestDto request, CancellationToken ct = default) =>
        PostAsync<ReplaceRetailMatchRequestDto, RetailMatchMovePreviewDto>(
            "POST /library/items/{entityId}/retail-match-preview",
            $"/library/items/{entityId}/retail-match-preview", request, ct: ct);

    public async Task<ItemCanonicalApplyResponseDto?> ReplaceWikidataMatchAsync(
        Guid entityId, ReplaceWikidataMatchRequestDto request, CancellationToken ct = default)
    {
        try
        {
            var resp = await _http.PostAsJsonAsync($"/library/items/{entityId}/wikidata-match", request, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var error = await resp.Content.ReadAsStringAsync(ct);
                LastError = string.IsNullOrWhiteSpace(error)
                    ? $"POST /library/items/.../wikidata-match failed: {resp.StatusCode}"
                    : $"POST /library/items/.../wikidata-match failed: {resp.StatusCode} - {error}";
                return null;
            }

            return await resp.Content.ReadFromJsonAsync<ItemCanonicalApplyResponseDto>(ct);
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            LastError = ex.Message;
            _logger.LogWarning(ex, "POST /library/items/{EntityId}/wikidata-match failed", entityId);
            return null;
        }
    }

    public async Task<CreateManualResponseDto?> CreateManualEntryAsync(
        Guid entityId, CreateManualRequestDto request,
        CancellationToken ct = default)
    {
        try
        {
            var resp = await _http.PostAsJsonAsync(
                $"/library/items/{entityId}/create-manual", request, ct);
            if (!resp.IsSuccessStatusCode)
            {
                LastError = $"POST /library/items/.../create-manual failed: {resp.StatusCode}";
                return null;
            }
            return await resp.Content.ReadFromJsonAsync<CreateManualResponseDto>(ct);
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            LastError = ex.Message;
            _logger.LogWarning(ex, "POST /library/items/{EntityId}/create-manual failed", entityId);
            return null;
        }
    }

    public async Task<bool> DeleteLibraryCatalogItemAsync(Guid entityId, CancellationToken ct = default)
    {
        try
        {
            var resp = await _http.DeleteAsync($"/library/items/{entityId}", ct);
            return resp.IsSuccessStatusCode;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception ex)
        {
            LastError = ex.Message;
            _logger.LogWarning(ex, "DELETE /library/items/{EntityId} failed", entityId);
            return false;
        }
    }

    public async Task<List<LibraryItemHistoryDto>> GetItemHistoryAsync(
        Guid entityId, CancellationToken ct = default) =>
        (await GetItemHistoryWithStatusAsync(entityId, ct)).Items;

    public async Task<(List<LibraryItemHistoryDto> Items, string? Error)> GetItemHistoryWithStatusAsync(
        Guid entityId, CancellationToken ct = default)
    {
        try
        {
            var result = await _http.GetFromJsonAsync<List<MediaEngine.Contracts.Items.LibraryItemHistoryDto>>(
                $"/library/items/{entityId}/history", ct);
            return (result?.Select(item => new LibraryItemHistoryDto
            {
                Id = item.Id,
                EntityId = item.EntityId,
                OccurredAt = item.OccurredAt,
                EventType = item.EventType,
                Label = item.Label,
                Detail = item.Detail,
                Category = item.Category,
                ActorLabel = item.ActorLabel,
            }).ToList() ?? [], null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return ([], "History loading was cancelled."); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GET /library/items/{EntityId}/history failed", entityId);
            return ([], ex is HttpRequestException { StatusCode: System.Net.HttpStatusCode.Forbidden }
                ? "You do not have permission to view this item's history."
                : "History could not be loaded. Try again later.");
        }
    }

    /// <inheritdoc/>
    public async Task<bool> MarkProvisionalAsync(Guid entityId, ProvisionalMetadataRequestDto metadata, CancellationToken ct = default)
    {
        try
        {
            var request = new MediaEngine.Contracts.Items.ProvisionalMetadataRequestDto
            {
                MediaType = metadata.MediaType,
                Title = metadata.Title,
                Creator = metadata.Creator,
                Year = metadata.Year,
                Description = metadata.Description,
                Narrator = metadata.Narrator,
                Isbn = metadata.Isbn,
                Director = metadata.Director,
                Runtime = metadata.Runtime,
                Seasons = metadata.Seasons,
                TrackCount = metadata.TrackCount,
                Host = metadata.Host,
                Writer = metadata.Writer,
                Artist = metadata.Artist,
                PageCount = metadata.PageCount,
            };
            var response = await _http.PostAsJsonAsync($"/library/items/{entityId}/provisional", request, ct);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MarkProvisionalAsync failed for entity {EntityId}", entityId);
            return false;
        }
    }

    public string? LastError
    {
        get => _failureState.LastError;
        private set => _failureState.SetError(value);
    }

    public int? LastStatusCode => _failureState.LastStatusCode;

    public string? LastFailedEndpoint => _failureState.LastFailedEndpoint;

    public string? LastFailureKind => _failureState.LastFailureKind;

    public TimeSpan? LastRetryAfter => _failureState.LastRetryAfter;

    private void ClearFailure(string endpoint)
        => _failureState.Clear(endpoint);

    private async Task RecordHttpFailureAsync(
        string endpoint,
        HttpResponseMessage response,
        CancellationToken ct,
        bool logAsWarning = true)
        => await _failureState.RecordHttpFailureAsync(endpoint, response, _logger, ct, logAsWarning);

    private void RecordExceptionFailure(string endpoint, Exception ex, bool logAsWarning = true)
        => _failureState.RecordExceptionFailure(endpoint, ex, _logger, logAsWarning);

    // ------------------------------------------------------------------------------------------------
    // MIGRATION RECIPE (stage 5B wave 2)
    // ------------------------------------------------------------------------------------------------
    // This file hand-writes the same HTTP envelope ~270 more times. The five helpers below extract the
    // "endpoint-label" envelope variant — the one used by SendPlayerCommandAsync, PostPlayerHeartbeatAsync,
    // and ReplacePlayerQueueAsync (migrated here as the wave 1 proof). Apply this decision table mechanically;
    // do not invent new shapes without escalating.
    //
    // Recognize the envelope variant a method uses, then transform it:
    //
    //   Shape found in the method                                              | Replace with
    //   -------------------------------------------------------------------------------------------------------
    //   GetFromJsonAsync<T>(url, ct) directly (no manual status check), single | GetAsync<T>(label, path, query, ct)
    //   catch(Exception) -> RecordExceptionFailure only, e.g.                  |   (returns T?; if the method currently
    //   GetAudiobookListenHistoryAsync, GetAudiobookChapterTitleOverridesAsync |   falls back to [] / a default value on
    //   GetAudiobookChapterTitleOverridesAsync, GetEncodeJobsAsync            |   null, use the GetAsync<T>(label, path,
    //                                                                         |   fallback, query, ct) overload instead.)
    //   -------------------------------------------------------------------------------------------------------
    //   PostAsJsonAsync + manual IsSuccessStatusCode check + ReadFromJsonAsync | PostAsync<TReq,TRes>(label, path, body, ct)
    //   for the typed result, e.g. SendPlayerCommandAsync, PostPlayerHeartbeat |
    //   Async, PostPlayerMutationAsync, TriggerScanAsync, UpsertAudiobook...   |
    //   -------------------------------------------------------------------------------------------------------
    //   PostAsJsonAsync + manual IsSuccessStatusCode check, no response body   | PostAsync<TReq>(label, path, body, ct)
    //   read, returns bool, e.g. TriggerRescanAsync, CancelEncodeJobAsync     |   (bool-returning overload)
    //   -------------------------------------------------------------------------------------------------------
    //   DeleteAsync(url, ct) + manual IsSuccessStatusCode check, returns bool, | DeleteAsync(label, path, ct)
    //   e.g. DeleteAudiobookChapterTitleOverrideAsync                         |
    //   Async                                                                 |
    //   -------------------------------------------------------------------------------------------------------
    //   PutAsJsonAsync + manual IsSuccessStatusCode check, returns bool       | PutAsync<TReq>(label, path, body, ct)
    //                                                                         |   ONLY if the method already follows the
    //                                                                         |   endpoint-label + RecordHttpFailureAsync/
    //                                                                         |   ClearFailure/RecordExceptionFailure
    //                                                                         |   pattern. As of wave 1, NO existing PUT
    //                                                                         |   method does — see "NOT covered" below.
    //                                                                         |   Do not point a legacy-shape PUT method
    //                                                                         |   at this helper without a deliberate,
    //                                                                         |   reviewed decision to add failure-state
    //                                                                         |   bookkeeping that did not exist before.
    //
    // Mechanical steps once a method matches a row above:
    //   1. Delete the `const string endpoint = "..."` local (it becomes the helper's first argument).
    //   2. Replace the whole try/catch body with a single expression-bodied call to the matching helper,
    //      passing the same path string(s) the method already builds (including any interpolated
    //      {workId}/{assetId} segments and query-string suffixes — do not change the wire path).
    //   3. If the method builds a query string by hand (e.g. `var suffix = "?" + string.Join("&", query)`),
    //      convert it to an `IReadOnlyDictionary<string, string?>` and let BuildEndpointPath assemble it —
    //      do NOT silently drop a query parameter or reorder it in a way that changes the escaped output.
    //   4. If the method passes `logAsWarning: false` to RecordHttpFailureAsync/RecordExceptionFailure today
    //      (e.g. GetReviewCountAsync), pass the same `logAsWarning: false` through to the helper call —
    //      the default is `true` to match the majority shape (and the three wave-1 proof methods).
    //   5. Keep the async method non-async (`=>` expression body returning the helper's Task) exactly like
    //      the three migrated methods here — do not add a redundant `async`/`await` wrapper.
    //
    // Envelope variants this file ALSO contains that these helpers deliberately do NOT cover — leave these
    // methods hand-written, do not force them onto the helpers above:
    //   - "Legacy LastError-only" shape (the majority of PUT methods, e.g.
    //     SaveTranscodingSettingsAsync, UpdatePlaybackSettingsAsync, TestPathAsync): sets `LastError` directly
    //     via the property setter, never touches `_failureState.RecordHttpFailureAsync`/`ClearFailure`/
    //     `RecordExceptionFailure`, so `LastFailedEndpoint`/`LastFailureKind`/`LastStatusCode` are never
    //     populated for these calls today. Routing them through PutAsync<TReq>/PostAsync<TReq,TRes> would
    //     start populating those fields — an observable behavior change, not a mechanical rename. Only do
    //     this as an explicit, reviewed decision (and note it in the stage 5B write-up), never silently.
    //   - "Manual GetAsync + explicit status check" GET shape (e.g. GetReviewCountAsync,
    //     GetPlaybackManifestAsync): uses `_http.GetAsync` + `IsSuccessStatusCode` + `RecordHttpFailureAsync`
    //     instead of relying on GetFromJsonAsync's auto-throw, often because it also projects the DTO into a
    //     scalar (e.g. `raw?.PendingCount ?? 0`) or needs the raw response for other reasons. GetAsync<T> here
    //     mirrors the auto-throw shape instead, because that is what most GET methods use and it is what
    //     keeps `LastFailureKind` classification (not_found/unauthorized/http_failure vs
    //     engine_unavailable/unexpected_failure) identical to today. Do not mechanically retarget shape-2 GETs
    //     at GetAsync<T> — the failure classification would silently change.
    //   - Methods with no failure-state bookkeeping at all (e.g. MarkProvisionalAsync): a single
    //     catch(Exception) that only logs and returns false/null, never calling ClearFailure/
    //     RecordHttpFailureAsync/RecordExceptionFailure. Leave these as-is unless deliberately upgrading them.
    //   - Methods whose catch(Exception) interpolates additional structured arguments beyond the endpoint
    //     label (e.g. `_logger.LogDebug(ex, "GET /player/audiobooks/{WorkId}/history failed", workId)`): the
    //     helpers below only log `"{Endpoint} failed"` with the endpoint label as the sole argument. Losing
    //     the extra structured arg is usually acceptable (the id is still visible as literal route text in
    //     the endpoint label string), but if a call site depends on the structured field for log queries,
    //     leave it hand-written instead of silently flattening it.
    //   - RunDevHarnessAsync: returns full diagnostic payload (status, body, timing) on both success AND
    //     failure paths and never returns a bare null/false/default on error. Not helper-shaped; leave as-is.
    //   - Endpoints reading the emerging application/problem+json body for anything beyond title/detail
    //     (stage 5A is concurrently migrating error shapes): RecordHttpFailureAsync already tolerates both
    //     ProblemDetails (title/detail/traceId) and legacy free-text bodies via
    //     EngineApiFailureState.ReadProblemSummaryAsync, which falls back to the raw string on any parse
    //     failure — the helpers below inherit that tolerance for free and require no extra parsing.
    // ------------------------------------------------------------------------------------------------

    /// <summary>
    /// GET envelope: calls <c>GetFromJsonAsync&lt;T&gt;</c> directly (relying on it to throw on a non-success
    /// status) and reports failures through <see cref="RecordExceptionFailure(string, Exception, bool)"/> only —
    /// this mirrors the majority "auto-throw" GET shape already used by e.g. GetAudiobookListenHistoryAsync.
    /// See the "manual GetAsync + explicit status check" note above for the GET shape this does NOT cover.
    /// </summary>
    private async Task<T?> GetAsync<T>(
        string endpointLabel,
        string path,
        IReadOnlyDictionary<string, string?>? query = null,
        bool logAsWarning = true,
        CancellationToken ct = default)
    {
        try
        {
            var result = await _http.GetFromJsonAsync<T>(BuildEndpointPath(path, query), ct);
            ClearFailure(endpointLabel);
            return result;
        }
        catch (OperationCanceledException) { return default; }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "{Endpoint} failed", endpointLabel);
            RecordExceptionFailure(endpointLabel, ex, logAsWarning);
            return default;
        }
    }

    /// <summary>
    /// Overload for GET methods that fall back to a non-null default (commonly <c>[]</c>) instead of
    /// returning null on failure/cancellation, e.g. GetEncodeJobsAsync's <c>?? []</c>.
    /// </summary>
    private async Task<T> GetAsync<T>(
        string endpointLabel,
        string path,
        Func<T> fallback,
        IReadOnlyDictionary<string, string?>? query = null,
        bool logAsWarning = true,
        CancellationToken ct = default)
    {
        var result = await GetAsync<T>(endpointLabel, path, query, logAsWarning, ct);
        return result is null ? fallback() : result;
    }

    private async Task<string?> GetRawTextAsync(string endpointLabel, string path, CancellationToken ct)
    {
        try
        {
            using var response = await _http.GetAsync(path, ct);
            if (!response.IsSuccessStatusCode)
            {
                await RecordHttpFailureAsync(endpointLabel, response, ct, logAsWarning: false);
                return null;
            }

            var content = await response.Content.ReadAsStringAsync(ct);
            ClearFailure(endpointLabel);
            return content;
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "{Endpoint} failed", endpointLabel);
            RecordExceptionFailure(endpointLabel, ex, logAsWarning: false);
            return null;
        }
    }

    /// <summary>
    /// POST envelope with a typed JSON response body. This is the shape proven by the wave 1 migration of
    /// SendPlayerCommandAsync, PostPlayerHeartbeatAsync, and ReplacePlayerQueueAsync (via PostPlayerMutationAsync).
    /// </summary>
    private async Task<TRes?> PostAsync<TReq, TRes>(
        string endpointLabel,
        string path,
        TReq body,
        bool logAsWarning = true,
        CancellationToken ct = default)
    {
        try
        {
            var response = await _http.PostAsJsonAsync(path, body, ct);
            if (!response.IsSuccessStatusCode)
            {
                await RecordHttpFailureAsync(endpointLabel, response, ct, logAsWarning);
                return default;
            }

            var result = await response.Content.ReadFromJsonAsync<TRes>(cancellationToken: ct);
            ClearFailure(endpointLabel);
            return result;
        }
        catch (OperationCanceledException) { return default; }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "{Endpoint} failed", endpointLabel);
            RecordExceptionFailure(endpointLabel, ex, logAsWarning);
            return default;
        }
    }

    /// <summary>
    /// Fire-and-check POST envelope for methods that only need a success/failure bool, e.g. TriggerRescanAsync.
    /// </summary>
    private async Task<bool> PostAsync<TReq>(
        string endpointLabel,
        string path,
        TReq body,
        bool logAsWarning = true,
        CancellationToken ct = default)
    {
        try
        {
            var response = await _http.PostAsJsonAsync(path, body, ct);
            if (!response.IsSuccessStatusCode)
            {
                await RecordHttpFailureAsync(endpointLabel, response, ct, logAsWarning);
                return false;
            }

            ClearFailure(endpointLabel);
            return true;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "{Endpoint} failed", endpointLabel);
            RecordExceptionFailure(endpointLabel, ex, logAsWarning);
            return false;
        }
    }

    /// <summary>
    /// PUT envelope matching the endpoint-label pattern (see the recipe note above: no current PUT method
    /// actually uses this shape yet — every PUT today uses the "legacy LastError-only" variant instead).
    /// Provided so wave 2 has it available for any PUT this pattern legitimately applies to, and so future
    /// endpoints don't reinvent it. Do not point an existing legacy-shape PUT at this without a deliberate
    /// decision to add failure-state bookkeeping that was not there before.
    /// </summary>
    private async Task<bool> PutAsync<TReq>(
        string endpointLabel,
        string path,
        TReq body,
        bool logAsWarning = true,
        CancellationToken ct = default)
    {
        try
        {
            var response = await _http.PutAsJsonAsync(path, body, ct);
            if (!response.IsSuccessStatusCode)
            {
                await RecordHttpFailureAsync(endpointLabel, response, ct, logAsWarning);
                return false;
            }

            ClearFailure(endpointLabel);
            return true;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "{Endpoint} failed", endpointLabel);
            RecordExceptionFailure(endpointLabel, ex, logAsWarning);
            return false;
        }
    }

    private async Task<TRes?> PutAsync<TReq, TRes>(
        string endpointLabel,
        string path,
        TReq body,
        bool logAsWarning = true,
        CancellationToken ct = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Put, path)
            {
                Content = JsonContent.Create(body),
            };
            using var response = await _http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                await RecordHttpFailureAsync(endpointLabel, response, ct, logAsWarning);
                return default;
            }

            var result = await response.Content.ReadFromJsonAsync<TRes>(cancellationToken: ct);
            ClearFailure(endpointLabel);
            return result;
        }
        catch (OperationCanceledException) { return default; }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "{Endpoint} failed", endpointLabel);
            RecordExceptionFailure(endpointLabel, ex, logAsWarning);
            return default;
        }
    }

    /// <summary>
    /// DELETE envelope matching e.g. DeleteAudiobookChapterTitleOverrideAsync.
    /// </summary>
    private async Task<bool> DeleteAsync(
        string endpointLabel,
        string path,
        bool logAsWarning = true,
        CancellationToken ct = default)
    {
        try
        {
            var response = await _http.DeleteAsync(path, ct);
            if (!response.IsSuccessStatusCode)
            {
                await RecordHttpFailureAsync(endpointLabel, response, ct, logAsWarning);
                return false;
            }

            ClearFailure(endpointLabel);
            return true;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "{Endpoint} failed", endpointLabel);
            RecordExceptionFailure(endpointLabel, ex, logAsWarning);
            return false;
        }
    }

    private static string BuildEndpointPath(string path, IReadOnlyDictionary<string, string?>? query)
    {
        var normalizedPath = string.IsNullOrWhiteSpace(path)
            ? "/"
            : path.StartsWith("/", StringComparison.Ordinal) ? path : $"/{path}";

        if (query is null || query.Count == 0)
        {
            return normalizedPath;
        }

        var parts = query
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value))
            .Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value!)}")
            .ToArray();

        return parts.Length == 0
            ? normalizedPath
            : $"{normalizedPath}?{string.Join("&", parts)}";
    }

    private static string SummarizeResponseBody(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return "Request failed";
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                var error = TryGetString(doc.RootElement, "error");
                var title = TryGetString(doc.RootElement, "title");
                var detail = TryGetString(doc.RootElement, "detail");
                var summary = string.Join(" ", new[] { error, title, detail }
                    .Where(static part => !string.IsNullOrWhiteSpace(part)));

                if (!string.IsNullOrWhiteSpace(summary))
                {
                    return summary;
                }
            }
        }
        catch (JsonException)
        {
            // Non-JSON harness responses are expected for the HTML integration report.
        }

        var compact = body.ReplaceLineEndings(" ").Trim();
        return compact.Length <= 500 ? compact : $"{compact[..500]}...";
    }

    private static async Task<string> ReadProblemSummaryAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var raw = await response.Content.ReadAsStringAsync(ct);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return response.ReasonPhrase ?? "Request failed";
        }

        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return raw;
            }

            var title = TryGetString(doc.RootElement, "title");
            var detail = TryGetString(doc.RootElement, "detail");
            var traceId = TryGetString(doc.RootElement, "traceId") ?? TryGetString(doc.RootElement, "trace_id");
            var parts = new[] { title, detail, string.IsNullOrWhiteSpace(traceId) ? null : $"Trace: {traceId}" }
                .Where(static part => !string.IsNullOrWhiteSpace(part));
            var summary = string.Join(" ", parts);
            return string.IsNullOrWhiteSpace(summary) ? raw : summary;
        }
        catch (JsonException)
        {
            return raw;
        }
    }

    private static string? TryGetString(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    // -- Private mapping -------------------------------------------------------

    private void NormalizeCollectionGroupDetail(CollectionGroupDetailViewModel? detail)
    {
        if (detail is null)
        {
            return;
        }

        if (detail.CoverUrl is not null)
        {
            detail.CoverUrl = AbsoluteUrl(detail.CoverUrl);
        }

        if (detail.BackgroundUrl is not null)
        {
            detail.BackgroundUrl = AbsoluteUrl(detail.BackgroundUrl);
        }

        if (detail.BannerUrl is not null)
        {
            detail.BannerUrl = AbsoluteUrl(detail.BannerUrl);
        }

        if (detail.HeroUrl is not null)
        {
            detail.HeroUrl = AbsoluteUrl(detail.HeroUrl);
        }

        if (detail.LogoUrl is not null)
        {
            detail.LogoUrl = AbsoluteUrl(detail.LogoUrl);
        }

        if (detail.ArtistPhotoUrl is not null)
        {
            detail.ArtistPhotoUrl = AbsoluteUrl(detail.ArtistPhotoUrl);
        }

        detail.TopCast = NormalizeCastCredits(detail.TopCast);

        foreach (var season in detail.Seasons)
        {
            if (season.CoverUrl is not null)
            {
                season.CoverUrl = AbsoluteUrl(season.CoverUrl);
            }

            foreach (var episode in season.Episodes)
            {
                NormalizeCollectionGroupWork(episode);
            }
        }

        foreach (var work in detail.Works)
        {
            NormalizeCollectionGroupWork(work);
        }
    }

    private void NormalizeCollectionGroupWork(CollectionGroupWorkDto work)
    {
        if (work.CoverUrl is not null)
        {
            work.CoverUrl = AbsoluteUrl(work.CoverUrl);
        }

        if (work.BackgroundUrl is not null)
        {
            work.BackgroundUrl = AbsoluteUrl(work.BackgroundUrl);
        }

        if (work.BannerUrl is not null)
        {
            work.BannerUrl = AbsoluteUrl(work.BannerUrl);
        }

        if (work.HeroUrl is not null)
        {
            work.HeroUrl = AbsoluteUrl(work.HeroUrl);
        }
    }

    private List<CastCreditDto> NormalizeCastCredits(IEnumerable<CastCreditDto>? castCredits)
    {
        if (castCredits is null)
        {
            return [];
        }

        return castCredits.Select(cast => new CastCreditDto
        {
            PersonId = cast.PersonId,
            Name = cast.Name,
            WikidataQid = cast.WikidataQid,
            HeadshotUrl = string.IsNullOrWhiteSpace(cast.HeadshotUrl)
                ? cast.HeadshotUrl
                : AbsoluteUrl(cast.HeadshotUrl),
            Characters = cast.Characters.Select(character => new CharacterPortrayalDto
            {
                FictionalEntityId = character.FictionalEntityId,
                CharacterName = character.CharacterName,
                CharacterQid = character.CharacterQid,
                PortraitUrl = string.IsNullOrWhiteSpace(character.PortraitUrl)
                    ? character.PortraitUrl
                    : AbsoluteUrl(character.PortraitUrl),
            }).ToList(),
        }).ToList();
    }

    private DisplayPageDto? NormalizeDisplayPage(DisplayPageDto? page)
    {
        if (page is null)
        {
            return null;
        }

        return page with
        {
            Hero = page.Hero is null ? null : page.Hero with { Artwork = NormalizeDisplayArtwork(page.Hero.Artwork) },
            Shelves = page.Shelves
                .Select(shelf => shelf with { Items = shelf.Items.Select(NormalizeDisplayCard).ToList() })
                .ToList(),
            Catalog = page.Catalog.Select(NormalizeDisplayCard).ToList(),
        };
    }

    private DisplayCardDto NormalizeDisplayCard(DisplayCardDto card) =>
        card with
        {
            Artwork = NormalizeDisplayArtwork(card.Artwork),
            PreviewItems = card.PreviewItems.Select(NormalizeDisplayPreviewItem).ToList(),
        };

    private DisplayCardPreviewItemDto NormalizeDisplayPreviewItem(DisplayCardPreviewItemDto item) =>
        item with { ImageUrl = AbsoluteUrl(item.ImageUrl) };

    private DisplayArtworkDto NormalizeDisplayArtwork(DisplayArtworkDto artwork) =>
        artwork with
        {
            CoverUrl = artwork.CoverUrl is null ? null : AbsoluteUrl(artwork.CoverUrl),
            CoverSmallUrl = artwork.CoverSmallUrl is null ? null : AbsoluteUrl(artwork.CoverSmallUrl),
            CoverMediumUrl = artwork.CoverMediumUrl is null ? null : AbsoluteUrl(artwork.CoverMediumUrl),
            CoverLargeUrl = artwork.CoverLargeUrl is null ? null : AbsoluteUrl(artwork.CoverLargeUrl),
            SquareUrl = artwork.SquareUrl is null ? null : AbsoluteUrl(artwork.SquareUrl),
            SquareSmallUrl = artwork.SquareSmallUrl is null ? null : AbsoluteUrl(artwork.SquareSmallUrl),
            SquareMediumUrl = artwork.SquareMediumUrl is null ? null : AbsoluteUrl(artwork.SquareMediumUrl),
            SquareLargeUrl = artwork.SquareLargeUrl is null ? null : AbsoluteUrl(artwork.SquareLargeUrl),
            BannerUrl = artwork.BannerUrl is null ? null : AbsoluteUrl(artwork.BannerUrl),
            BannerSmallUrl = artwork.BannerSmallUrl is null ? null : AbsoluteUrl(artwork.BannerSmallUrl),
            BannerMediumUrl = artwork.BannerMediumUrl is null ? null : AbsoluteUrl(artwork.BannerMediumUrl),
            BannerLargeUrl = artwork.BannerLargeUrl is null ? null : AbsoluteUrl(artwork.BannerLargeUrl),
            BackgroundUrl = artwork.BackgroundUrl is null ? null : AbsoluteUrl(artwork.BackgroundUrl),
            BackgroundSmallUrl = artwork.BackgroundSmallUrl is null ? null : AbsoluteUrl(artwork.BackgroundSmallUrl),
            BackgroundMediumUrl = artwork.BackgroundMediumUrl is null ? null : AbsoluteUrl(artwork.BackgroundMediumUrl),
            BackgroundLargeUrl = artwork.BackgroundLargeUrl is null ? null : AbsoluteUrl(artwork.BackgroundLargeUrl),
            LogoUrl = artwork.LogoUrl is null ? null : AbsoluteUrl(artwork.LogoUrl),
        };

    private DetailPageViewModel NormalizeDetailArtwork(DetailPageViewModel detail)
    {
        var artwork = detail.Artwork;
        return new DetailPageViewModel
        {
            Id = detail.Id,
            EntityType = detail.EntityType,
            PresentationContext = detail.PresentationContext,
            EditorTarget = detail.EditorTarget,
            Title = detail.Title,
            Subtitle = detail.Subtitle,
            Tagline = detail.Tagline,
            SecondaryTitleText = detail.SecondaryTitleText,
            SecondaryTitleTextKind = detail.SecondaryTitleTextKind,
            SecondaryTitleTextHasMore = detail.SecondaryTitleTextHasMore,
            Description = detail.Description,
            DescriptionAttribution = detail.DescriptionAttribution,
            SourceLinks = detail.SourceLinks,
            PersonDetails = NormalizePersonDetails(detail.PersonDetails),
            Facts = detail.Facts,
            Artwork = new ArtworkSet
            {
                BackdropUrl = NormalizeOptionalUrl(artwork.BackdropUrl),
                BannerUrl = NormalizeOptionalUrl(artwork.BannerUrl),
                PosterUrl = NormalizeOptionalUrl(artwork.PosterUrl),
                CoverUrl = NormalizeOptionalUrl(artwork.CoverUrl),
                LogoUrl = NormalizeOptionalUrl(artwork.LogoUrl),
                PortraitUrl = NormalizeOptionalUrl(artwork.PortraitUrl),
                CharacterImageUrl = NormalizeOptionalUrl(artwork.CharacterImageUrl),
                RelatedArtworkUrls = artwork.RelatedArtworkUrls.Select(AbsoluteUrl).ToList(),
                DominantColors = artwork.DominantColors,
                PrimaryColor = artwork.PrimaryColor,
                SecondaryColor = artwork.SecondaryColor,
                AccentColor = artwork.AccentColor,
                HeroArtwork = NormalizeHeroArtwork(artwork.HeroArtwork),
                PresentationMode = artwork.PresentationMode,
                Source = artwork.Source,
            },
            HeroBrand = NormalizeHeroBrand(detail.HeroBrand),
            Progress = detail.Progress,
            OwnedFormats = detail.OwnedFormats.Select(format => new OwnedFormatViewModel
            {
                Id = format.Id,
                FormatType = format.FormatType,
                DisplayName = format.DisplayName,
                CoverUrl = NormalizeOptionalUrl(format.CoverUrl),
                EditionTitle = format.EditionTitle,
                Publisher = format.Publisher,
                ReleaseDate = format.ReleaseDate,
                PrimaryContributor = format.PrimaryContributor,
                FileFormat = format.FileFormat,
                Runtime = format.Runtime,
                PageCount = format.PageCount,
                ChapterCount = format.ChapterCount,
                Progress = format.Progress,
                Actions = format.Actions,
            }).ToList(),
            MultiFormatState = detail.MultiFormatState,
            ReadingListeningSync = detail.ReadingListeningSync,
            SyncCapability = detail.SyncCapability,
            SequencePlacement = NormalizeSequencePlacement(detail.SequencePlacement),
            Metadata = detail.Metadata,
            PrimaryActions = detail.PrimaryActions,
            SecondaryActions = detail.SecondaryActions,
            OverflowActions = detail.OverflowActions,
            ContributorGroups = detail.ContributorGroups.Select(NormalizeCreditGroup).ToList(),
            PreviewContributors = detail.PreviewContributors.Select(NormalizeCredit).ToList(),
            CharacterGroups = detail.CharacterGroups.Select(group => new CharacterGroupViewModel
            {
                Title = group.Title,
                GroupType = group.GroupType,
                Characters = group.Characters.Select(NormalizeCredit).ToList(),
            }).ToList(),
            PreviewCharacters = detail.PreviewCharacters.Select(NormalizeCredit).ToList(),
            RelationshipStrip = detail.RelationshipStrip,
            Tabs = detail.Tabs,
            PrimaryModule = detail.PrimaryModule,
            MusicAlbumCompanion = NormalizeMusicAlbumCompanion(detail.MusicAlbumCompanion),
            MediaGroups = detail.MediaGroups.Select(group => new MediaGroupingViewModel
            {
                Key = group.Key,
                Title = group.Title,
                OwnedCount = group.OwnedCount,
                TotalCount = group.TotalCount,
                MissingCount = group.MissingCount,
                CompletionPercent = group.CompletionPercent,
                InitiallyCollapsed = group.InitiallyCollapsed,
                Items = group.Items.Select(item => new MediaGroupingItemViewModel
                {
                    Id = item.Id,
                    EntityType = item.EntityType,
                    Title = item.Title,
                    Subtitle = item.Subtitle,
                    Description = item.Description,
                    ArtworkUrl = NormalizeOptionalUrl(item.ArtworkUrl),
                    TrackNumber = item.TrackNumber,
                    Duration = item.Duration,
                    DurationSeconds = item.DurationSeconds,
                    Artist = item.Artist,
                    AssetId = item.AssetId,
                    ChapterIndex = item.ChapterIndex,
                    StartSeconds = item.StartSeconds,
                    EndSeconds = item.EndSeconds,
                    ResumePositionSeconds = item.ResumePositionSeconds,
                    IsExplicit = item.IsExplicit,
                    Quality = item.Quality,
                    ProgressPercent = item.ProgressPercent,
                    Lane = item.Lane,
                    Roles = item.Roles,
                    Metadata = item.Metadata,
                    Actions = item.Actions,
                    IsOwned = item.IsOwned,
                    IsFavorite = item.IsFavorite,
                    ProgressState = item.ProgressState,
                }).ToList(),
            }).ToList(),
            IdentityStatus = detail.IdentityStatus,
            LibraryStatus = detail.LibraryStatus,
            IsAdminView = detail.IsAdminView,
        };
    }

    private MusicAlbumCompanionViewModel? NormalizeMusicAlbumCompanion(MusicAlbumCompanionViewModel? companion)
        => companion is null
            ? null
            : new MusicAlbumCompanionViewModel
            {
                PrimaryArtistId = companion.PrimaryArtistId,
                PrimaryArtistName = companion.PrimaryArtistName,
                PrimaryArtistRoute = companion.PrimaryArtistRoute,
                MoreByAlbums = companion.MoreByAlbums.Select(album => new MusicAlbumPreviewViewModel
                {
                    Id = album.Id,
                    Title = album.Title,
                    Year = album.Year,
                    ArtworkUrl = NormalizeOptionalUrl(album.ArtworkUrl),
                    Route = album.Route,
                }).ToList(),
            };

    private HeroArtworkViewModel NormalizeHeroArtwork(HeroArtworkViewModel? heroArtwork)
    {
        if (heroArtwork is null)
        {
            return new HeroArtworkViewModel();
        }

        return new HeroArtworkViewModel
        {
            Url = NormalizeOptionalUrl(heroArtwork.Url),
            SmallUrl = NormalizeOptionalUrl(heroArtwork.SmallUrl),
            MediumUrl = NormalizeOptionalUrl(heroArtwork.MediumUrl),
            LargeUrl = NormalizeOptionalUrl(heroArtwork.LargeUrl),
            Mode = heroArtwork.Mode,
            HasImage = heroArtwork.HasImage && !string.IsNullOrWhiteSpace(heroArtwork.Url),
            AspectRatio = heroArtwork.AspectRatio,
            BackgroundPosition = heroArtwork.BackgroundPosition,
            MobilePosition = heroArtwork.MobilePosition,
        };
    }

    private HeroBrandViewModel? NormalizeHeroBrand(HeroBrandViewModel? heroBrand)
    {
        if (heroBrand is null)
        {
            return null;
        }

        var imageUrl = NormalizeOptionalUrl(heroBrand.ImageUrl)
            ?? _streamingServiceLogos.ResolveLogoPath(heroBrand.Label);

        return new HeroBrandViewModel
        {
            Label = heroBrand.Label,
            ImageUrl = imageUrl,
        };
    }

    private PersonDetailFacts? NormalizePersonDetails(PersonDetailFacts? details)
    {
        if (details is null)
        {
            return null;
        }

        return new PersonDetailFacts
        {
            WikidataQid = details.WikidataQid,
            WikidataUrl = details.WikidataUrl,
            Biography = details.Biography,
            Occupation = details.Occupation,
            Roles = details.Roles,
            DateOfBirth = details.DateOfBirth,
            DateOfDeath = details.DateOfDeath,
            PlaceOfBirth = details.PlaceOfBirth,
            PlaceOfDeath = details.PlaceOfDeath,
            Nationality = details.Nationality,
            IsPseudonym = details.IsPseudonym,
            IsGroup = details.IsGroup,
            CreatedAt = details.CreatedAt,
            EnrichedAt = details.EnrichedAt,
            ExternalLinks = details.ExternalLinks,
            Aliases = details.Aliases.Select(NormalizePersonRelatedLink).ToList(),
            GroupMembers = details.GroupMembers.Select(NormalizePersonRelatedLink).ToList(),
            MemberOfGroups = details.MemberOfGroups.Select(NormalizePersonRelatedLink).ToList(),
        };
    }

    private PersonRelatedLink NormalizePersonRelatedLink(PersonRelatedLink link) => new()
    {
        Id = link.Id,
        Name = link.Name,
        Subtitle = link.Subtitle,
        ImageUrl = NormalizeOptionalUrl(link.ImageUrl),
        Route = link.Route,
    };

    private CreditGroupViewModel NormalizeCreditGroup(CreditGroupViewModel group) => new()
    {
        Title = group.Title,
        GroupType = group.GroupType,
        Credits = group.Credits.Select(NormalizeCredit).ToList(),
    };

    private EntityCreditViewModel NormalizeCredit(EntityCreditViewModel credit) => new()
    {
        EntityId = credit.EntityId,
        EntityType = credit.EntityType,
        DisplayName = credit.DisplayName,
        ImageUrl = NormalizeOptionalUrl(credit.ImageUrl),
        FallbackInitials = credit.FallbackInitials,
        PrimaryRole = credit.PrimaryRole,
        SecondaryRole = credit.SecondaryRole,
        CharacterName = credit.CharacterName,
        CharacterEntityId = credit.CharacterEntityId,
        CharacterImageUrl = NormalizeOptionalUrl(credit.CharacterImageUrl),
        SortOrder = credit.SortOrder,
        IsPrimary = credit.IsPrimary,
        IsCanonical = credit.IsCanonical,
        SourceName = credit.SourceName,
        SourceId = credit.SourceId,
    };

    private SequencePlacementViewModel? NormalizeSequencePlacement(SequencePlacementViewModel? placement)
        => placement is null
            ? null
            : new SequencePlacementViewModel
            {
                ContainerId = placement.ContainerId,
                SourceContainerId = placement.SourceContainerId,
                ContainerTitle = placement.ContainerTitle,
                ContainerDescription = placement.ContainerDescription,
                ContainerWikipediaUrl = NormalizeOptionalUrl(placement.ContainerWikipediaUrl),
                SelectedContainerId = placement.SelectedContainerId,
                CanChooseContainer = placement.CanChooseContainer,
                CanSetDefaultContainer = placement.CanSetDefaultContainer,
                AvailableContainers = placement.AvailableContainers.Select(NormalizeSequenceContainerOption).ToList(),
                UniverseId = placement.UniverseId,
                UniverseTitle = placement.UniverseTitle,
                ContainerLabel = placement.ContainerLabel,
                ItemLabel = placement.ItemLabel,
                ItemPluralLabel = placement.ItemPluralLabel,
                GroupLabel = placement.GroupLabel,
                CurrentGroupKey = placement.CurrentGroupKey,
                PositionNumber = placement.PositionNumber,
                PositionSort = placement.PositionSort,
                TotalKnownItems = placement.TotalKnownItems,
                HasAuthoritativeTotal = placement.HasAuthoritativeTotal,
                PositionLabel = placement.PositionLabel,
                PositionText = placement.PositionText,
                PositionSummary = placement.PositionSummary,
                OrderingType = placement.OrderingType,
                PreviousItem = NormalizeSequenceItem(placement.PreviousItem),
                CurrentItem = NormalizeSequenceItem(placement.CurrentItem) ?? new SequenceItemViewModel(),
                NextItem = NormalizeSequenceItem(placement.NextItem),
                OrderedItems = placement.OrderedItems.Select(NormalizeSequenceItem).OfType<SequenceItemViewModel>().ToList(),
                Groups = placement.Groups.Select(NormalizeSequenceGroup).ToList(),
            };

    private static SequenceContainerOptionViewModel NormalizeSequenceContainerOption(SequenceContainerOptionViewModel option)
        => new()
        {
            ContainerId = option.ContainerId,
            SourceContainerId = option.SourceContainerId,
            ContainerTitle = option.ContainerTitle,
            IsSelected = option.IsSelected,
            IsDefault = option.IsDefault,
            MediaScope = option.MediaScope,
            EquivalentContainerIds = option.EquivalentContainerIds,
        };

    private SequenceGroupViewModel NormalizeSequenceGroup(SequenceGroupViewModel group)
        => new()
        {
            Key = group.Key,
            Title = group.Title,
            EntityId = group.EntityId,
            ArtworkSmallUrl = NormalizeOptionalUrl(group.ArtworkSmallUrl),
            ArtworkUrl = NormalizeOptionalUrl(group.ArtworkUrl),
            BackgroundArtworkUrl = NormalizeOptionalUrl(group.BackgroundArtworkUrl),
            TotalKnownItems = group.TotalKnownItems,
            HasAuthoritativeTotal = group.HasAuthoritativeTotal,
            Items = group.Items.Select(NormalizeSequenceItem).OfType<SequenceItemViewModel>().ToList(),
        };

    private SequenceItemViewModel? NormalizeSequenceItem(SequenceItemViewModel? item)
        => item is null
            ? null
            : new SequenceItemViewModel
            {
                Id = item.Id,
                EntityType = item.EntityType,
                Title = item.Title,
                ArtworkUrl = NormalizeOptionalUrl(item.ArtworkUrl),
                Route = item.Route,
                Description = item.Description,
                Duration = item.Duration,
                PublicationDate = item.PublicationDate,
                PositionNumber = item.PositionNumber,
                PositionSort = item.PositionSort,
                PositionLabel = item.PositionLabel,
                PositionText = item.PositionText,
                GroupKey = item.GroupKey,
                GroupTitle = item.GroupTitle,
                MembershipScope = item.MembershipScope,
                IsCurrent = item.IsCurrent,
                IsOwned = item.IsOwned,
                ProgressState = item.ProgressState,
            };

    private string? NormalizeOptionalUrl(string? value)
        => string.IsNullOrWhiteSpace(value) ? value : AbsoluteUrl(value);

    /// <summary>
    /// Converts browser-visible Engine artwork to an authenticated, same-origin
    /// Dashboard URL. Other relative Engine URLs remain absolute server URLs.
    /// </summary>
    private string AbsoluteUrl(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        return EngineImageProxyPath.ToBrowserUrl(value, _http.BaseAddress);
    }

    private static string GetImageContentType(string fileName) =>
        Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".webp" => "image/webp",
            _ => "image/jpeg",
        };

    private string? ResolvePersonHeadshotUrl(Guid personId, bool hasLocalHeadshot, string? headshotUrl) =>
        hasLocalHeadshot || !string.IsNullOrWhiteSpace(headshotUrl)
            ? AbsoluteUrl($"/persons/{personId}/headshot")
            : null;

    private static void AddQuery(ICollection<string> query, string name, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        query.Add($"{Uri.EscapeDataString(name)}={Uri.EscapeDataString(value)}");
    }

    private string NormalizeCanonicalValue(string key, string value) =>
        IsArtworkCanonicalKey(key)
            ? AbsoluteUrl(value)
            : value;

    private static bool IsArtworkCanonicalKey(string? key)
    {
        var normalized = key?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return false;
        }

        return normalized switch
        {
            "cover" or "cover_url" or
            "square" or "square_url" or
            "background" or "background_url" or
            "banner" or "banner_url" or
            "hero" or "hero_url" or
            "logo" or "logo_url" or
            "artist_photo_url" or "headshot_url" or
            "season_poster" or "season_poster_url" or
            "season_thumb" or "season_thumb_url" or
            "episode_still" or "episode_still_url" or
            "character_portrait" or "character_portrait_url" => true,
            _ when normalized.EndsWith("_url_s", StringComparison.Ordinal)
                || normalized.EndsWith("_url_m", StringComparison.Ordinal)
                || normalized.EndsWith("_url_l", StringComparison.Ordinal) => true,
            _ => false,
        };
    }

    private WorkViewModel MapLibraryWork(MediaEngine.Contracts.Collections.LibraryWorkListItemDto work)
    {
        var canonicalValues = (work.CanonicalValues ?? new())
            .Select(kv => new CanonicalValueViewModel
            {
                Key = kv.Key,
                Value = NormalizeCanonicalValue(kv.Key, kv.Value),
            })
            .ToList();

        return new WorkViewModel
        {
            Id = work.Id,
            CollectionId = work.CollectionId,
            RootWorkId = work.RootWorkId,
            AssetId = work.AssetId,
            MediaType = work.MediaType ?? "Unknown",
            WorkKind = work.WorkKind,
            Ordinal = work.Ordinal,
            CreatedAt = ParseDateTimeOffset(work.CreatedAt),
            ResolvedCoverUrl = work.CoverUrl is not null ? AbsoluteUrl(work.CoverUrl) : SelectCanonicalUrl(canonicalValues, "cover_url", "cover"),
            ResolvedBackgroundUrl = work.BackgroundUrl is not null ? AbsoluteUrl(work.BackgroundUrl) : SelectCanonicalUrl(canonicalValues, "background_url", "background"),
            ResolvedBannerUrl = work.BannerUrl is not null ? AbsoluteUrl(work.BannerUrl) : SelectCanonicalUrl(canonicalValues, "banner_url", "banner"),
            ResolvedHeroUrl = null,
            ResolvedLogoUrl = work.LogoUrl is not null ? AbsoluteUrl(work.LogoUrl) : SelectCanonicalUrl(canonicalValues, "logo_url", "logo"),
            CanonicalValues = canonicalValues,
        };
    }

    private WorkViewModel MapWork(MediaEngine.Contracts.Collections.WorkDto work)
    {
        var canonicalValues = work.CanonicalValues.Select(cv => new CanonicalValueViewModel
        {
            Key = cv.Key,
            Value = NormalizeCanonicalValue(cv.Key, cv.Value),
            LastScoredAt = cv.LastScoredAt,
        }).ToList();

        return new WorkViewModel
        {
            Id = work.Id,
            CollectionId = work.CollectionId,
            MediaType = work.MediaType,
            Ordinal = work.Ordinal,
            ResolvedCoverUrl = SelectCanonicalUrl(canonicalValues, "cover_url", "cover"),
            ResolvedBackgroundUrl = SelectCanonicalUrl(canonicalValues, "background_url", "background"),
            ResolvedBannerUrl = SelectCanonicalUrl(canonicalValues, "banner_url", "banner"),
            ResolvedHeroUrl = null,
            ResolvedLogoUrl = SelectCanonicalUrl(canonicalValues, "logo_url", "logo"),
            CanonicalValues = canonicalValues,
        };
    }

    private static DateTimeOffset ParseDateTimeOffset(string? value) =>
        DateTimeOffset.TryParse(value, out var parsed)
            ? parsed
            : DateTimeOffset.MinValue;

    private string? SelectCanonicalUrl(IEnumerable<CanonicalValueViewModel> values, params string[] keys)
    {
        foreach (var key in keys)
        {
            var match = values.FirstOrDefault(value => value.Key.Equals(key, StringComparison.OrdinalIgnoreCase))?.Value;
            if (!string.IsNullOrWhiteSpace(match))
            {
                return AbsoluteUrl(match);
            }
        }

        return null;
    }

    private CollectionViewModel MapCollection(MediaEngine.Contracts.Collections.CollectionDto h) => CollectionViewModel.FromApiDto(
        h.Id,
        h.UniverseId,
        h.CreatedAt,
        h.Works.Select(MapWork),
        displayName: h.DisplayName,
        parentCollectionId: h.ParentCollectionId,
        parentCollectionName: null,
        childCollectionCount: 0);

    private CollectionViewModel MapParentCollection(MediaEngine.Contracts.Collections.ParentCollectionDto h) => CollectionViewModel.FromParentCollection(
        h.Id,
        h.UniverseId,
        h.CreatedAt,
        displayName: h.DisplayName,
        description: h.Description,
        wikidataQid: h.WikidataQid,
        childCollectionCount: h.ChildCollectionCount,
        mediaTypes: h.MediaTypes,
        totalWorks: h.TotalWorks);

    // -- Raw response shapes (mirror API Dtos.cs) ------------------------------

    private sealed record StatusRaw(
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("version")] string Version,
        [property: JsonPropertyName("language")] string? Language);


    // -- Universe health + character data -------------------------------------

    public async Task<UniverseHealthDto?> GetUniverseHealthAsync(string qid, CancellationToken ct = default)
    {
        try
        {
            var raw = await _http.GetFromJsonAsync<UniverseHealthRaw>($"/universe/{Uri.EscapeDataString(qid)}/health", ct);
            if (raw is null)
            {
                return null;
            }

            return new UniverseHealthDto
            {
                Qid = raw.Qid ?? qid,
                Label = raw.Label ?? string.Empty,
                EntitiesTotal = raw.EntitiesTotal,
                EntitiesEnriched = raw.EntitiesEnriched,
                EntitiesWithImages = raw.EntitiesWithImages,
                RelationshipsTotal = raw.RelationshipsTotal,
                HealthPercent = raw.HealthPercent,
            };
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GET /universe/{Qid}/health failed", qid);
            return null;
        }
    }

    public async Task<IReadOnlyList<UniverseCharacterDto>> GetUniverseCharactersAsync(string universeQid, CancellationToken ct = default)
    {
        try
        {
            var raw = await _http.GetFromJsonAsync<List<UniverseCharacterRaw>>(
                $"/library/universes/{Uri.EscapeDataString(universeQid)}/characters", ct);
            if (raw is null)
            {
                return [];
            }

            return raw.Select(r => new UniverseCharacterDto
            {
                FictionalEntityId = r.FictionalEntityId,
                CharacterName = r.CharacterName ?? string.Empty,
                DefaultActorName = r.DefaultActorName,
                DefaultActorId = r.DefaultActorId,
                PortraitUrl = r.PortraitUrl,
                ActorCount = r.ActorCount,
            }).ToList();
        }
        catch (OperationCanceledException) { return []; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GET /library/universes/{Qid}/characters failed", universeQid);
            return [];
        }
    }

    public async Task<IReadOnlyList<CharacterRoleDto>> GetPersonCharacterRolesAsync(Guid personId, CancellationToken ct = default)
    {
        try
        {
            var raw = await _http.GetFromJsonAsync<List<PersonCharacterRoleDto>>(
                $"/library/persons/{personId}/character-roles", ct);
            if (raw is null)
            {
                return [];
            }

            return raw.Select(r => new CharacterRoleDto
            {
                FictionalEntityId = r.FictionalEntityId,
                CharacterName = r.CharacterName,
                PortraitUrl = r.PortraitUrl is not null ? AbsoluteUrl(r.PortraitUrl) : null,
                WorkId = r.WorkId,
                WorkQid = r.WorkQid,
                WorkTitle = r.WorkTitle,
                CollectionId = r.CollectionId,
                MediaType = r.MediaType,
                IsDefault = r.IsDefault,
                UniverseQid = r.UniverseQid,
                UniverseLabel = r.UniverseLabel,
            }).ToList();
        }
        catch (OperationCanceledException) { return []; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GET /library/persons/{PersonId}/character-roles failed", personId);
            return [];
        }
    }

    public async Task<List<CastCreditDto>> GetWorkCastAsync(Guid workId, CancellationToken ct = default)
    {
        try
        {
            var cast = await _http.GetFromJsonAsync<List<CastCreditDto>>(
                $"/works/{workId}/cast", ct);

            return NormalizeCastCredits(cast);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GET /works/{WorkId}/cast failed", workId);
            LastError = ex.Message;
            return [];
        }
    }

    public async Task<ArtworkEditorDto?> GetArtworkAsync(Guid entityId, CancellationToken ct = default)
    {
        try
        {
            var raw = await _http.GetFromJsonAsync<ArtworkEditorDto>($"/metadata/{entityId}/artwork", ct);
            if (raw is null)
            {
                return null;
            }

            return NormalizeArtworkEditor(raw);
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GET /metadata/{EntityId}/artwork failed", entityId);
            return null;
        }
    }

    public async Task<ArtworkEditorDto?> GetScopeArtworkAsync(Guid entityId, string scopeId, CancellationToken ct = default)
    {
        try
        {
            var encodedScope = Uri.EscapeDataString(scopeId);
            var raw = await _http.GetFromJsonAsync<ArtworkEditorDto>($"/metadata/{entityId}/artwork/{encodedScope}", ct);
            if (raw is null)
            {
                return null;
            }

            return NormalizeArtworkEditor(raw);
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GET /metadata/{EntityId}/artwork/{ScopeId} failed", entityId, scopeId);
            return null;
        }
    }

    private ArtworkEditorDto NormalizeArtworkEditor(ArtworkEditorDto raw)
    {
        foreach (var slot in raw.Slots)
        {
            slot.AssetType ??= string.Empty;
            foreach (var variant in slot.Variants)
            {
                variant.AssetType = string.IsNullOrWhiteSpace(variant.AssetType)
                    ? slot.AssetType
                    : variant.AssetType;
                variant.ImageUrl = variant.ImageUrl is null ? null : AbsoluteUrl(variant.ImageUrl);
                variant.Origin = string.IsNullOrWhiteSpace(variant.Origin) ? "Stored" : variant.Origin;
            }
        }

        return raw;
    }

    public async Task<ProviderArtworkRefreshDto?> RefreshScopeProviderArtworkAsync(Guid entityId, string scopeId, CancellationToken ct = default)
    {
        var encodedScope = Uri.EscapeDataString(scopeId);
        var result = await PostAsync<object, ProviderArtworkRefreshDto>(
            "POST /metadata/{entityId}/artwork/{scopeId}/refresh-provider",
            $"/metadata/{entityId}/artwork/{encodedScope}/refresh-provider",
            new { },
            ct: ct);
        if (result is not null)
        {
            result.StoredVariantCounts = new Dictionary<string, int>(
                result.StoredVariantCounts,
                StringComparer.OrdinalIgnoreCase);
        }

        return result;
    }

    public async Task TriggerUniverseEnrichmentAsync(CancellationToken ct = default)
    {
        try
        {
            await _http.PostAsJsonAsync("/library/enrichment/universe/trigger", new { }, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "POST /library/enrichment/universe/trigger failed");
        }
    }

    // -- Raw deserialization models (character/universe health) ----------------

    private sealed class UniverseHealthRaw
    {
        [JsonPropertyName("qid")] public string? Qid { get; set; }
        [JsonPropertyName("label")] public string? Label { get; set; }
        [JsonPropertyName("entities_total")] public int EntitiesTotal { get; set; }
        [JsonPropertyName("entities_enriched")] public int EntitiesEnriched { get; set; }
        [JsonPropertyName("entities_with_images")] public int EntitiesWithImages { get; set; }
        [JsonPropertyName("relationships_total")] public int RelationshipsTotal { get; set; }
        [JsonPropertyName("health_percent")] public double HealthPercent { get; set; }
    }

    private sealed class UniverseCharacterRaw
    {
        [JsonPropertyName("fictional_entity_id")] public Guid FictionalEntityId { get; set; }
        [JsonPropertyName("character_name")] public string? CharacterName { get; set; }
        [JsonPropertyName("default_actor_name")] public string? DefaultActorName { get; set; }
        [JsonPropertyName("default_actor_id")] public Guid? DefaultActorId { get; set; }
        [JsonPropertyName("portrait_url")] public string? PortraitUrl { get; set; }
        [JsonPropertyName("actor_count")] public int ActorCount { get; set; }
    }

}
