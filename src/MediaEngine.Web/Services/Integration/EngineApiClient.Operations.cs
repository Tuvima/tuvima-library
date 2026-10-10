using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using MediaEngine.Contracts.Details;
using MediaEngine.Contracts.Display;
using MediaEngine.Contracts.Maintenance;
using MediaEngine.Contracts.Operations;
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
    // -- POST /ingestion/scan --------------------------------------------------

    public async Task<WorkDetailViewModel?> GetWorkDetailAsync(Guid workId, CancellationToken ct = default)
    {
        try
        {
            var detail = await _http.GetFromJsonAsync<MediaEngine.Contracts.Collections.WorkDetailDto>(
                $"/works/{workId:D}", ct);
            return detail is null ? null : new WorkDetailViewModel
            {
                Id = detail.Id,
                CollectionId = detail.CollectionId,
                ParentWorkId = detail.ParentWorkId,
                MediaType = detail.MediaType,
                WorkKind = detail.WorkKind,
                Ordinal = detail.Ordinal,
                IsCatalogOnly = detail.IsCatalogOnly,
                WikidataQid = detail.WikidataQid,
                CanonicalValues = detail.CanonicalValues.Select(MapCanonicalValue).ToList(),
                Editions = detail.Editions.Select(edition => new EditionViewModel
                {
                    Id = edition.Id,
                    WorkId = edition.WorkId,
                    FormatLabel = edition.FormatLabel,
                    WikidataQid = edition.WikidataQid,
                    CanonicalValues = edition.CanonicalValues.Select(MapCanonicalValue).ToList(),
                    Assets = edition.Assets.Select(asset => new EditionAssetViewModel
                    {
                        Id = asset.Id,
                        EditionId = asset.EditionId,
                        FilePathRoot = asset.FilePathRoot,
                        Status = asset.Status,
                        CanonicalValues = asset.CanonicalValues.Select(MapCanonicalValue).ToList(),
                    }).ToList(),
                }).ToList(),
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GET /works/{WorkId} failed", workId);
            LastError = ex.Message;
            return null;
        }
    }

    private static CanonicalValueViewModel MapCanonicalValue(
        MediaEngine.Contracts.Collections.CanonicalValueDto value) => new()
        {
            Key = value.Key,
            Value = value.Value,
            LastScoredAt = value.LastScoredAt,
        };

    public async Task<List<EditionViewModel>> GetWorkEditionsAsync(Guid workId, CancellationToken ct = default)
    {
        try
        {
            var response = await _http.GetFromJsonAsync<List<MediaEngine.Contracts.Collections.EditionDto>>(
                $"/works/{workId:D}/editions",
                ct);
            return response?.Select(MapEdition).ToList() ?? [];
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GET /works/{WorkId}/editions failed", workId);
            LastError = ex.Message;
            return [];
        }
    }

    private static EditionViewModel MapEdition(MediaEngine.Contracts.Collections.EditionDto edition) => new()
    {
        Id = edition.Id,
        WorkId = edition.WorkId,
        FormatLabel = edition.FormatLabel,
        WikidataQid = edition.WikidataQid,
        CanonicalValues = edition.CanonicalValues.Select(MapCanonicalValue).ToList(),
        Assets = edition.Assets.Select(asset => new EditionAssetViewModel
        {
            Id = asset.Id,
            EditionId = asset.EditionId,
            FilePathRoot = asset.FilePathRoot,
            Status = asset.Status,
            CanonicalValues = asset.CanonicalValues.Select(MapCanonicalValue).ToList(),
        }).ToList(),
    };
    public Task<ScanResponse?> TriggerScanAsync(
        string? rootPath = null,
        CancellationToken ct = default) =>
        PostAsync<ScanRequest, ScanResponse>(
            "POST /ingestion/scan",
            "/ingestion/scan",
            new ScanRequest { RootPath = rootPath },
            ct: ct);

    // -- POST /ingestion/reconcile ---------------------------------------------

    public async Task<ReconciliationResultResponse?> TriggerReconciliationAsync(
        CancellationToken ct = default)
    {
        try
        {
            var resp = await _http.PostAsJsonAsync("/ingestion/reconcile", new { }, ct);
            if (!resp.IsSuccessStatusCode)
            {
                return null;
            }

            return await resp.Content.ReadFromJsonAsync<ReconciliationResultResponse>(ct);
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "POST /ingestion/reconcile failed");
            return null;
        }
    }

    public Task<FileMetadataRereadResponse?> RereadAssetMetadataAsync(
        Guid assetId,
        CancellationToken ct = default) =>
        PostAsync<object, FileMetadataRereadResponse>(
            "POST /ingestion/assets/{assetId}/reread-metadata",
            $"/ingestion/assets/{assetId:D}/reread-metadata",
            new { },
            ct: ct);

    // -- GET /ingestion/watch-folder --------------------------------------------

    public async Task<List<WatchFolderFileDto>> GetWatchFolderAsync(CancellationToken ct = default)
    {
        try
        {
            var page = await _http.GetFromJsonAsync<WatchFolderPageResponse>("/ingestion/watch-folder", ct);
            return page?.Files.ToList() ?? [];
        }
        catch (OperationCanceledException) { return []; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GET /ingestion/watch-folder failed");
            return [];
        }
    }

    // -- POST /ingestion/rescan ----------------------------------------------

    // Migrated to the shared PostAsync<TReq> bool-returning helper (stage 5B wave 2).
    public Task<bool> TriggerRescanAsync(
        string? rootPath = null,
        bool? includeSubdirectories = null,
        CancellationToken ct = default) =>
        PostAsync<RescanRequest>(
            "POST /ingestion/rescan",
            "/ingestion/rescan",
            new RescanRequest
            {
                RootPath = rootPath,
                IncludeSubdirectories = includeSubdirectories,
            },
            ct: ct);

    // -- /metadata/hydrate ------------------------------------------------------

    public async Task<HydrateResultViewModel?> TriggerHydrationAsync(
        Guid entityId, CancellationToken ct = default)
    {
        try
        {
            var resp = await _http.PostAsJsonAsync($"/metadata/hydrate/{entityId}", new { }, ct);
            if (!resp.IsSuccessStatusCode)
            {
                return null;
            }

            var result = await resp.Content.ReadFromJsonAsync<MediaEngine.Contracts.Metadata.HydrateResponse>(ct);
            return result is null ? null : new HydrateResultViewModel
            {
                WikidataQid = result.WikidataQid,
                ClaimsAdded = result.ClaimsAdded,
                Success = result.Success,
                Message = result.Message,
            };
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "POST /metadata/hydrate/{EntityId} failed", entityId);
            return null;
        }
    }

    public async Task<EnrichmentRefreshScheduleResponse?> GetEnrichmentRefreshScheduleAsync(
        string? entityType = null,
        string? status = null,
        int limit = 250,
        CancellationToken ct = default)
    {
        var query = new Dictionary<string, string?>
        {
            ["limit"] = Math.Clamp(limit, 1, 1000).ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["entityType"] = entityType,
            ["status"] = status,
        };
        return await GetAsync<EnrichmentRefreshScheduleResponse>(
            "GET /ingestion/refresh-schedule",
            "/ingestion/refresh-schedule",
            query,
            ct: ct);
    }

    public Task<EnrichmentRefreshScheduleDto?> GetEntityEnrichmentRefreshScheduleAsync(
        string entityType,
        Guid entityId,
        CancellationToken ct = default) =>
        GetAsync<EnrichmentRefreshScheduleDto>(
            "GET /ingestion/refresh-schedule/{entityType}/{entityId}",
            $"/ingestion/refresh-schedule/{Uri.EscapeDataString(entityType)}/{entityId:D}",
            ct: ct);

    public async Task<EnrichmentRefreshQueuedResponse?> QueueEnrichmentRefreshNowAsync(
        string entityType,
        Guid entityId,
        CancellationToken ct = default)
    {
        return await PostAsync<object, EnrichmentRefreshQueuedResponse>(
            "POST /ingestion/refresh-schedule/{entityType}/{entityId}/run-now",
            $"/ingestion/refresh-schedule/{Uri.EscapeDataString(entityType)}/{entityId:D}/run-now",
            new { },
            ct: ct);
    }

    // -- /metadata/conflicts ----------------------------------------------------

    public async Task<List<ConflictViewModel>> GetConflictsAsync(CancellationToken ct = default)
    {
        try
        {
            var raw = await _http.GetFromJsonAsync<List<MediaEngine.Contracts.Metadata.ConflictDto>>(
                "/metadata/conflicts", ct);
            return raw?.Select(item => new ConflictViewModel(
                item.EntityId,
                item.Key,
                item.Value,
                item.LastScoredAt)).ToList() ?? [];
        }
        catch (OperationCanceledException) { return []; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GET /metadata/conflicts failed");
            return [];
        }
    }

    // -- POST /dev/seed-library -----------------------------------------

    public async Task<bool> SeedLibraryAsync(CancellationToken ct = default)
    {
        try
        {
            var response = await _http.PostAsync("/dev/seed-library", null, ct);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "POST /dev/seed-library failed");
            LastError = ex.Message;
            return false;
        }
    }

    public async Task<MediaEngine.Contracts.Items.ItemEditorPreferencesResponse?> GetItemEditorPreferencesAsync(
        Guid entityId, Guid profileId, CancellationToken ct = default)
    {
        try
        {
            var response = await _http.GetAsync($"/library/items/{entityId}/editor-preferences/{profileId}", ct);
            if (!response.IsSuccessStatusCode)
            {
                var detail = await response.Content.ReadAsStringAsync(ct);
                LastError = $"HTTP {(int)response.StatusCode}: {detail}";
                _logger.LogWarning("GET editor preferences for {EntityId}/{ProfileId} returned {Status}: {Detail}",
                    entityId, profileId, (int)response.StatusCode, detail);
                return null;
            }

            return await response.Content.ReadFromJsonAsync<MediaEngine.Contracts.Items.ItemEditorPreferencesResponse>(
                cancellationToken: ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GET editor preferences for {EntityId}/{ProfileId} failed", entityId, profileId);
            LastError = ex.Message;
            return null;
        }
    }

    public async Task<ItemEditorPreferencesSaveResultDto> SaveItemEditorPreferencesAsync(
        Guid entityId,
        Guid profileId,
        MediaEngine.Contracts.Items.ItemEditorPreferencesRequest request,
        CancellationToken ct = default)
    {
        try
        {
            var response = await _http.PutAsJsonAsync(
                $"/library/items/{entityId}/editor-preferences/{profileId}", request, ct);
            var result = await response.Content.ReadFromJsonAsync<MediaEngine.Contracts.Items.ItemEditorPreferencesResponse>(
                cancellationToken: ct);
            if (response.StatusCode == System.Net.HttpStatusCode.Conflict)
            {
                return new ItemEditorPreferencesSaveResultDto(false, true, result, "Source metadata or profile preferences changed while you were editing.");
            }

            if (!response.IsSuccessStatusCode)
            {
                var detail = result is null ? await response.Content.ReadAsStringAsync(ct) : null;
                LastError = $"HTTP {(int)response.StatusCode}: {detail}";
                _logger.LogWarning("PUT editor preferences for {EntityId}/{ProfileId} returned {Status}: {Detail}",
                    entityId, profileId, (int)response.StatusCode, detail);
                return new ItemEditorPreferencesSaveResultDto(false, false, result, detail ?? "Editor preferences could not be saved.");
            }

            return new ItemEditorPreferencesSaveResultDto(true, false, result, null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PUT editor preferences for {EntityId}/{ProfileId} failed", entityId, profileId);
            LastError = ex.Message;
            return new ItemEditorPreferencesSaveResultDto(false, false, null, ex.Message);
        }
    }

    public async Task<IReadOnlyList<string>> GetItemEditorSuggestionsAsync(
        string field,
        Guid? profileId = null,
        CancellationToken ct = default)
    {
        try
        {
            var encodedField = Uri.EscapeDataString(field);
            var profileQuery = profileId.HasValue ? $"?profileId={profileId.Value:D}" : string.Empty;
            using var response = await _http.GetAsync($"/library/items/editor-suggestions/{encodedField}{profileQuery}", ct);
            if (!response.IsSuccessStatusCode)
            {
                return [];
            }

            return await response.Content.ReadFromJsonAsync<List<string>>(cancellationToken: ct) ?? [];
        }
        catch (OperationCanceledException)
        {
            return [];
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GET editor suggestions for {Field} failed", field);
            return [];
        }
    }

    public async Task<DevHarnessRunResult?> RunDevHarnessAsync(
        string path,
        IReadOnlyDictionary<string, string?>? query = null,
        CancellationToken ct = default)
    {
        var endpointPath = BuildEndpointPath(path, query);
        var endpoint = $"POST {endpointPath}";
        var started = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();

        try
        {
            using var response = await _http.PostAsync(endpointPath, null, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            sw.Stop();

            var result = new DevHarnessRunResult(
                response.IsSuccessStatusCode,
                (int)response.StatusCode,
                endpoint,
                response.Content.Headers.ContentType?.MediaType,
                body,
                started,
                DateTimeOffset.UtcNow,
                sw.ElapsedMilliseconds);

            if (response.IsSuccessStatusCode)
            {
                ClearFailure(endpoint);
            }
            else
            {
                _failureState.RecordRawFailure(endpoint, (int)response.StatusCode, SummarizeResponseBody(body));
            }

            return result;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            sw.Stop();
            RecordExceptionFailure(endpoint, ex);

            return new DevHarnessRunResult(
                false,
                0,
                endpoint,
                null,
                ex.Message,
                started,
                DateTimeOffset.UtcNow,
                sw.ElapsedMilliseconds);
        }
    }

    // -- Pass 2 (Universe Lookup) ----------------------------------------------

    public async Task<DeferredEnrichmentStatusResponse?> GetPass2StatusAsync(CancellationToken ct = default)
    {
        try
        {
            return await _http.GetFromJsonAsync<DeferredEnrichmentStatusResponse>("/metadata/pass2/status", ct);
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GET /metadata/pass2/status failed");
            return null;
        }
    }

    public async Task<DeferredEnrichmentTriggerResponse?> TriggerPass2NowAsync(CancellationToken ct = default)
    {
        try
        {
            var resp = await _http.PostAsJsonAsync("/metadata/pass2/trigger", new { }, ct);
            if (!resp.IsSuccessStatusCode)
            {
                return null;
            }

            return await resp.Content.ReadFromJsonAsync<DeferredEnrichmentTriggerResponse>(ct);
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "POST /metadata/pass2/trigger failed");
            return null;
        }
    }

    // -- Retag Sweep (Auto re-tag) ---------------------------------------------

    public async Task<RetagSweepStateResponse?> GetRetagSweepStateAsync(CancellationToken ct = default)
    {
        try
        {
            using var response = await _http.GetAsync("/maintenance/retag-sweep/state", ct);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            return await response.Content.ReadFromJsonAsync<RetagSweepStateResponse>(
                cancellationToken: ct);
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GET /maintenance/retag-sweep/state failed");
            return null;
        }
    }

    public async Task<bool> ApplyRetagSweepPendingAsync(CancellationToken ct = default)
    {
        try
        {
            var resp = await _http.PostAsync("/maintenance/retag-sweep/apply", content: null, ct);
            return resp.IsSuccessStatusCode;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "POST /maintenance/retag-sweep/apply failed");
            return false;
        }
    }

    public async Task<bool> RunRetagSweepNowAsync(CancellationToken ct = default)
    {
        try
        {
            var resp = await _http.PostAsync("/maintenance/retag-sweep/run-now", content: null, ct);
            return resp.IsSuccessStatusCode;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "POST /maintenance/retag-sweep/run-now failed");
            return false;
        }
    }

    public async Task<bool> RunInitialSweepAsync(CancellationToken ct = default)
    {
        try
        {
            var resp = await _http.PostAsync("/maintenance/initial-sweep/run", content: null, ct);
            return resp.IsSuccessStatusCode;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "POST /maintenance/initial-sweep/run failed");
            return false;
        }
    }

    public async Task<bool> RetryRetagForAssetAsync(Guid assetId, CancellationToken ct = default)
    {
        try
        {
            var resp = await _http.PostAsync($"/maintenance/retag-sweep/retry/{assetId}", content: null, ct);
            return resp.IsSuccessStatusCode;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "POST /maintenance/retag-sweep/retry/{Id} failed", assetId);
            return false;
        }
    }

    // -- Library items (/library/items) -------------------------------------------

    public async Task<LibraryCatalogPageResponse?> GetLibraryCatalogItemsAsync(
        int offset = 0, int limit = 50,
        string? search = null, string? type = null, string? status = null,
        double? minConfidence = null, string? matchSource = null,
        bool? duplicatesOnly = null, bool? missingUniverseOnly = null,
        string? sort = null, int? maxDays = null,
        CancellationToken ct = default)
    {
        try
        {
            var url = $"/library/items?offset={offset}&limit={limit}";
            if (!string.IsNullOrWhiteSpace(search))
            {
                url += $"&search={Uri.EscapeDataString(search)}";
            }

            if (!string.IsNullOrWhiteSpace(type))
            {
                url += $"&type={Uri.EscapeDataString(type)}";
            }

            if (!string.IsNullOrWhiteSpace(status))
            {
                url += $"&status={Uri.EscapeDataString(status)}";
            }

            if (minConfidence.HasValue)
            {
                url += $"&minConfidence={minConfidence.Value}";
            }

            if (!string.IsNullOrWhiteSpace(matchSource))
            {
                url += $"&matchSource={Uri.EscapeDataString(matchSource)}";
            }

            if (duplicatesOnly == true)
            {
                url += "&duplicatesOnly=true";
            }

            if (missingUniverseOnly == true)
            {
                url += "&missingUniverseOnly=true";
            }

            if (!string.IsNullOrWhiteSpace(sort))
            {
                url += $"&sort={Uri.EscapeDataString(sort)}";
            }

            if (maxDays.HasValue)
            {
                url += $"&maxDays={maxDays.Value}";
            }

            var transport = await _http.GetFromJsonAsync<MediaEngine.Contracts.Items.LibraryItemsPageDto>(url, ct);
            var response = transport?.ToViewModel();
            if (response?.Items is not null)
            {
                foreach (var item in response.Items)
                {
                    if (item.CoverUrl is not null)
                    {
                        item.CoverUrl = AbsoluteUrl(item.CoverUrl);
                    }

                    if (item.BackgroundUrl is not null)
                    {
                        item.BackgroundUrl = AbsoluteUrl(item.BackgroundUrl);
                    }

                    if (item.BannerUrl is not null)
                    {
                        item.BannerUrl = AbsoluteUrl(item.BannerUrl);
                    }

                    if (item.HeroUrl is not null)
                    {
                        item.HeroUrl = AbsoluteUrl(item.HeroUrl);
                    }
                }
            }
            return response;
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GET /library/items failed");
            LastError = ex.Message;
            return null;
        }
    }

    public async Task<BatchLibraryItemResponse?> BatchApproveLibraryCatalogItemsAsync(Guid[] entityIds, CancellationToken ct = default)
    {
        try
        {
            var request = new MediaEngine.Contracts.Items.BatchLibraryItemRequest { EntityIds = entityIds };
            var response = await _http.PostAsJsonAsync("/library/items/batch/approve", request, ct);
            response.EnsureSuccessStatusCode();
            return (await response.Content.ReadFromJsonAsync<MediaEngine.Contracts.Items.BatchLibraryItemResponse>(ct))
                ?.ToViewModel();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Batch approve failed");
            return null;
        }
    }

    public async Task<BatchLibraryItemResponse?> BatchDeleteLibraryCatalogItemsAsync(Guid[] entityIds, CancellationToken ct = default)
    {
        try
        {
            var request = new MediaEngine.Contracts.Items.BatchLibraryItemRequest { EntityIds = entityIds };
            var response = await _http.PostAsJsonAsync("/library/items/batch/delete", request, ct);
            response.EnsureSuccessStatusCode();
            return (await response.Content.ReadFromJsonAsync<MediaEngine.Contracts.Items.BatchLibraryItemResponse>(ct))
                ?.ToViewModel();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Batch delete failed");
            return null;
        }
    }

    public async Task<BatchLibraryItemResponse?> RejectLibraryCatalogItemAsync(Guid entityId, CancellationToken ct = default)
    {
        try
        {
            var response = await _http.PostAsJsonAsync($"/library/items/{entityId}/reject", new { }, ct);
            response.EnsureSuccessStatusCode();
            return (await response.Content.ReadFromJsonAsync<MediaEngine.Contracts.Items.BatchLibraryItemResponse>(ct))
                ?.ToViewModel();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Reject libraryItem item {EntityId} failed", entityId);
            return null;
        }
    }

    public async Task<BatchLibraryItemResponse?> BatchRejectLibraryCatalogItemsAsync(Guid[] entityIds, CancellationToken ct = default)
    {
        try
        {
            var request = new MediaEngine.Contracts.Items.BatchLibraryItemRequest { EntityIds = entityIds };
            var response = await _http.PostAsJsonAsync("/library/items/batch/reject", request, ct);
            response.EnsureSuccessStatusCode();
            return (await response.Content.ReadFromJsonAsync<MediaEngine.Contracts.Items.BatchLibraryItemResponse>(ct))
                ?.ToViewModel();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Batch reject failed");
            return null;
        }
    }

    public async Task<LibraryItemDetailViewModel?> GetLibraryItemDetailAsync(
        Guid entityId, CancellationToken ct = default)
    {
        var endpoint = $"GET /library/items/{entityId}/detail";
        try
        {
            var response = await _http.GetAsync($"/library/items/{entityId}/detail", ct);
            if (!response.IsSuccessStatusCode)
            {
                await RecordHttpFailureAsync(endpoint, response, ct);
                return null;
            }

            var transport = await response.Content.ReadFromJsonAsync<MediaEngine.Contracts.Items.LibraryItemDetailDto>(
                cancellationToken: ct);
            var detail = transport?.ToViewModel();
            if (detail?.CoverUrl is not null)
            {
                detail.CoverUrl = AbsoluteUrl(detail.CoverUrl);
            }

            if (detail?.BackgroundUrl is not null)
            {
                detail.BackgroundUrl = AbsoluteUrl(detail.BackgroundUrl);
            }

            if (detail?.BannerUrl is not null)
            {
                detail.BannerUrl = AbsoluteUrl(detail.BannerUrl);
            }

            if (detail?.HeroUrl is not null)
            {
                detail.HeroUrl = AbsoluteUrl(detail.HeroUrl);
            }

            ClearFailure(endpoint);
            return detail;
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GET /library/items/{EntityId}/detail failed", entityId);
            RecordExceptionFailure(endpoint, ex);
            return null;
        }
    }

    public async Task<LibraryItemStatusCountsDto?> GetLibraryItemStatusCountsAsync(CancellationToken ct = default)
    {
        try
        {
            var counts = await _http.GetFromJsonAsync<MediaEngine.Contracts.Items.LibraryItemStatusCountsDto>(
                "/library/items/counts", ct);
            return counts is null ? null : new LibraryItemStatusCountsDto
            {
                Total = counts.Total,
                NeedsReview = counts.NeedsReview,
                AutoApproved = counts.AutoApproved,
                Edited = counts.Edited,
                Duplicate = counts.Duplicate,
                Staging = counts.Staging,
                MissingImages = counts.MissingImages,
                RecentlyUpdated = counts.RecentlyUpdated,
                LowConfidence = counts.LowConfidence,
                Rejected = counts.Rejected,
            };
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GET /library/items/counts failed");
            LastError = ex.Message;
            return null;
        }
    }

    /// <inheritdoc/>
    public async Task<LibraryItemLifecycleCountsDto?> GetLibraryItemLifecycleCountsAsync(
        Guid? batchId = null, CancellationToken ct = default)
    {
        try
        {
            var url = batchId.HasValue
                ? $"/library/items/state-counts?batchId={batchId.Value}"
                : "/library/items/state-counts";
            var counts = await _http.GetFromJsonAsync<MediaEngine.Contracts.Items.LibraryItemLifecycleCountsDto>(url, ct);
            return counts is null ? null : new LibraryItemLifecycleCountsDto
            {
                Identified = counts.Identified,
                InReview = counts.InReview,
                Provisional = counts.Provisional,
                Rejected = counts.Rejected,
                PersonCount = counts.PersonCount,
                CollectionCount = counts.CollectionCount,
                TriggerCounts = counts.TriggerCounts,
            };
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GET /library/items/state-counts failed");
            LastError = ex.Message;
            return null;
        }
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<IngestionBatchResponse>> GetIngestionBatchesAsync(
        int limit = 20, CancellationToken ct = default)
    {
        try
        {
            var result = await _http.GetFromJsonAsync<List<IngestionBatchResponse>>(
                $"ingestion/batches?limit={limit}", ct).ConfigureAwait(false);
            return result ?? [];
        }
        catch (OperationCanceledException) { return []; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch ingestion batches");
            return [];
        }
    }

    /// <inheritdoc/>
    public Task<List<IngestionOperationsBatchDto>?> GetIngestionNotificationStatusAsync(CancellationToken ct = default) =>
        GetAsync<List<IngestionOperationsBatchDto>>("Ingestion notifications", "ingestion/notification-status", ct: ct);

    public async Task<IngestionOperationsSnapshotDto?> GetIngestionOperationsSnapshotAsync(CancellationToken ct = default)
    {
        try
        {
            return await _http.GetFromJsonAsync<IngestionOperationsSnapshotDto>(
                "ingestion/operations", ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch ingestion operations snapshot");
            LastError = ex.Message;
            return null;
        }
    }

    public async Task<IngestionPresentationSnapshotDto?> GetIngestionPresentationAsync(CancellationToken ct = default)
    {
        var result = await GetAsync<IngestionPresentationSnapshotDto>(
            "Ingestion presentation", "ingestion/presentation", ct: ct).ConfigureAwait(false);
        NormalizePresentationUrls(result?.CurrentMedia);
        if (result is not null)
        {
            foreach (var day in result.RecentDays)
            {
                NormalizePresentationUrls(day.Items);
            }
        }

        return result;
    }

    public Task<PagedResponse<IngestionMediaGroupDto>?> GetCurrentIngestionMediaAsync(int offset = 0, int limit = 50, CancellationToken ct = default) =>
        GetPresentationPageAsync($"ingestion/media-groups?offset={Math.Max(0, offset)}&limit={Math.Clamp(limit, 1, 100)}", ct);

    public Task<PagedResponse<IngestionMediaGroupDto>?> GetRecentIngestionAdditionsAsync(
        string? search = null, string? lane = null, DateTimeOffset? start = null, DateTimeOffset? end = null,
        int offset = 0, int limit = 50, string? sort = null, CancellationToken ct = default)
    {
        var values = new List<string>
        {
            $"offset={Math.Max(0, offset)}",
            $"limit={Math.Clamp(limit, 1, 100)}",
        };
        if (!string.IsNullOrWhiteSpace(search))
        {
            values.Add($"search={Uri.EscapeDataString(search)}");
        }

        if (!string.IsNullOrWhiteSpace(lane))
        {
            values.Add($"lane={Uri.EscapeDataString(lane)}");
        }

        if (start.HasValue)
        {
            values.Add($"start={Uri.EscapeDataString(start.Value.ToString("O"))}");
        }

        if (end.HasValue)
        {
            values.Add($"end={Uri.EscapeDataString(end.Value.ToString("O"))}");
        }

        if (!string.IsNullOrWhiteSpace(sort) && !sort.Equals("newest", StringComparison.OrdinalIgnoreCase))
        {
            values.Add($"sort={Uri.EscapeDataString(sort)}");
        }

        return GetPresentationPageAsync($"ingestion/recent-additions?{string.Join('&', values)}", ct);
    }

    public async Task<IngestionMediaGroupDto?> GetIngestionMediaGroupAsync(Guid batchId, Guid groupId, CancellationToken ct = default)
    {
        var result = await GetAsync<IngestionMediaGroupDto>(
            "Ingestion media group",
            $"ingestion/batches/{batchId:D}/media-groups/{groupId:D}",
            ct: ct).ConfigureAwait(false);
        NormalizePresentationUrl(result);
        return result;
    }

    public Task<PagedResponse<IngestionMediaChildDto>?> GetIngestionMediaChildrenAsync(
        Guid batchId, Guid groupId, int offset = 0, int limit = 50, CancellationToken ct = default)
        => GetAsync<PagedResponse<IngestionMediaChildDto>>(
            "Ingestion media children",
            $"ingestion/batches/{batchId:D}/media-groups/{groupId:D}/children?offset={Math.Max(0, offset)}&limit={Math.Clamp(limit, 1, 250)}",
            ct: ct);

    public async Task<ActivityHistorySummaryDto?> GetActivityHistorySummaryAsync(CancellationToken ct = default)
        => await GetAsync<ActivityHistorySummaryDto>(
            "Activity history summary", "activity/summary", ct: ct).ConfigureAwait(false);

    public async Task<ActivityBatchPresentationDto?> GetActivityBatchPresentationAsync(Guid batchId, CancellationToken ct = default)
    {
        var result = await GetAsync<ActivityBatchPresentationDto>(
            "Activity batch presentation",
            $"activity/batches/{batchId:D}/presentation",
            ct: ct).ConfigureAwait(false);
        NormalizePresentationUrls(result?.AddedPreview);
        return result;
    }

    public Task<PagedResponse<IngestionMediaGroupDto>?> GetActivityBatchMediaGroupsAsync(
        Guid batchId,
        int offset = 0,
        int limit = 50,
        string? search = null,
        string? lane = null,
        string? sort = null,
        CancellationToken ct = default)
    {
        var query = new Dictionary<string, string?>
        {
            ["offset"] = Math.Max(0, offset).ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["limit"] = Math.Clamp(limit, 1, 100).ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["search"] = string.IsNullOrWhiteSpace(search) ? null : search.Trim(),
            ["lane"] = string.IsNullOrWhiteSpace(lane) || lane.Equals("all", StringComparison.OrdinalIgnoreCase) ? null : lane,
            ["sort"] = sort?.Equals("oldest", StringComparison.OrdinalIgnoreCase) == true ? "oldest" : null,
        };
        return GetPresentationPageAsync(Microsoft.AspNetCore.WebUtilities.QueryHelpers.AddQueryString($"activity/batches/{batchId:D}/media-groups", query), ct);
    }

    private async Task<PagedResponse<IngestionMediaGroupDto>?> GetPresentationPageAsync(string path, CancellationToken ct)
    {
        var result = await GetAsync<PagedResponse<IngestionMediaGroupDto>>(
            "Ingestion presentation page", path, ct: ct).ConfigureAwait(false);
        NormalizePresentationUrls(result?.Items);
        return result;
    }

    private void NormalizePresentationUrls(IEnumerable<IngestionMediaGroupDto>? items)
    {
        if (items is null)
        {
            return;
        }

        foreach (var item in items)
        {
            NormalizePresentationUrl(item);
        }
    }

    private void NormalizePresentationUrl(IngestionMediaGroupDto? item)
    {
        if (item is not null && !string.IsNullOrWhiteSpace(item.CoverUrl))
        {
            item.CoverUrl = AbsoluteUrl(item.CoverUrl);
        }
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<OperationDto>> GetMediaOperationsAsync(
        string? queueName = null, int limit = 100, CancellationToken ct = default)
    {
        try
        {
            var safeLimit = Math.Clamp(limit <= 0 ? 100 : limit, 1, 500);
            var query = $"operations?limit={safeLimit}";
            if (!string.IsNullOrWhiteSpace(queueName))
            {
                query += $"&queueName={Uri.EscapeDataString(queueName)}";
            }

            var result = await _http.GetFromJsonAsync<List<OperationDto>>(
                query, ct).ConfigureAwait(false);
            return result ?? [];
        }
        catch (OperationCanceledException) { return []; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch durable media operations");
            return [];
        }
    }

    /// <inheritdoc/>
    public async Task<OperationDetailDto?> GetMediaOperationAsync(Guid id, CancellationToken ct = default)
    {
        try
        {
            return await _http.GetFromJsonAsync<OperationDetailDto>(
                $"operations/{id:D}", ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch media operation {Id}", id);
            return null;
        }
    }

    /// <inheritdoc/>
    public async Task<Dictionary<string, int>> GetMediaOperationsSummaryAsync(CancellationToken ct = default)
    {
        try
        {
            return await _http.GetFromJsonAsync<Dictionary<string, int>>(
                "operations/summary", ct).ConfigureAwait(false) ?? new();
        }
        catch (OperationCanceledException) { return new(); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch media operation summary");
            return new();
        }
    }

    /// <inheritdoc/>
    public async Task<bool> RetryMediaOperationAsync(Guid id, CancellationToken ct = default)
    {
        try
        {
            using var response = await _http.PostAsJsonAsync(
                $"operations/{id:D}/retry", new { }, ct).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to retry media operation {Id}", id);
            return false;
        }
    }

    /// <inheritdoc/>
    public async Task<bool> CancelMediaOperationAsync(Guid id, CancellationToken ct = default)
    {
        try
        {
            using var response = await _http.PostAsJsonAsync(
                $"operations/{id:D}/cancel", new { }, ct).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to cancel media operation {Id}", id);
            return false;
        }
    }

    /// <inheritdoc/>
    public async Task<IngestionBatchResponse?> GetIngestionBatchByIdAsync(
        Guid id, CancellationToken ct = default)
    {
        try
        {
            return await _http.GetFromJsonAsync<IngestionBatchResponse>(
                $"ingestion/batches/{id}", ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch batch {Id}", id);
            return null;
        }
    }

    /// <inheritdoc/>
    public async Task<PagedResponse<IngestionBatchItemResponse>?> GetIngestionBatchItemsAsync(
        Guid id, int offset = 0, int limit = 100, CancellationToken ct = default)
    {
        try
        {
            var safeOffset = Math.Max(0, offset);
            var safeLimit = Math.Clamp(limit <= 0 ? 100 : limit, 1, 500);
            return await _http.GetFromJsonAsync<PagedResponse<IngestionBatchItemResponse>>(
                $"ingestion/batches/{id:D}/items?offset={safeOffset}&limit={safeLimit}", ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch batch items for {Id}", id);
            return null;
        }
    }

    /// <inheritdoc/>
    public async Task<int> GetBatchAttentionCountAsync(CancellationToken ct = default)
    {
        try
        {
            var result = await _http.GetFromJsonAsync<BatchAttentionCountResponse>(
                "ingestion/batches/attention-count", ct).ConfigureAwait(false);
            return result?.count ?? 0;
        }
        catch (OperationCanceledException) { return 0; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch batch attention count");
            return 0;
        }
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<CapabilityStateDto>> GetAssetCapabilitiesAsync(
        Guid id, CancellationToken ct = default)
    {
        try
        {
            var result = await _http.GetFromJsonAsync<List<CapabilityStateDto>>(
                $"assets/{id:D}/capabilities", ct).ConfigureAwait(false);
            return result ?? [];
        }
        catch (OperationCanceledException) { return []; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch capability states for asset {Id}", id);
            return [];
        }
    }

    /// <inheritdoc/>
    public async Task<Dictionary<string, int>> GetCapabilitySummaryAsync(CancellationToken ct = default)
    {
        try
        {
            return await _http.GetFromJsonAsync<Dictionary<string, int>>(
                "capabilities/summary", ct).ConfigureAwait(false) ?? new();
        }
        catch (OperationCanceledException) { return new(); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch capability summary");
            return new();
        }
    }

    // -- POST /review/{id}/move-to-tv -----------------------------------------

    public Task<MediaEngine.Contracts.Review.ReviewMoveToTvResponse?> MoveReviewItemToTvAsync(
        Guid id, CancellationToken ct = default)
        => PostAsync<object, MediaEngine.Contracts.Review.ReviewMoveToTvResponse>(
            "POST /review/{id}/move-to-tv", $"/review/{id}/move-to-tv", new { }, ct: ct);
}
