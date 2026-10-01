using System.Text.Json;
using MediaEngine.Contracts.Details;
using MediaEngine.Contracts.Display;
using MediaEngine.Contracts.Maintenance;
using MediaEngine.Contracts.Operations;
using MediaEngine.Contracts.Paging;
using MediaEngine.Contracts.Playback;
using MediaEngine.Contracts.Settings;
using MediaEngine.Domain.Models;
using MediaEngine.Web.Models.ViewDTOs;

namespace MediaEngine.Web.Services.Integration;

public partial interface IEngineApiClient
{
    /// <summary>POST /ingestion/scan — dry-run scan of a directory path.</summary>
    Task<ScanResponse?> TriggerScanAsync(string? rootPath = null, CancellationToken ct = default);

    /// <summary>POST /ingestion/reconcile — scan all assets and clean orphans.</summary>
    Task<ReconciliationResultResponse?> TriggerReconciliationAsync(CancellationToken ct = default);

    Task<FileMetadataRereadResponse?> RereadAssetMetadataAsync(Guid assetId, CancellationToken ct = default);

    // ── Hydration (/metadata/hydrate) ──────────────────────────────────────────

    /// <summary>POST /metadata/hydrate/{entityId} — trigger Wikidata SPARQL deep hydration.</summary>
    Task<HydrateResultViewModel?> TriggerHydrationAsync(
        Guid entityId, CancellationToken ct = default);

    Task<EnrichmentRefreshScheduleResponse?> GetEnrichmentRefreshScheduleAsync(
        string? entityType = null, string? status = null, int limit = 250, CancellationToken ct = default);

    Task<EnrichmentRefreshScheduleDto?> GetEntityEnrichmentRefreshScheduleAsync(
        string entityType, Guid entityId, CancellationToken ct = default);

    Task<EnrichmentRefreshQueuedResponse?> QueueEnrichmentRefreshNowAsync(
        string entityType, Guid entityId, CancellationToken ct = default);

    /// <summary>GET /metadata/pass2/status — pending count and enabled state for the Pass 2 deferred enrichment queue.</summary>
    Task<DeferredEnrichmentStatusResponse?> GetPass2StatusAsync(CancellationToken ct = default);

    /// <summary>POST /metadata/pass2/trigger — trigger immediate Pass 2 (Universe Lookup) processing.</summary>
    Task<DeferredEnrichmentTriggerResponse?> TriggerPass2NowAsync(CancellationToken ct = default);

    // ── Retag Sweep (auto re-tag) ─────────────────────────────────────────────

    /// <summary>GET /maintenance/retag-sweep/state — returns the pending diff + current hashes.</summary>
    Task<RetagSweepStateResponse?> GetRetagSweepStateAsync(CancellationToken ct = default);

    /// <summary>POST /maintenance/retag-sweep/apply — commits the staged pending diff.</summary>
    Task<bool> ApplyRetagSweepPendingAsync(CancellationToken ct = default);

    /// <summary>POST /maintenance/retag-sweep/run-now — wakes the sweep worker immediately.</summary>
    Task<bool> RunRetagSweepNowAsync(CancellationToken ct = default);

    /// <summary>POST /maintenance/retag-sweep/retry/{assetId} — re-queues a single terminal-failed asset.</summary>
    Task<bool> RetryRetagForAssetAsync(Guid assetId, CancellationToken ct = default);

    // ── Initial Sweep (side-by-side-with-Plex plan §M) ───────────────────────

    /// <summary>POST /maintenance/initial-sweep/run — fire-and-forget hash sweep.</summary>
    Task<bool> RunInitialSweepAsync(CancellationToken ct = default);

    // ── QID Label Resolution (/metadata/labels) ────────────────────────────────

    // ── Conflicts (/metadata/conflicts) ──────────────────────────────────────

    /// <summary>GET /metadata/conflicts — canonical values with unresolved metadata conflicts.</summary>
    Task<List<ConflictViewModel>> GetConflictsAsync(CancellationToken ct = default);

    // ── Watch Folder (/ingestion/watch-folder) ─────────────────────────────────

    /// <summary>GET /ingestion/watch-folder — list files currently in the Watch Folder.</summary>
    Task<List<WatchFolderFileDto>> GetWatchFolderAsync(CancellationToken ct = default);

    /// <summary>POST /ingestion/rescan — trigger re-processing of Watch Folder files.</summary>
    Task<bool> TriggerRescanAsync(string? rootPath = null, bool? includeSubdirectories = null, CancellationToken ct = default);

    // ── Development Seed (/dev) ────────────────────────────────────────

    /// <summary>POST /dev/seed-library — create test EPUBs in the Watch Folder (dev only).</summary>
    Task<bool> SeedLibraryAsync(CancellationToken ct = default);

    /// <summary>POST a development ingestion harness endpoint and return its raw response for admin diagnostics.</summary>
    Task<DevHarnessRunResult?> RunDevHarnessAsync(
        string path,
        IReadOnlyDictionary<string, string?>? query = null,
        CancellationToken ct = default);

    // ── Library items (/library/items) ─────────────────────────────────────────

    /// <summary>GET /library/items - paginated list of all ingested items.</summary>
    Task<LibraryCatalogPageResponse?> GetLibraryCatalogItemsAsync(
        int offset = 0, int limit = 50,
        string? search = null, string? type = null, string? status = null,
        double? minConfidence = null, string? matchSource = null,
        bool? duplicatesOnly = null, bool? missingUniverseOnly = null,
        string? sort = null, int? maxDays = null,
        CancellationToken ct = default);

    /// <summary>POST /library/items/batch/approve - bulk-approve library items.</summary>
    Task<BatchLibraryItemResponse?> BatchApproveLibraryCatalogItemsAsync(Guid[] entityIds, CancellationToken ct = default);

    /// <summary>POST /library/items/batch/delete - bulk-delete library items.</summary>
    Task<BatchLibraryItemResponse?> BatchDeleteLibraryCatalogItemsAsync(Guid[] entityIds, CancellationToken ct = default);

    /// <summary>POST /library/items/{entityId}/reject - reject a single library item.</summary>
    Task<BatchLibraryItemResponse?> RejectLibraryCatalogItemAsync(Guid entityId, CancellationToken ct = default);

    /// <summary>POST /library/items/batch/reject - bulk-reject library items.</summary>
    Task<BatchLibraryItemResponse?> BatchRejectLibraryCatalogItemsAsync(Guid[] entityIds, CancellationToken ct = default);

    /// <summary>GET /library/items/{entityId}/detail - full detail for expanded row.</summary>
    Task<LibraryItemDetailViewModel?> GetLibraryItemDetailAsync(Guid entityId, CancellationToken ct = default);

    /// <summary>GET /library/items/{entityId}/history - processing history timeline.</summary>
    Task<List<LibraryItemHistoryDto>> GetItemHistoryAsync(Guid entityId, CancellationToken ct = default);
    Task<(List<LibraryItemHistoryDto> Items, string? Error)> GetItemHistoryWithStatusAsync(Guid entityId, CancellationToken ct = default);

    /// <summary>POST /library/items/{entityId}/provisional - mark an item as provisional with curator metadata.</summary>
    Task<bool> MarkProvisionalAsync(Guid entityId, ProvisionalMetadataRequestDto metadata, CancellationToken ct = default);

    /// <summary>GET /library/items/counts - status counts for tab badges.</summary>
    Task<LibraryItemStatusCountsDto?> GetLibraryItemStatusCountsAsync(CancellationToken ct = default);

    /// <summary>GET /library/items/state-counts - four-state counts with trigger breakdown.</summary>
    Task<LibraryItemLifecycleCountsDto?> GetLibraryItemLifecycleCountsAsync(
        Guid? batchId = null, CancellationToken ct = default);

    /// <summary>GET /ingestion/batches — recent ingestion batches.</summary>
    Task<IReadOnlyList<IngestionBatchResponse>> GetIngestionBatchesAsync(
        int limit = 20, CancellationToken ct = default);

    /// <summary>GET /ingestion/operations — Ingestion dashboard snapshot.</summary>
    Task<List<IngestionOperationsBatchDto>?> GetIngestionNotificationStatusAsync(CancellationToken ct = default);

    Task<IngestionOperationsSnapshotDto?> GetIngestionOperationsSnapshotAsync(CancellationToken ct = default);

    Task<IngestionPresentationSnapshotDto?> GetIngestionPresentationAsync(CancellationToken ct = default);

    Task<PagedResponse<IngestionMediaGroupDto>?> GetCurrentIngestionMediaAsync(
        int offset = 0, int limit = 50, CancellationToken ct = default);

    Task<PagedResponse<IngestionMediaGroupDto>?> GetRecentIngestionAdditionsAsync(
        string? search = null, string? lane = null, DateTimeOffset? start = null, DateTimeOffset? end = null,
        int offset = 0, int limit = 50, string? sort = null, CancellationToken ct = default);

    Task<IngestionMediaGroupDto?> GetIngestionMediaGroupAsync(
        Guid batchId, Guid groupId, CancellationToken ct = default);

    Task<PagedResponse<IngestionMediaChildDto>?> GetIngestionMediaChildrenAsync(
        Guid batchId, Guid groupId, int offset = 0, int limit = 50, CancellationToken ct = default);

    Task<ActivityHistorySummaryDto?> GetActivityHistorySummaryAsync(CancellationToken ct = default);

    Task<ActivityBatchPresentationDto?> GetActivityBatchPresentationAsync(
        Guid batchId, CancellationToken ct = default);

    Task<PagedResponse<IngestionMediaGroupDto>?> GetActivityBatchMediaGroupsAsync(
        Guid batchId,
        int offset = 0,
        int limit = 50,
        string? search = null,
        string? lane = null,
        string? sort = null,
        CancellationToken ct = default);

    /// <summary>GET /operations — durable media operations by queue order.</summary>
    Task<IReadOnlyList<OperationDto>> GetMediaOperationsAsync(
        string? queueName = null, int limit = 100, CancellationToken ct = default);

    /// <summary>GET /operations/{id} — one durable operation and its timeline.</summary>
    Task<OperationDetailDto?> GetMediaOperationAsync(Guid id, CancellationToken ct = default);

    /// <summary>GET /operations/summary — durable operation counts by status.</summary>
    Task<Dictionary<string, int>> GetMediaOperationsSummaryAsync(CancellationToken ct = default);

    /// <summary>POST /operations/{id}/retry — requeue a durable operation.</summary>
    Task<bool> RetryMediaOperationAsync(Guid id, CancellationToken ct = default);

    /// <summary>POST /operations/{id}/cancel — cancel a durable operation.</summary>
    Task<bool> CancelMediaOperationAsync(Guid id, CancellationToken ct = default);

    /// <summary>GET /ingestion/batches/{id} — single batch detail.</summary>
    Task<IngestionBatchResponse?> GetIngestionBatchByIdAsync(
        Guid id, CancellationToken ct = default);

    /// <summary>GET /ingestion/batches/{id}/items — item-level batch progress.</summary>
    Task<PagedResponse<IngestionBatchItemResponse>?> GetIngestionBatchItemsAsync(
        Guid id, int offset = 0, int limit = 100, CancellationToken ct = default);

    /// <summary>GET /ingestion/batches/attention-count — items needing attention.</summary>
    Task<int> GetBatchAttentionCountAsync(CancellationToken ct = default);

    /// <summary>GET /assets/{id}/capabilities — explicit capability readiness for an asset.</summary>
    Task<IReadOnlyList<CapabilityStateDto>> GetAssetCapabilitiesAsync(
        Guid id, CancellationToken ct = default);

    /// <summary>GET /capabilities/summary — capability counts by capability/status.</summary>
    Task<Dictionary<string, int>> GetCapabilitySummaryAsync(CancellationToken ct = default);

}
