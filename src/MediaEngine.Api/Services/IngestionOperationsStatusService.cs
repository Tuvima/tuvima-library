using System.Globalization;
using System.Text.Json;
using Dapper;
using MediaEngine.Api.Services.ReadServices;
using MediaEngine.Contracts.Ingestion;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Models;
using MediaEngine.Domain.Services;
using MediaEngine.Ingestion.Models;
using MediaEngine.Providers.Services;
using MediaEngine.Storage;
using MediaEngine.Storage.Contracts;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using ProviderConfig = MediaEngine.Domain.Configuration.ProviderConfiguration;

namespace MediaEngine.Api.Services;

public sealed class IngestionOperationsStatusService : IIngestionOperationsStatusService
{
    private static readonly string[] ActiveBatchStatuses = ["running", "queued", "processing", "active"];
    private static readonly string[] FailedBatchStatuses = ["failed"];
    private static readonly string[] InterruptedBatchStatuses = ["abandoned", "interrupted"];
    private static readonly TimeSpan NoWorkBatchGrace = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan NoWorkBatchMaximumAge = TimeSpan.FromMinutes(2);
    private static readonly string[] RetailMatchedStates =
    [
        nameof(IdentityJobState.RetailMatched),
        nameof(IdentityJobState.BridgeSearching),
        nameof(IdentityJobState.QidResolved),
        nameof(IdentityJobState.QidNeedsReview),
        nameof(IdentityJobState.QidNoMatch),
        nameof(IdentityJobState.Hydrating),
        nameof(IdentityJobState.UniverseEnriching),
        nameof(IdentityJobState.Ready),
        nameof(IdentityJobState.ReadyWithoutUniverse),
    ];
    private static readonly string[] RetailReviewStates = [nameof(IdentityJobState.RetailMatchedNeedsReview)];
    private static readonly string[] RetailNoMatchStates = [nameof(IdentityJobState.RetailNoMatch)];
    private static readonly string[] WikidataResolvedStates =
    [
        nameof(IdentityJobState.QidResolved),
        nameof(IdentityJobState.Hydrating),
        nameof(IdentityJobState.UniverseEnriching),
        nameof(IdentityJobState.Ready),
        nameof(IdentityJobState.ReadyWithoutUniverse),
    ];
    private static readonly string[] WikidataReviewStates =
    [
        nameof(IdentityJobState.QidNeedsReview),
        nameof(IdentityJobState.QidNoMatch),
    ];
    private static readonly string[] EnrichmentCompleteStates =
    [
        nameof(IdentityJobState.Ready),
        nameof(IdentityJobState.ReadyWithoutUniverse),
    ];
    private static readonly string[] RelationshipActivityTypes =
    [
        SystemActionType.CollectionAssigned,
        SystemActionType.CollectionCreated,
        SystemActionType.CollectionMerged,
        SystemActionType.NarrativeRootResolved,
        SystemActionType.RelationshipDiscovered,
        SystemActionType.UniverseXmlUpdated,
    ];
    private static readonly string[] PeopleActivityTypes =
    [
        SystemActionType.PersonHydrated,
        SystemActionType.PersonMerged,
        SystemActionType.CharacterEnriched,
        SystemActionType.LocationEnriched,
        SystemActionType.OrganizationEnriched,
    ];
    private static readonly string[] ActiveOperationStatuses =
    [
        MediaOperationStatus.Leased,
        MediaOperationStatus.Running,
    ];
    private static readonly string[] QueuedOperationStatuses =
    [
        MediaOperationStatus.Pending,
        MediaOperationStatus.Queued,
        MediaOperationStatus.RetryWaiting,
        MediaOperationStatus.Interrupted,
    ];
    private static readonly string[] TerminalOperationStatuses =
    [
        MediaOperationStatus.Succeeded,
        MediaOperationStatus.NoResult,
        MediaOperationStatus.MissingConfirmed,
        MediaOperationStatus.NotApplicable,
        MediaOperationStatus.Blocked,
        MediaOperationStatus.FailedTerminal,
        MediaOperationStatus.DeadLettered,
        MediaOperationStatus.Cancelled,
        MediaOperationStatus.Skipped,
    ];
    private static readonly string[] VisibleOperationTypes =
    [
        MediaOperationType.IdentityWikidataBridge,
        MediaOperationType.EnrichmentCoverArt,
        MediaOperationType.EnrichmentPeople,
        MediaOperationType.EnrichmentRelationships,
    ];
    // Wikidata and artwork batches can legitimately spend many minutes in one
    // leased state before per-item finalisation updates the row. Keep those rows
    // visible as active so the Library Update page does not look frozen.
    private static readonly TimeSpan ActiveActivityFreshness = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ActiveBatchFreshness = TimeSpan.FromMinutes(2);
    private static readonly string[] CurrentActivityStates =
    [
        nameof(IdentityJobState.Queued),
        nameof(IdentityJobState.RetailSearching),
        nameof(IdentityJobState.RetailMatched),
        nameof(IdentityJobState.BridgeSearching),
        nameof(IdentityJobState.QidResolved),
        nameof(IdentityJobState.Hydrating),
        nameof(IdentityJobState.UniverseEnriching),
    ];
    private const int ActivityBatchSize = 50;

    private readonly IDatabaseConnection _db;
    private readonly IConfigurationLoader _configLoader;
    private readonly IProviderHealthRepository _providerHealth;
    private readonly IIngestionBatchRepository _batchRepository;
    private readonly ILibraryItemRepository _libraryItems;
    private readonly IOptions<IngestionOptions> _ingestionOptions;
    private readonly IReviewQueueReadService _reviewQueueReadService;
    private readonly IProviderRateLimiterCoordinator? _providerRateLimiter;

    public IngestionOperationsStatusService(
        IDatabaseConnection db,
        IConfigurationLoader configLoader,
        IProviderHealthRepository providerHealth,
        IIngestionBatchRepository batchRepository,
        ILibraryItemRepository libraryItems,
        IReviewQueueReadService reviewQueueReadService,
        IOptions<IngestionOptions> ingestionOptions,
        IProviderRateLimiterCoordinator? providerRateLimiter = null)
    {
        _db = db;
        _configLoader = configLoader;
        _providerHealth = providerHealth;
        _batchRepository = batchRepository;
        _libraryItems = libraryItems;
        _reviewQueueReadService = reviewQueueReadService;
        _ingestionOptions = ingestionOptions;
        _providerRateLimiter = providerRateLimiter;
    }

    public async Task<IngestionOperationsSnapshotDto> GetSnapshotAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var recentBatches = await _batchRepository.GetRecentAsync(12, ct);
        var activeBatches = await _batchRepository.GetActiveAsync(ct);
        recentBatches = activeBatches
            .Concat(recentBatches)
            .GroupBy(batch => batch.Id)
            .Select(group => group.First())
            .OrderByDescending(batch => batch.StartedAt)
            .ToList();
        recentBatches = await ReconcileCompletedBatchesAsync(recentBatches, ct);
        recentBatches = await ProjectRecentBatchesForDisplayAsync(recentBatches, ct);
        var displayBatches = SelectDisplayBatches(recentBatches);
        var displayBatch = AggregateDisplayBatch(displayBatches);
        var lifecycle = await _libraryItems.GetFourStateCountsAsync(ct: ct);
        var projection = await _libraryItems.GetProjectionSummaryAsync(ct);
        var providers = _configLoader.LoadAllProviders();
        var healthRecords = await _providerHealth.GetAllAsync(ct);
        var healthById = healthRecords.ToDictionary(h => h.ProviderId, StringComparer.OrdinalIgnoreCase);

        using var conn = _db.CreateConnection();
        var pipelineRows = (await conn.QueryAsync<StageCountRow>("""
            SELECT state AS Key, COUNT(*) AS Count
            FROM identity_jobs
            GROUP BY state
            """)).ToDictionary(r => r.Key, r => ToInt(r.Count), StringComparer.OrdinalIgnoreCase);

        var ingestionRows = (await conn.QueryAsync<StageCountRow>("""
            SELECT status AS Key, COUNT(*) AS Count
            FROM media_operations
            WHERE operation_type = 'ingestion.file'
            GROUP BY status
            """)).ToDictionary(r => r.Key, r => ToInt(r.Count), StringComparer.OrdinalIgnoreCase);
        var scopedPipelineRows = displayBatches.Count == 0
            ? pipelineRows
            : await ReadIdentityStateCountsAsync(displayBatches.Select(batch => batch.Id).ToArray(), ct);
        var scopedIngestionRows = displayBatches.Count == 0
            ? ingestionRows
            : await ReadIngestionStatusCountsAsync(displayBatches.Select(batch => batch.Id).ToArray(), ct);

        var reviewRows = (await _reviewQueueReadService.GetPendingReasonCountsAsync(ct))
            .Select(row => new ReviewCountRow
            {
                Trigger = row.Trigger ?? string.Empty,
                Detail = row.Detail,
                Count = row.Count,
            })
            .ToList();
        var pendingReviewCount = await _reviewQueueReadService.GetPendingCountAsync(ct);

        var folderStats = (await conn.QueryAsync<FolderStatsRow>("""
            SELECT
                COALESCE(source_path, '') AS SourcePath,
                MAX(started_at) AS LastScan,
                COALESCE(SUM(files_registered), 0) AS ItemCount,
                COALESCE(SUM(files_review + files_no_match + files_failed), 0) AS UnresolvedCount
            FROM ingestion_batches
            WHERE source_path IS NOT NULL AND source_path <> ''
            GROUP BY source_path
            """)).ToDictionary(r => r.SourcePath, StringComparer.OrdinalIgnoreCase);

        // Folder inventory comes from owned assets, independently of batch history.
        foreach (var library in _configLoader.LoadLibraries().Libraries)
        {
            foreach (var source in library.Sources)
            {
                if (string.IsNullOrWhiteSpace(source.Path))
                {
                    continue;
                }

                var root = source.Path.Replace('\\', '/').TrimEnd('/') + "/";
                var row = folderStats.GetValueOrDefault(source.Path) ?? new FolderStatsRow { SourcePath = source.Path };
                row.ItemCount = conn.ExecuteScalar<long>("""
                    SELECT COUNT(*) FROM media_assets
                    WHERE is_orphaned = 0 AND library_id = @LibraryId
                      AND substr(replace(file_path_root, char(92), '/'), 1, length(@Root)) = @Root COLLATE NOCASE
                    """, new { LibraryId = library.Id, Root = root });
                folderStats[source.Path] = row;
            }
        }

        var activeJobs = new List<IngestionOperationsJobDto>();
        foreach (var batch in recentBatches
            .Where(batch => ActiveBatchStatuses.Contains(batch.Status, StringComparer.OrdinalIgnoreCase))
            .Where(ShouldShowActiveBatch))
        {
            var batchPipelineRows = displayBatch?.Id == batch.Id
                ? scopedPipelineRows
                : await ReadIdentityStateCountsAsync(batch.Id, ct);
            var batchIngestionRows = displayBatch?.Id == batch.Id
                ? scopedIngestionRows
                : await ReadIngestionStatusCountsAsync(batch.Id, ct);
            var batchStages = BuildPipelineStages(batchPipelineRows, batchIngestionRows, lifecycle, projection, batch);
            activeJobs.Add(ToActiveJob(batch, batchPipelineRows, batchStages));
        }

        var providerDtos = providers
            .Where(IsIngestionProvider)
            .OrderBy(p => ProviderSortKey(p.Name))
            .ThenBy(p => p.Name)
            .Select(p => ToProviderDto(p, healthById.GetValueOrDefault(p.Name)))
            .ToList();
        var providerActivity = _providerRateLimiter?.GetSnapshots()
            .Select(ToProviderActivityDto)
            .ToList() ?? [];

        var providerWarnings = providerDtos.Count(p =>
            p.Status is "Degraded" or "Offline" or "Missing Configuration");

        var failedJobs = recentBatches.Count(batch =>
            FailedBatchStatuses.Contains(batch.Status, StringComparer.OrdinalIgnoreCase))
            + Count(pipelineRows, nameof(IdentityJobState.Failed));
        var summaryTotals = BuildSummaryTotals(displayBatch, lifecycle, projection, pendingReviewCount);
        var expectedOutcomes = LoadManifestExpectedOutcomes(summaryTotals.Total);
        var pipelineStages = BuildPipelineStages(scopedPipelineRows, scopedIngestionRows, lifecycle, projection, displayBatch, pendingReviewCount);
        var batchStats = await ReadBatchStatsAsync(recentBatches, ct);
        var recentBatchGroups = BuildRecentBatchGroups(recentBatches);
        var currentActivities = await ReadTaskActivitiesAsync(displayBatches.Select(batch => batch.Id).ToArray(), pipelineStages, ct);
        if (currentActivities.Count == 0)
        {
            currentActivities = activeJobs
                .Take(3)
                .Select(job => ToCurrentActivity(job, pipelineStages))
                .Where(activity => !string.IsNullOrWhiteSpace(activity.Message))
                .ToList();
        }

        if (activeJobs.Count == 0
            && displayBatch is not null
            && ActiveBatchStatuses.Contains(displayBatch.Status, StringComparer.OrdinalIgnoreCase)
            && currentActivities.Any(IsActiveActivity))
        {
            activeJobs.Add(ToActiveJob(displayBatch, scopedPipelineRows, pipelineStages));
        }

        var stageProgress = await BuildNumberedStageProgressAsync(
            displayBatches,
            pipelineStages,
            currentActivities,
            pendingReviewCount,
            summaryTotals.Total,
            ct).ConfigureAwait(false);

        var recentBatchDtos = await BuildRecentBatchDtosAsync(
            recentBatchGroups,
            recentBatches,
            batchStats,
            lifecycle,
            projection,
            ct).ConfigureAwait(false);
        var recentIdentityJobs = await ReadRecentIdentityJobsAsync(ct).ConfigureAwait(false);
        using var retryConnection = _db.CreateConnection();
        var retryWaits = (await retryConnection.QueryAsync<IngestionRetryWaitDto>(new CommandDefinition("""
            SELECT media_type AS MediaType, COUNT(*) AS Count, MAX(attempt_count) AS Attempts,
                   COALESCE(last_error, 'Waiting for dependency') AS Reason, MIN(next_retry_at) AS NextAttemptAt
            FROM identity_jobs
            WHERE next_retry_at IS NOT NULL AND state = 'Queued'
            GROUP BY media_type, last_error;
            """, cancellationToken: ct))).ToList();

        var activeWorkCount = activeJobs.Count > 0
            ? activeJobs.Count
            : currentActivities.Any(IsActiveActivity) ? 1 : 0;

        var summary = new IngestionOperationsSummaryDto
        {
            TotalItems = summaryTotals.Total,
            RegisteredItems = summaryTotals.Registered,
            ProvisionalItems = lifecycle.Provisional,
            ItemsNeedingReview = summaryTotals.Review,
            ActiveJobs = activeWorkCount,
            FailedJobs = failedJobs,
            ProviderWarnings = providerWarnings,
            LastSuccessfulScanTime = recentBatches
                .Where(batch => batch.CompletedAt.HasValue && !batch.Status.Contains("fail", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(batch => batch.CompletedAt)
                .Select(batch => batch.CompletedAt)
                .FirstOrDefault(),
            EngineStatus = providerWarnings > 0 || failedJobs > 0 ? "Degraded" : "Online",
            HealthLabel = ResolveHealthLabel(summaryTotals.Review, providerWarnings, failedJobs, activeWorkCount),
            ExpectedOutcomes = expectedOutcomes,
        };

        return new IngestionOperationsSnapshotDto
        {
            RetryWaits = retryWaits,
            Summary = summary,
            ActiveJobs = activeJobs,
            RecentIdentityJobs = recentIdentityJobs,
            CurrentActivities = currentActivities,
            PipelineStages = pipelineStages,
            StageProgress = stageProgress,
            ReviewReasons = BuildReviewReasons(reviewRows),
            SourceGroups = BuildSourceGroups(folderStats),
            ProviderHealth = providerDtos,
            ProviderActivity = providerActivity,
            RecentBatches = recentBatchDtos,
            Organization = BuildOrganizationRules(),
            GeneratedAt = DateTimeOffset.UtcNow,
        };
    }

    private async Task<List<IngestionOperationsJobDto>> ReadRecentIdentityJobsAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = _db.CreateConnection();
        var cutoff = DateTimeOffset.UtcNow.AddDays(-7).ToString("O", CultureInfo.InvariantCulture);
        var rows = (await conn.QueryAsync<RecentIdentityJobRow>("""
            WITH latest_jobs AS (
                SELECT
                    id,
                    entity_id,
                    state,
                    media_type,
                    last_error,
                    created_at,
                    updated_at,
                    ROW_NUMBER() OVER (
                        PARTITION BY entity_id
                        ORDER BY updated_at DESC, created_at DESC
                    ) AS rn
                FROM identity_jobs
                WHERE ingestion_run_id IS NULL
                  AND updated_at >= @cutoff
            )
            SELECT
                lj.id AS JobId,
                lj.state AS State,
                lj.media_type AS MediaType,
                lj.last_error AS LastError,
                lj.created_at AS CreatedAt,
                lj.updated_at AS UpdatedAt,
                ma.file_path_root AS SourcePath,
                COALESCE(
                    CASE
                        WHEN LOWER(COALESCE(lj.media_type, '')) = 'music' AND parent_work.id IS NOT NULL THEN
                            (
                                SELECT cv.value
                                FROM canonical_values cv
                                WHERE cv.entity_id = parent_work.id
                                  AND cv.key IN ('album', 'title')
                                  AND cv.value IS NOT NULL
                                  AND cv.value <> ''
                                ORDER BY CASE cv.key WHEN 'album' THEN 0 ELSE 1 END
                                LIMIT 1
                            )
                        ELSE NULL
                    END,
                    (
                        SELECT cv.value
                        FROM canonical_values cv
                        WHERE cv.entity_id = lj.entity_id
                          AND cv.key IN ('title', 'episode_title')
                          AND cv.value IS NOT NULL
                          AND cv.value <> ''
                        ORDER BY CASE cv.key WHEN 'title' THEN 0 ELSE 1 END
                        LIMIT 1
                    ),
                    (
                        SELECT cv.value
                        FROM canonical_values cv
                        WHERE cv.entity_id = w.id
                          AND cv.key = 'title'
                          AND cv.value IS NOT NULL
                          AND cv.value <> ''
                        LIMIT 1
                    ),
                    ''
                ) AS Title
            FROM latest_jobs lj
            LEFT JOIN media_assets ma ON ma.id = lj.entity_id
            LEFT JOIN editions e ON e.id = ma.edition_id
            LEFT JOIN works w ON w.id = e.work_id
            LEFT JOIN works parent_work ON parent_work.id = w.parent_work_id
            WHERE lj.rn = 1
            ORDER BY lj.updated_at DESC
            LIMIT 12;
            """, new { cutoff })).AsList();

        return rows.Select(ToRecentIdentityJob).ToList();
    }

    private static IngestionOperationsJobDto ToRecentIdentityJob(RecentIdentityJobRow row)
    {
        var state = row.State ?? string.Empty;
        var terminalSuccess = state.Equals(nameof(IdentityJobState.Ready), StringComparison.OrdinalIgnoreCase)
            || state.Equals(nameof(IdentityJobState.ReadyWithoutUniverse), StringComparison.OrdinalIgnoreCase);
        var needsAttention = state.Equals(nameof(IdentityJobState.Failed), StringComparison.OrdinalIgnoreCase)
            || state.Equals(nameof(IdentityJobState.RetailNoMatch), StringComparison.OrdinalIgnoreCase)
            || state.Equals(nameof(IdentityJobState.RetailMatchedNeedsReview), StringComparison.OrdinalIgnoreCase)
            || state.Equals(nameof(IdentityJobState.QidNeedsReview), StringComparison.OrdinalIgnoreCase)
            || state.Equals(nameof(IdentityJobState.QidNoMatch), StringComparison.OrdinalIgnoreCase);
        var updatedAt = ParseDate(row.UpdatedAt);
        var createdAt = ParseDate(row.CreatedAt);

        return new IngestionOperationsJobDto
        {
            JobId = row.JobId,
            JobType = "Item enrichment",
            MediaType = row.MediaType,
            SourceFolder = row.SourcePath,
            CurrentStage = ResolveRecentIdentityStage(state),
            CurrentItem = StringHelpers.FirstNonBlankOr("Matched item", row.Title, ShortPath(row.SourcePath)),
            ProcessedCount = terminalSuccess || needsAttention ? 1 : 0,
            TotalCount = 1,
            CountUnit = "item",
            PercentComplete = terminalSuccess || needsAttention ? 100 : ResolveRecentIdentityPercent(state),
            Status = terminalSuccess ? "completed" : needsAttention ? "attention" : "running",
            Elapsed = createdAt.HasValue && updatedAt.HasValue ? updatedAt.Value - createdAt.Value : null,
            LastUpdatedTime = updatedAt,
            WarningSummary = needsAttention
                ? StringHelpers.FirstNonBlankOr("This item needs attention before enrichment can finish.", row.LastError)
                : null,
        };
    }

    private static string ResolveRecentIdentityStage(string state) => state switch
    {
        nameof(IdentityJobState.Queued) => "Queued for full enrichment",
        nameof(IdentityJobState.RetailSearching) => "Confirming retail metadata",
        nameof(IdentityJobState.RetailMatched) => "Retail match confirmed",
        nameof(IdentityJobState.RetailMatchedNeedsReview) => "Retail match needs review",
        nameof(IdentityJobState.RetailNoMatch) => "No retail match found",
        nameof(IdentityJobState.BridgeSearching) => "Checking Wikidata identity",
        nameof(IdentityJobState.QidResolved) => "Wikidata identity confirmed",
        nameof(IdentityJobState.QidNeedsReview) => "Wikidata identity needs review",
        nameof(IdentityJobState.QidNoMatch) => "No Wikidata identity found",
        nameof(IdentityJobState.Hydrating) => "Adding metadata and people",
        nameof(IdentityJobState.UniverseEnriching) => "Adding artwork and relationships",
        nameof(IdentityJobState.Ready) or nameof(IdentityJobState.ReadyWithoutUniverse) => "Full enrichment complete",
        nameof(IdentityJobState.Failed) => "Enrichment failed",
        _ => "Processing item",
    };

    private static double ResolveRecentIdentityPercent(string state) => state switch
    {
        nameof(IdentityJobState.Queued) => 5,
        nameof(IdentityJobState.RetailSearching) => 15,
        nameof(IdentityJobState.RetailMatched) or nameof(IdentityJobState.RetailMatchedNeedsReview) => 30,
        nameof(IdentityJobState.BridgeSearching) => 45,
        nameof(IdentityJobState.QidResolved) or nameof(IdentityJobState.QidNeedsReview) or nameof(IdentityJobState.QidNoMatch) => 55,
        nameof(IdentityJobState.Hydrating) => 75,
        nameof(IdentityJobState.UniverseEnriching) => 90,
        _ => 0,
    };

    private IngestionExpectedOutcomesDto? LoadManifestExpectedOutcomes(int activeFileTotal)
    {
        var manifestPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var sourcePath in _configLoader.LoadLibraries().Libraries
            .SelectMany(library => library.ScannableSources.Select(source => source.Path)))
        {
            AddManifestCandidate(manifestPaths, sourcePath);
            var parent = Directory.GetParent(sourcePath);
            if (parent is not null)
            {
                AddManifestCandidate(manifestPaths, parent.FullName);
            }
        }

        if (manifestPaths.Count == 0)
        {
            return null;
        }

        var counts = new ExpectedOutcomeCounts();
        foreach (var manifestPath in manifestPaths)
        {
            if (!File.Exists(manifestPath))
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
                if (document.RootElement.TryGetProperty("files", out var files)
                    && files.ValueKind == JsonValueKind.Array)
                {
                    foreach (var file in files.EnumerateArray())
                    {
                        if (file.TryGetProperty("expected_identity", out var expectedIdentity))
                        {
                            AccumulateExpectedOutcome(expectedIdentity, counts);
                        }
                    }
                }

                if (document.RootElement.TryGetProperty("expected_identity", out var expectedItems)
                    && expectedItems.ValueKind == JsonValueKind.Array)
                {
                    foreach (var expectedIdentity in expectedItems.EnumerateArray())
                    {
                        AccumulateExpectedOutcome(expectedIdentity, counts);
                    }
                }
            }
            catch (JsonException)
            {
                // Harness manifests are optional status hints for the dashboard; malformed
                // dev manifests should not break the production ingestion status endpoint.
                continue;
            }
            catch (IOException)
            {
                // A manifest can be regenerated while the dashboard polls. The next snapshot
                // will pick it up once the file is readable.
                continue;
            }
        }

        if (activeFileTotal > 0 && counts.ExpectedResolved > activeFileTotal)
        {
            return null;
        }

        return counts.TotalFiles == 0
            ? null
            : new IngestionExpectedOutcomesDto
            {
                TotalFiles = counts.TotalFiles,
                ExpectedResolved = counts.ExpectedResolved,
                ExpectedExactQid = counts.ExpectedExactQid,
                ExpectedAnyQid = counts.ExpectedAnyQid,
                ExpectedReview = counts.ExpectedReview,
                ExpectedKnownNoQid = counts.ExpectedKnownNoQid,
                ExpectedDuplicate = counts.ExpectedDuplicate,
                ExpectedSkipped = counts.ExpectedSkipped,
                ExpectedCorrupt = counts.ExpectedCorrupt,
            };
    }

    private sealed class ExpectedOutcomeCounts
    {
        public int TotalFiles { get; set; }
        public int ExpectedResolved { get; set; }
        public int ExpectedExactQid { get; set; }
        public int ExpectedAnyQid { get; set; }
        public int ExpectedReview { get; set; }
        public int ExpectedKnownNoQid { get; set; }
        public int ExpectedDuplicate { get; set; }
        public int ExpectedSkipped { get; set; }
        public int ExpectedCorrupt { get; set; }
    }

    private static void AddManifestCandidate(HashSet<string> manifestPaths, string directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }

        manifestPaths.Add(Path.Combine(directory, "MANIFEST.json"));
    }

    private static void AccumulateExpectedOutcome(JsonElement expectedIdentity, ExpectedOutcomeCounts counts)
    {
        if (expectedIdentity.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var status = ReadJsonString(expectedIdentity, "expected_status");
        if (string.IsNullOrWhiteSpace(status))
        {
            return;
        }

        counts.TotalFiles++;
        switch (status.Trim())
        {
            case "ExactQid":
                counts.ExpectedResolved++;
                counts.ExpectedExactQid++;
                break;
            case "ResolvedQid":
                counts.ExpectedResolved++;
                counts.ExpectedAnyQid++;
                break;
            case "NeedsReview":
                counts.ExpectedReview++;
                break;
            case "KnownNoWikidataEntity":
                counts.ExpectedKnownNoQid++;
                break;
            case "Duplicate":
                counts.ExpectedDuplicate++;
                break;
            case "Skipped":
                counts.ExpectedSkipped++;
                break;
            case "Corrupt":
                counts.ExpectedCorrupt++;
                break;
        }
    }

    private static string? ReadJsonString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private async Task<IReadOnlyDictionary<string, int>> ReadIdentityStateCountsAsync(Guid batchId, CancellationToken ct)
        => await ReadIdentityStateCountsAsync([batchId], ct);

    private async Task<IReadOnlyDictionary<string, int>> ReadIdentityStateCountsAsync(
        IReadOnlyCollection<Guid> batchIds,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (batchIds.Count == 0)
        {
            return new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        }

        using var conn = _db.CreateConnection();
        var rows = await conn.QueryAsync<StageCountRow>("""
            WITH latest_jobs AS (
                SELECT
                    entity_id,
                    state,
                    ROW_NUMBER() OVER (
                        PARTITION BY entity_id
                        ORDER BY updated_at DESC, created_at DESC
                    ) AS rn
                FROM identity_jobs
                WHERE ingestion_run_id IN @batchIds
            )
            SELECT state AS Key, COUNT(*) AS Count
            FROM latest_jobs
            WHERE rn = 1
            GROUP BY state
            """, new { batchIds = ToBatchIdBlobs(batchIds) });

        return rows.ToDictionary(r => r.Key, r => ToInt(r.Count), StringComparer.OrdinalIgnoreCase);
    }

    private async Task<IReadOnlyDictionary<string, int>> ReadIngestionStatusCountsAsync(Guid batchId, CancellationToken ct)
        => await ReadIngestionStatusCountsAsync([batchId], ct);

    private async Task<IReadOnlyDictionary<string, int>> ReadIngestionStatusCountsAsync(
        IReadOnlyCollection<Guid> batchIds,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (batchIds.Count == 0)
        {
            return new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        }

        using var conn = _db.CreateConnection();
        var rows = await conn.QueryAsync<StageCountRow>("""
            SELECT status AS Key, COUNT(*) AS Count
            FROM media_operations
            WHERE batch_id IN @batchIds
              AND operation_type = 'ingestion.file'
            GROUP BY status
            """, new { batchIds = batchIds.ToArray() });

        return rows.ToDictionary(r => r.Key, r => ToInt(r.Count), StringComparer.OrdinalIgnoreCase);
    }

    private static byte[][] ToBatchIdBlobs(IReadOnlyCollection<Guid> batchIds) =>
        batchIds.Count > 0
            ? batchIds.Select(GuidSql.ToBlob).ToArray()
            : [GuidSql.ToBlob(Guid.Empty)];

    private static IReadOnlyList<IngestionBatch> SelectDisplayBatches(IReadOnlyList<IngestionBatch> recentBatches)
    {
        if (recentBatches.Count == 0)
        {
            return [];
        }

        var groups = BuildRecentBatchGroups(recentBatches);
        if (groups.Count == 0)
        {
            return [];
        }

        var selected = SelectDisplayBatchGroup(groups);
        var selectedIds = selected.SourceBatchIds.ToHashSet();
        return recentBatches
            .Where(batch => selectedIds.Contains(batch.Id))
            .OrderBy(batch => batch.StartedAt)
            .ToList();
    }

    private static DisplayBatchGroup SelectDisplayBatchGroup(IReadOnlyList<DisplayBatchGroup> groups)
    {
        var activeWithOutcomes = groups
            .Where(group => ActiveBatchStatuses.Contains(group.Batch.Status, StringComparer.OrdinalIgnoreCase))
            .Where(group => ShouldShowActiveBatch(group.Batch))
            .Where(group => HasOutcomeCounters(group.Batch))
            .OrderByDescending(group => Math.Max(group.Batch.FilesTotal, group.Batch.FilesProcessed))
            .ThenByDescending(group => group.Batch.StartedAt)
            .FirstOrDefault();
        if (activeWithOutcomes is not null)
        {
            return activeWithOutcomes;
        }

        var completedWithOutcomes = groups
            .Where(group => HasOutcomeCounters(group.Batch))
            .OrderByDescending(group => Math.Max(group.Batch.FilesTotal, group.Batch.FilesProcessed))
            .ThenByDescending(group => group.Batch.StartedAt)
            .FirstOrDefault();
        if (completedWithOutcomes is not null)
        {
            return completedWithOutcomes;
        }

        var activeWithoutOutcomes = groups
            .Where(group => ActiveBatchStatuses.Contains(group.Batch.Status, StringComparer.OrdinalIgnoreCase))
            .Where(group => ShouldShowActiveBatch(group.Batch))
            .OrderByDescending(group => HasOutcomeCounters(group.Batch))
            .ThenByDescending(group => Math.Max(group.Batch.FilesTotal, group.Batch.FilesProcessed))
            .ThenByDescending(group => group.Batch.StartedAt)
            .FirstOrDefault();
        if (activeWithoutOutcomes is not null)
        {
            return activeWithoutOutcomes;
        }

        return groups
            .Where(group => group.Batch.FilesProcessed > 0)
            .OrderByDescending(group => group.Batch.StartedAt)
            .FirstOrDefault()
            ?? groups
                .OrderByDescending(group => group.Batch.StartedAt)
                .First();
    }

    private static IngestionBatch? AggregateDisplayBatch(IReadOnlyList<IngestionBatch> displayBatches)
    {
        if (displayBatches.Count == 0)
        {
            return null;
        }

        if (displayBatches.Count == 1)
        {
            return displayBatches[0];
        }

        var active = displayBatches.Any(batch =>
            ActiveBatchStatuses.Contains(batch.Status, StringComparer.OrdinalIgnoreCase)
            && ShouldShowActiveBatch(batch));
        var failed = displayBatches.Any(batch =>
            FailedBatchStatuses.Contains(batch.Status, StringComparer.OrdinalIgnoreCase));
        var interrupted = displayBatches.Any(batch =>
            InterruptedBatchStatuses.Contains(batch.Status, StringComparer.OrdinalIgnoreCase));
        var completedAt = displayBatches.All(batch => batch.CompletedAt.HasValue)
            ? displayBatches.Max(batch => batch.CompletedAt)
            : null;

        return new IngestionBatch
        {
            Id = displayBatches
                .OrderByDescending(batch => batch.UpdatedAt)
                .ThenByDescending(batch => batch.StartedAt)
                .First().Id,
            Status = active ? "running" : failed ? "failed" : interrupted ? "abandoned" : "completed",
            SourcePath = displayBatches.Count == 1 ? displayBatches[0].SourcePath : "Multiple source folders",
            Category = displayBatches.Count == 1 ? displayBatches[0].Category : "Mixed",
            FilesTotal = displayBatches.Sum(batch => Math.Max(0, batch.FilesTotal)),
            FilesProcessed = displayBatches.Sum(batch => Math.Max(0, batch.FilesProcessed)),
            FilesIdentified = displayBatches.Sum(batch => Math.Max(0, batch.FilesIdentified)),
            FilesReview = displayBatches.Sum(batch => Math.Max(0, batch.FilesReview)),
            FilesNoMatch = displayBatches.Sum(batch => Math.Max(0, batch.FilesNoMatch)),
            FilesFailed = displayBatches.Sum(batch => Math.Max(0, batch.FilesFailed)),
            StartedAt = displayBatches.Min(batch => batch.StartedAt),
            CompletedAt = completedAt,
            CreatedAt = displayBatches.Min(batch => batch.CreatedAt),
            UpdatedAt = displayBatches.Max(batch => batch.UpdatedAt),
        };
    }

    private static IReadOnlyList<DisplayBatchGroup> BuildRecentBatchGroups(IReadOnlyList<IngestionBatch> recentBatches)
    {
        if (recentBatches.Count == 0)
        {
            return [];
        }

        var remaining = recentBatches
            .OrderByDescending(batch => batch.StartedAt)
            .ToList();
        var groups = new List<DisplayBatchGroup>();

        while (remaining.Count > 0)
        {
            var anchor = remaining[0];
            var anchorStart = anchor.StartedAt.ToUniversalTime();
            var anchorHasOutcomes = HasOutcomeCounters(anchor);
            var grouped = remaining
                .Where(batch => DurationBetween(batch.StartedAt.ToUniversalTime(), anchorStart) <= TimeSpan.FromMinutes(3))
                .Where(batch => ShouldIncludeInRecentBatchGroup(batch, anchorHasOutcomes))
                .ToList();

            foreach (var batch in grouped)
            {
                remaining.Remove(batch);
            }

            groups.Add(new DisplayBatchGroup(
                AggregateDisplayBatch(grouped) ?? anchor,
                grouped.Select(batch => batch.Id).ToArray()));
        }

        return groups;
    }

    private static TimeSpan DurationBetween(DateTimeOffset left, DateTimeOffset right)
    {
        var delta = left - right;
        return delta < TimeSpan.Zero ? -delta : delta;
    }

    private static bool ShouldIncludeInRecentBatchGroup(IngestionBatch batch, bool anchorHasOutcomes)
    {
        var batchHasOutcomes = HasOutcomeCounters(batch);
        return anchorHasOutcomes
            ? batchHasOutcomes
            : !batchHasOutcomes;
    }

    private static BatchSummaryTotals BuildSummaryTotals(
        IngestionBatch? displayBatch,
        LibraryItemLifecycleCounts lifecycle,
        LibraryItemProjectionSummary projection,
        int pendingReviewCount)
    {
        var reviewCount = Math.Max(0, pendingReviewCount);
        if (displayBatch is null)
        {
            return new(projection.TotalItems, lifecycle.Identified, reviewCount);
        }

        return new(
            Math.Max(0, displayBatch.FilesTotal),
            Math.Max(0, displayBatch.FilesIdentified),
            reviewCount);
    }

    private static bool ShouldShowActiveBatch(IngestionBatch batch)
    {
        if (HasOutcomeCounters(batch) || batch.FilesProcessed > 0)
        {
            return IsFreshActiveBatch(batch);
        }

        return DateTimeOffset.UtcNow - batch.StartedAt.ToUniversalTime() <= NoWorkBatchGrace;
    }

    private static bool ShouldIncludeInDisplayBatchGroup(IngestionBatch batch, bool anchorHasOutcomes)
    {
        if (!anchorHasOutcomes)
        {
            return true;
        }

        return HasOutcomeCounters(batch);
    }

    private static bool HasOutcomeCounters(IngestionBatch batch) =>
        batch.FilesIdentified + batch.FilesReview + batch.FilesNoMatch + batch.FilesFailed > 0;

    private static bool IsFreshActiveBatch(IngestionBatch batch)
    {
        var updatedAt = batch.UpdatedAt == default ? batch.StartedAt : batch.UpdatedAt;
        return DateTimeOffset.UtcNow - updatedAt.ToUniversalTime() <= ActiveBatchFreshness;
    }

    private async Task<IReadOnlyList<IngestionBatch>> ProjectRecentBatchesForDisplayAsync(
        IReadOnlyList<IngestionBatch> recentBatches,
        CancellationToken ct)
    {
        if (recentBatches.Count == 0)
        {
            return recentBatches;
        }

        var projected = new List<IngestionBatch>(recentBatches.Count);
        foreach (var batch in recentBatches)
        {
            var snapshot = await ReadBatchTerminalSnapshotAsync(batch.Id, ct);
            projected.Add(ProjectBatchForDisplay(batch, snapshot));
        }

        return projected;
    }

    private static IngestionBatch ProjectBatchForDisplay(IngestionBatch batch, BatchTerminalSnapshot snapshot)
    {
        if (!snapshot.HasRows)
        {
            return batch;
        }

        var identified = Math.Max(0, snapshot.Identified);
        var review = Math.Max(0, snapshot.Review);
        var noMatch = Math.Max(0, snapshot.NoMatch + snapshot.OperationNoMatch);
        var failed = Math.Max(0, snapshot.Failed + snapshot.OperationFailed);
        var skipped = Math.Max(0, snapshot.OperationSkipped + snapshot.OperationOnlyTerminal);
        var terminal = identified + review + noMatch + failed + skipped;
        var total = batch.FilesTotal > 0
            ? batch.FilesTotal
            : Math.Max(batch.FilesProcessed, snapshot.TotalJobs + snapshot.OperationTerminal);
        if (total > 0)
        {
            terminal = Math.Clamp(terminal, 0, total);
        }

        return new IngestionBatch
        {
            Id = batch.Id,
            Status = batch.Status,
            SourcePath = batch.SourcePath,
            Category = batch.Category,
            FilesTotal = total,
            FilesProcessed = terminal,
            FilesIdentified = identified,
            FilesReview = review,
            FilesNoMatch = noMatch,
            FilesFailed = failed,
            StartedAt = batch.StartedAt,
            CompletedAt = batch.CompletedAt,
            CreatedAt = batch.CreatedAt,
            UpdatedAt = batch.UpdatedAt,
        };
    }

    private List<IngestionSourceGroupDto> BuildSourceGroups(IReadOnlyDictionary<string, FolderStatsRow> folderStats)
    {
        var groups = new Dictionary<string, Dictionary<string, List<IngestionSourceFolderDto>>>(StringComparer.OrdinalIgnoreCase)
        {
            ["Watch"] = new(StringComparer.OrdinalIgnoreCase),
            ["Listen"] = new(StringComparer.OrdinalIgnoreCase),
            ["Read"] = new(StringComparer.OrdinalIgnoreCase),
            ["View"] = new(StringComparer.OrdinalIgnoreCase),
        };

        foreach (var library in _configLoader.LoadLibraries().Libraries)
        {
            var category = NormalizeCategory(library.Category ?? library.Name);
            var intent = ResolveIntent(library.Area, category);
            if (!groups.TryGetValue(intent, out var libraries))
            {
                libraries = new(StringComparer.OrdinalIgnoreCase);
                groups[intent] = libraries;
            }

            if (!libraries.TryGetValue(category, out var folders))
            {
                folders = [];
                libraries[category] = folders;
            }

            foreach (var path in EffectiveSourcePaths(library))
            {
                var status = ProbeFolder(path);
                var stats = folderStats.GetValueOrDefault(path);
                folders.Add(new IngestionSourceFolderDto
                {
                    Path = path,
                    MediaType = string.Join(", ", library.MediaTypes.DefaultIfEmpty(category)),
                    Purpose = ResolvePurpose(library),
                    ScanMode = library.AcceptedIntakeModes.Contains(
                            LibraryIntakeModes.IncomingFolder,
                            StringComparer.OrdinalIgnoreCase)
                        ? "automatic"
                        : "direct",
                    LastScan = ParseDate(stats?.LastScan),
                    ItemCount = stats is null ? 0 : ToInt(stats.ItemCount),
                    UnresolvedCount = stats is null ? 0 : ToInt(stats.UnresolvedCount),
                    Status = status.Label,
                    IsReachable = status.IsReachable,
                    PermissionsValid = status.PermissionsValid,
                    MusicNote = category.Equals("Music", StringComparison.OrdinalIgnoreCase)
                        ? "Preserve album folders; prefer tags and fingerprints before organization."
                        : null,
                });
            }
        }

        return groups
            .Select(group => new IngestionSourceGroupDto
            {
                Intent = group.Key,
                Libraries = group.Value
                    .Select(library => new IngestionSourceLibraryDto
                    {
                        Label = library.Key,
                        Folders = library.Value,
                    })
                    .Where(library => library.Folders.Count > 0)
                    .ToList(),
            })
            .ToList();
    }

    private IngestionOrganizationRulesDto BuildOrganizationRules()
    {
        var options = _ingestionOptions.Value;
        var folderTemplate = StringHelpers.FirstNonBlankOr(string.Empty, options.OrganizationTemplate, _configLoader.LoadCore().OrganizationTemplate, "Not configured");
        var filenameTemplate = Path.GetFileName(folderTemplate.Replace('/', Path.DirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(filenameTemplate))
        {
            filenameTemplate = folderTemplate;
        }

        return new IngestionOrganizationRulesDto
        {
            RenameEnabled = options.AutoOrganize,
            MoveEnabled = options.AutoOrganize,
            PreviewRequired = true,
            FolderTemplateSummary = folderTemplate,
            FilenameTemplateSummary = filenameTemplate,
            LastOrganizationRun = null,
            MusicBehavior = "Music uses conservative organization and should preserve album folders unless a library rule says otherwise.",
        };
    }

    private static List<IngestionPipelineStageDto> BuildPipelineStages(
        IReadOnlyDictionary<string, int> pipelineRows,
        IReadOnlyDictionary<string, int> ingestionRows,
        LibraryItemLifecycleCounts lifecycle,
        LibraryItemProjectionSummary projection,
        IngestionBatch? displayBatch,
        int? pendingReviewCount = null)
    {
        var identityTotal = pipelineRows.Values.Sum();
        var duplicates = Count(ingestionRows, "duplicate") + Count(ingestionRows, "same_path_redetected");
        var skipped = Count(ingestionRows, "skipped_non_media");
        var failed = Count(pipelineRows, nameof(IdentityJobState.Failed)) + Count(ingestionRows, "failed") + Count(ingestionRows, "missing");
        var skippedOrDuplicate = duplicates + skipped;
        var batchTotal = Math.Max(0, displayBatch?.FilesTotal ?? projection.TotalItems);
        if (batchTotal == 0)
        {
            batchTotal = Math.Max(0, identityTotal + skippedOrDuplicate);
        }

        var review = Math.Max(0, pendingReviewCount ?? (displayBatch is null
            ? lifecycle.InReview
            : displayBatch.FilesReview));

        var retailMatched = SumStates(pipelineRows, RetailMatchedStates);
        var retailReview = SumStates(pipelineRows, RetailReviewStates);
        var retailNoMatch = SumStates(pipelineRows, RetailNoMatchStates);
        var qidNoMatch = Count(pipelineRows, nameof(IdentityJobState.QidNoMatch));
        var noMatch = retailNoMatch + qidNoMatch;

        var wikidataResolved = SumStates(pipelineRows, WikidataResolvedStates);
        var wikidataReview = SumStates(pipelineRows, WikidataReviewStates);
        var enriched = SumStates(pipelineRows, EnrichmentCompleteStates);
        var terminal = ClampStageCount(enriched + review + noMatch + failed + skippedOrDuplicate, batchTotal);
        var detected = batchTotal;
        var parsed = ClampStageCount(Math.Max(identityTotal + skippedOrDuplicate, terminal), batchTotal);
        var identified = ClampStageCount(retailMatched + review + noMatch + failed + skippedOrDuplicate, batchTotal);
        var matched = ClampStageCount(retailMatched, batchTotal);
        var retailReviewTotal = ClampStageCount(retailReview + retailNoMatch, batchTotal);
        var canonicalized = ClampStageCount(wikidataResolved, batchTotal);
        var wikidataReviewTotal = ClampStageCount(wikidataReview, batchTotal);
        var registered = ClampStageCount(enriched, batchTotal);

        return
        [
            Stage("detected", "Detected", detected, batchTotal, "Files found and handed to ingestion"),
            Stage("parsed", "Parsed", parsed, batchTotal, "Names and embedded metadata interpreted"),
            Stage("identified", "Identified", identified, batchTotal, "Recognized as media and routed into identity matching"),
            Stage("matched", "Matched", matched, batchTotal, "Matched with retail metadata providers"),
            Stage("retail_review", "Retail Review", retailReviewTotal, batchTotal, "Retail matches needing review or files with no retail match"),
            Stage("canonicalized", "Media QIDs", canonicalized, batchTotal, "Media items linked to a Wikidata identity when available"),
            Stage("wikidata_review", "Wikidata Review", wikidataReviewTotal, batchTotal, "Wikidata matches needing review or files with no QID"),
            Stage("enriched", "Enriched", registered, batchTotal, "Artwork, people, series, genres, universes added"),
            Stage("organized", "Organized", registered, batchTotal, "Rename and folder operations completed"),
            Stage("registered", "Registered", registered, batchTotal, "Ready in the library"),
            Stage("needs_review", "Needs Review", review, batchTotal, "Waiting for a curator decision"),
            Stage("duplicate", "Duplicates", duplicates, batchTotal, "Files already tracked or identical to an existing item"),
            Stage("skipped", "Skipped", skipped, batchTotal, "Non-media sidecars ignored"),
            Stage("failed", "Failed", failed, batchTotal, "Could not finish automatically"),
        ];
    }

    private async Task<List<IngestionStageProgressDto>> BuildNumberedStageProgressAsync(
        IReadOnlyList<IngestionBatch> displayBatches,
        IReadOnlyList<IngestionPipelineStageDto> pipelineStages,
        IReadOnlyList<IngestionCurrentActivityDto> currentActivities,
        int pendingReviewCount,
        int summaryTotal,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var byKey = pipelineStages.ToDictionary(stage => stage.Key, StringComparer.OrdinalIgnoreCase);
        var totalFiles = pipelineStages.Select(stage => stage.TotalCount).DefaultIfEmpty(0).Max();
        if (totalFiles <= 0)
        {
            totalFiles = Math.Max(0, summaryTotal);
        }

        var detected = CountStage(byKey, "detected");
        var parsed = CountStage(byKey, "parsed");
        var duplicates = CountStage(byKey, "duplicate");
        var skipped = CountStage(byKey, "skipped");
        var duplicateOrSkipped = duplicates + skipped;
        var failed = CountStage(byKey, "failed");
        var terminalOther = duplicateOrSkipped + failed;
        var review = Math.Max(0, pendingReviewCount);
        var identified = CountStage(byKey, "identified");
        var canonicalized = CountStage(byKey, "canonicalized");
        var wikidataReview = CountStage(byKey, "wikidata_review");
        var registered = CountStage(byKey, "registered");
        var needsReview = CountStage(byKey, "needs_review");
        var retailDone = ClampStageCount(identified, totalFiles);
        var retailReviewTotal = ClampStageCount(CountStage(byKey, "retail_review"), totalFiles);
        var wikidataDone = ClampStageCount(
            canonicalized + wikidataReview + review + terminalOther,
            totalFiles);
        var fileReady = ClampStageCount(
            registered + needsReview + wikidataReview + terminalOther,
            totalFiles);
        var optionalEnrichmentDone = fileReady;
        var reviewResolved = totalFiles > 0
            ? Math.Clamp(totalFiles - review, 0, totalFiles)
            : 0;
        var scanAccepted = Math.Max(0, totalFiles - duplicateOrSkipped - failed);
        var terminalBatch = displayBatches.Count > 0
            && displayBatches.All(IsTerminalBatchForStageProgress);

        var batchIds = displayBatches.Select(batch => batch.Id).Distinct().ToArray();
        var artifactCounts = await ReadNumberedStageArtifactCountsAsync(batchIds, ct).ConfigureAwait(false);
        ApplyBatchCategoryFallbacks(displayBatches, artifactCounts);
        var unidentified = Math.Max(0, totalFiles
            - artifactCounts.BooksCount
            - artifactCounts.MoviesCount
            - artifactCounts.TvShowsCount
            - artifactCounts.MusicCount
            - artifactCounts.ComicsCount
            - artifactCounts.AudiobooksCount);

        return
        [
            CreateNumberedStage(
                1,
                "scan",
                "Scanned",
                detected,
                totalFiles,
                "scanned",
                scanAccepted,
                ActivityFor(currentActivities, "scanning", "scan"),
                DetailItems(
                    Detail("Books", artifactCounts.BooksCount, "neutral", "books"),
                    Detail("Movies", artifactCounts.MoviesCount, "neutral", "movies"),
                    Detail("TV", artifactCounts.TvShowsCount, "neutral", "tv"),
                    Detail("Music", artifactCounts.MusicCount, "neutral", "music"),
                    Detail("Comics", artifactCounts.ComicsCount, "neutral", "comics"),
                    Detail("Audiobooks", artifactCounts.AudiobooksCount, "neutral", "audiobooks"),
                    Detail("Unidentified", unidentified, unidentified > 0 ? "warning" : "neutral", "review"),
                    Detail("Skipped / duplicate", duplicateOrSkipped, duplicateOrSkipped > 0 ? "muted" : "neutral", "skip"),
                    Detail("Failed", failed, failed > 0 ? "danger" : "neutral", "warning")),
                terminalBatch: terminalBatch),
            CreateNumberedStage(
                2,
                "retail",
                "Retail Match",
                retailDone,
                totalFiles,
                "matches",
                artifactCounts.ProviderMatches,
                ActivityFor(currentActivities, "retail", "metadata"),
                DetailItems(
                    Detail("Matches", artifactCounts.ProviderMatches, "success", "match"),
                    Detail("Books", artifactCounts.RetailBooksCount, "neutral", "books"),
                    Detail("Movies", artifactCounts.RetailMoviesCount, "neutral", "movies"),
                    Detail("TV", artifactCounts.RetailTvShowsCount, "neutral", "tv"),
                    Detail("Music", artifactCounts.RetailMusicCount, "neutral", "music"),
                    Detail("Comics", artifactCounts.RetailComicsCount, "neutral", "comics"),
                    Detail("Audiobooks", artifactCounts.RetailAudiobooksCount, "neutral", "audiobooks"),
                    Detail("Metadata fields", artifactCounts.MetadataFields, "info", "metadata"),
                    Detail("Cover art assets", artifactCounts.PrimaryArtworkCount, "success", "artwork"),
                    Detail("Unresolved", Math.Max(retailReviewTotal, artifactCounts.RetailUnresolvedCount), Math.Max(retailReviewTotal, artifactCounts.RetailUnresolvedCount) > 0 ? "warning" : "neutral", "review"),
                    Detail("Failed", Math.Max(failed, artifactCounts.RetailFailedCount), Math.Max(failed, artifactCounts.RetailFailedCount) > 0 ? "danger" : "neutral", "warning")),
                terminalBatch: terminalBatch),
            CreateNumberedStage(
                3,
                "wikidata",
                "Wikidata",
                wikidataDone,
                totalFiles,
                "QIDs",
                Math.Max(artifactCounts.RelevantQidCount, Math.Max(artifactCounts.MediaQidCount, artifactCounts.FilesWithQid)),
                ActivityFor(currentActivities, "wikidata"),
                DetailItems(
                    Detail("Media QIDs", Math.Max(artifactCounts.MediaQidCount, artifactCounts.FilesWithQid), "success", "qid"),
                    Detail("Relevant QIDs", artifactCounts.RelevantQidCount, "info", "qid"),
                    Detail("Related QIDs discovered", artifactCounts.RelatedQidCount, "info", "qid"),
                    Detail("Unresolved", Math.Max(wikidataReview + review, artifactCounts.WikidataUnresolvedCount), Math.Max(wikidataReview + review, artifactCounts.WikidataUnresolvedCount) > 0 ? "warning" : "neutral", "review"),
                    Detail("Failed", Math.Max(failed, artifactCounts.WikidataFailedCount), Math.Max(failed, artifactCounts.WikidataFailedCount) > 0 ? "danger" : "neutral", "warning")),
                terminalBatch: terminalBatch),
            CreateNumberedStage(
                4,
                "people",
                "People",
                optionalEnrichmentDone,
                totalFiles,
                "people",
                Math.Max(artifactCounts.PeopleCount, ParseMetricValue(ActivityFor(currentActivities, "people")?.MetricValue)),
                ActivityFor(currentActivities, "people"),
                DetailItems(
                    Detail("Cast", artifactCounts.CastCount, "neutral", "people"),
                    Detail("Directors", artifactCounts.DirectorCount, "neutral", "people"),
                    Detail("Authors", artifactCounts.AuthorCount, "neutral", "people"),
                    Detail("Narrators", artifactCounts.NarratorCount, "neutral", "people"),
                    Detail("Music artists", artifactCounts.MusicArtistCount, "neutral", "people"),
                    Detail("Creators / crew", artifactCounts.CreatorCrewCount, "neutral", "people"),
                    Detail("Deeply enriched people", artifactCounts.DeepPeopleCount, "success", "people")),
                terminalBatch: terminalBatch),
            CreateNumberedStage(
                5,
                "universes",
                "Universes",
                optionalEnrichmentDone,
                totalFiles,
                "links",
                artifactCounts.UniverseLinkCount,
                ActivityFor(currentActivities, "relationships"),
                DetailItems(
                    Detail("Relationship links", artifactCounts.UniverseLinkCount, "success", "relationships"),
                    Detail("Files with universe links", artifactCounts.FilesWithUniverseLinks, "success", "relationships"),
                    Detail("Series / shelves", artifactCounts.SeriesShelfCount, "neutral", "relationships"),
                    Detail("Universe roots", artifactCounts.UniverseRootCount, "neutral", "relationships"),
                    Detail("Characters / locations / orgs", artifactCounts.FictionalEntityCount, "neutral", "relationships"),
                    Detail("Adaptation / story links", artifactCounts.AdaptationStoryLinkCount, "neutral", "relationships")),
                terminalBatch: terminalBatch),
            CreateNumberedStage(
                6,
                "artwork",
                "Artwork",
                optionalEnrichmentDone,
                totalFiles,
                "assets",
                artifactCounts.TotalArtworkCount,
                ActivityFor(currentActivities, "artwork", "deep_artwork", "images"),
                DetailItems(
                    Detail("Covers / posters", artifactCounts.PrimaryArtworkCount, "success", "artwork"),
                    Detail("People headshots", artifactCounts.HeadshotCount, "neutral", "artwork"),
                    Detail("Backdrops", artifactCounts.BackdropBannerCount, "neutral", "artwork"),
                    Detail("Season / episode art", artifactCounts.SeasonEpisodeArtworkCount, "neutral", "artwork"),
                    Detail("Album / music art", artifactCounts.MusicArtworkCount, "neutral", "artwork"),
                    Detail("Logos", artifactCounts.LogoCount, "neutral", "artwork")),
                terminalBatch: terminalBatch),
        ];
    }

    private async Task<NumberedStageArtifactCounts> ReadNumberedStageArtifactCountsAsync(
        IReadOnlyCollection<Guid> batchIds,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        using var conn = _db.CreateConnection();
        var scopedBatchIds = ToBatchIdBlobs(batchIds);
        var hasBatchScope = batchIds.Count > 0 ? 1 : 0;

        var counts = await conn.QueryFirstOrDefaultAsync<NumberedStageArtifactCounts>("""
            WITH scoped_jobs AS (
                SELECT id, entity_id, state, media_type, resolved_qid, updated_at, created_at
                FROM identity_jobs
                WHERE @hasBatchScope = 0 OR ingestion_run_id IN @batchIds
            ),
            latest_jobs AS (
                SELECT
                    id,
                    entity_id,
                    state,
                    media_type,
                    resolved_qid,
                    ROW_NUMBER() OVER (
                        PARTITION BY entity_id
                        ORDER BY updated_at DESC, created_at DESC
                    ) AS rn
                FROM scoped_jobs
                WHERE entity_id IS NOT NULL
            ),
            latest_entities AS (
                SELECT id, entity_id, state, media_type, resolved_qid
                FROM latest_jobs
                WHERE rn = 1
            ),
            batch_assets AS (
                SELECT DISTINCT entity_id
                FROM latest_entities
                WHERE entity_id IS NOT NULL
            ),
            lineage AS (
                SELECT DISTINCT
                    ba.entity_id AS asset_id,
                    w.id AS work_id,
                    w.collection_id AS collection_id,
                    w.wikidata_qid AS work_qid,
                    p.id AS parent_work_id,
                    p.collection_id AS parent_collection_id,
                    p.wikidata_qid AS parent_work_qid,
                    gp.id AS root_work_id,
                    gp.collection_id AS root_collection_id,
                    gp.wikidata_qid AS root_work_qid
                FROM batch_assets ba
                LEFT JOIN media_assets ma ON ma.id = ba.entity_id
                LEFT JOIN editions e ON e.id = ma.edition_id
                LEFT JOIN works w ON w.id = e.work_id
                LEFT JOIN works p ON p.id = w.parent_work_id
                LEFT JOIN works gp ON gp.id = p.parent_work_id
            ),
            entity_scope AS (
                SELECT asset_id AS id FROM lineage WHERE asset_id IS NOT NULL
                UNION
                SELECT work_id AS id FROM lineage WHERE work_id IS NOT NULL
                UNION
                SELECT parent_work_id AS id FROM lineage WHERE parent_work_id IS NOT NULL
                UNION
                SELECT root_work_id AS id FROM lineage WHERE root_work_id IS NOT NULL
            ),
            batch_collections AS (
                SELECT collection_id AS id FROM lineage WHERE collection_id IS NOT NULL
                UNION
                SELECT parent_collection_id AS id FROM lineage WHERE parent_collection_id IS NOT NULL
                UNION
                SELECT root_collection_id AS id FROM lineage WHERE root_collection_id IS NOT NULL
            ),
            batch_work_qids AS (
                SELECT work_qid AS qid FROM lineage WHERE work_qid IS NOT NULL AND work_qid <> ''
                UNION
                SELECT parent_work_qid AS qid FROM lineage WHERE parent_work_qid IS NOT NULL AND parent_work_qid <> ''
                UNION
                SELECT root_work_qid AS qid FROM lineage WHERE root_work_qid IS NOT NULL AND root_work_qid <> ''
                UNION
                SELECT cv.value AS qid
                FROM canonical_values cv
                WHERE cv.key = 'wikidata_qid'
                  AND cv.value IS NOT NULL
                  AND cv.value <> ''
                  AND (@hasBatchScope = 0 OR cv.entity_id IN (SELECT id FROM entity_scope))
            ),
            retail_matches AS (
                SELECT DISTINCT sj.entity_id, sj.media_type
                FROM retail_match_candidates r
                JOIN scoped_jobs sj ON sj.id = r.job_id
                WHERE r.outcome IN ('AutoAccepted', 'Ambiguous')
            ),
            media_qids AS (
                SELECT resolved_qid AS qid
                FROM latest_entities
                WHERE resolved_qid IS NOT NULL
                  AND resolved_qid <> ''
                  AND resolved_qid <> 'NF'
                UNION
                SELECT qid
                FROM batch_work_qids
            ),
            related_qids AS (
                SELECT p.wikidata_qid AS qid
                FROM persons p
                WHERE p.wikidata_qid IS NOT NULL
                  AND p.wikidata_qid <> ''
                  AND (
                        @hasBatchScope = 0
                        OR EXISTS (
                            SELECT 1
                            FROM person_media_links pml
                            WHERE pml.person_id = p.id
                              AND pml.media_asset_id IN (SELECT asset_id FROM lineage WHERE asset_id IS NOT NULL)
                        )
                  )
                UNION
                SELECT c.wikidata_qid AS qid
                FROM collections c
                WHERE c.wikidata_qid IS NOT NULL
                  AND c.wikidata_qid <> ''
                  AND (@hasBatchScope = 0 OR c.id IN (SELECT id FROM batch_collections))
                UNION
                SELECT fe.wikidata_qid AS qid
                FROM fictional_entities fe
                WHERE fe.wikidata_qid IS NOT NULL
                  AND fe.wikidata_qid <> ''
                  AND (
                        @hasBatchScope = 0
                        OR EXISTS (
                            SELECT 1
                            FROM fictional_entity_work_links fewl
                            WHERE fewl.entity_id = fe.id
                              AND fewl.work_qid IN (SELECT qid FROM batch_work_qids)
                        )
                  )
                UNION
                SELECT smi.series_qid AS qid
                FROM series_manifest_items smi
                WHERE smi.series_qid IS NOT NULL
                  AND smi.series_qid <> ''
                  AND (@hasBatchScope = 0 OR smi.collection_id IN (SELECT id FROM batch_collections) OR smi.linked_work_id IN (SELECT id FROM entity_scope))
                UNION
                SELECT smi.item_qid AS qid
                FROM series_manifest_items smi
                WHERE smi.item_qid IS NOT NULL
                  AND smi.item_qid <> ''
                  AND (@hasBatchScope = 0 OR smi.collection_id IN (SELECT id FROM batch_collections) OR smi.linked_work_id IN (SELECT id FROM entity_scope))
            )
            SELECT
                (
                    SELECT COUNT(*)
                    FROM retail_matches
                ) AS ProviderMatches,
                (SELECT COUNT(DISTINCT entity_id) FROM latest_entities WHERE LOWER(REPLACE(COALESCE(media_type, ''), ' ', '')) IN ('books','book')) AS BooksCount,
                (SELECT COUNT(DISTINCT entity_id) FROM latest_entities WHERE LOWER(REPLACE(COALESCE(media_type, ''), ' ', '')) IN ('movies','movie')) AS MoviesCount,
                (SELECT COUNT(DISTINCT entity_id) FROM latest_entities WHERE LOWER(REPLACE(COALESCE(media_type, ''), ' ', '')) IN ('tv','tvshows','tvshow','shows')) AS TvShowsCount,
                (SELECT COUNT(DISTINCT entity_id) FROM latest_entities WHERE LOWER(REPLACE(COALESCE(media_type, ''), ' ', '')) IN ('music','musictrack','musicalbum','album','song')) AS MusicCount,
                (SELECT COUNT(DISTINCT entity_id) FROM latest_entities WHERE LOWER(REPLACE(COALESCE(media_type, ''), ' ', '')) IN ('comics','comic')) AS ComicsCount,
                (SELECT COUNT(DISTINCT entity_id) FROM latest_entities WHERE LOWER(REPLACE(COALESCE(media_type, ''), ' ', '')) IN ('audiobooks','audiobook')) AS AudiobooksCount,
                (SELECT COUNT(DISTINCT entity_id) FROM retail_matches WHERE LOWER(REPLACE(COALESCE(media_type, ''), ' ', '')) IN ('books','book')) AS RetailBooksCount,
                (SELECT COUNT(DISTINCT entity_id) FROM retail_matches WHERE LOWER(REPLACE(COALESCE(media_type, ''), ' ', '')) IN ('movies','movie')) AS RetailMoviesCount,
                (SELECT COUNT(DISTINCT entity_id) FROM retail_matches WHERE LOWER(REPLACE(COALESCE(media_type, ''), ' ', '')) IN ('tv','tvshows','tvshow','shows')) AS RetailTvShowsCount,
                (SELECT COUNT(DISTINCT entity_id) FROM retail_matches WHERE LOWER(REPLACE(COALESCE(media_type, ''), ' ', '')) IN ('music','musictrack','musicalbum','album','song')) AS RetailMusicCount,
                (SELECT COUNT(DISTINCT entity_id) FROM retail_matches WHERE LOWER(REPLACE(COALESCE(media_type, ''), ' ', '')) IN ('comics','comic')) AS RetailComicsCount,
                (SELECT COUNT(DISTINCT entity_id) FROM retail_matches WHERE LOWER(REPLACE(COALESCE(media_type, ''), ' ', '')) IN ('audiobooks','audiobook')) AS RetailAudiobooksCount,
                (
                    SELECT COUNT(*)
                    FROM metadata_claims mc
                    WHERE @hasBatchScope = 0 OR mc.entity_id IN (SELECT id FROM entity_scope)
                ) AS MetadataFields,
                (
                    SELECT COUNT(DISTINCT cv.entity_id)
                    FROM canonical_values cv
                    WHERE cv.key IN ('cover', 'cover_url')
                      AND cv.value LIKE 'http%'
                      AND (@hasBatchScope = 0 OR cv.entity_id IN (SELECT id FROM entity_scope))
                ) AS CoverUrls,
                (
                    SELECT COUNT(*)
                    FROM entity_assets ea
                    WHERE ea.asset_type = 'CoverArt'
                      AND (@hasBatchScope = 0 OR ea.entity_id IN (SELECT id FROM entity_scope))
                ) AS PrimaryArtworkCount,
                (
                    SELECT COUNT(DISTINCT le.entity_id)
                    FROM latest_entities le
                    WHERE (le.resolved_qid IS NOT NULL AND le.resolved_qid <> '' AND le.resolved_qid <> 'NF')
                       OR EXISTS (
                            SELECT 1
                            FROM lineage l
                            JOIN canonical_values cv ON cv.entity_id IN (
                                l.asset_id,
                                l.work_id,
                                l.parent_work_id,
                                l.root_work_id
                            )
                            WHERE l.asset_id = le.entity_id
                              AND cv.key = 'wikidata_qid'
                              AND cv.value IS NOT NULL
                              AND cv.value <> ''
                       )
                ) AS FilesWithQid,
                (
                    SELECT COUNT(DISTINCT qid)
                    FROM media_qids
                ) AS MediaQidCount,
                (
                    SELECT COUNT(DISTINCT qid)
                    FROM related_qids
                ) AS RelatedQidCount,
                (
                    SELECT COUNT(DISTINCT qid)
                    FROM (
                        SELECT qid FROM media_qids
                        UNION
                        SELECT qid FROM related_qids
                    )
                ) AS RelevantQidCount,
                (
                    SELECT COUNT(DISTINCT entity_id)
                    FROM latest_entities
                    WHERE state IN ('RetailNoMatch', 'RetailMatchedNeedsReview')
                ) AS RetailUnresolvedCount,
                (
                    SELECT COUNT(DISTINCT entity_id)
                    FROM latest_entities
                    WHERE state = 'Failed'
                ) AS RetailFailedCount,
                (
                    SELECT COUNT(DISTINCT entity_id)
                    FROM latest_entities
                    WHERE state IN ('QidNoMatch', 'QidNeedsReview')
                ) AS WikidataUnresolvedCount,
                (
                    SELECT COUNT(DISTINCT entity_id)
                    FROM latest_entities
                    WHERE state = 'Failed'
                ) AS WikidataFailedCount,
                (
                    SELECT COUNT(*)
                    FROM person_media_links pml
                    WHERE @hasBatchScope = 0 OR pml.media_asset_id IN (SELECT asset_id FROM lineage WHERE asset_id IS NOT NULL)
                ) AS PeopleCount,
                (
                    SELECT COUNT(DISTINCT person_id)
                    FROM person_media_links
                    WHERE role IN ('Actor', 'Voice Actor')
                      AND (@hasBatchScope = 0 OR media_asset_id IN (SELECT asset_id FROM lineage WHERE asset_id IS NOT NULL))
                ) AS CastCount,
                (
                    SELECT COUNT(DISTINCT person_id)
                    FROM person_media_links
                    WHERE role = 'Director'
                      AND (@hasBatchScope = 0 OR media_asset_id IN (SELECT asset_id FROM lineage WHERE asset_id IS NOT NULL))
                ) AS DirectorCount,
                (
                    SELECT COUNT(DISTINCT person_id)
                    FROM person_media_links
                    WHERE role = 'Author'
                      AND (@hasBatchScope = 0 OR media_asset_id IN (SELECT asset_id FROM lineage WHERE asset_id IS NOT NULL))
                ) AS AuthorCount,
                (
                    SELECT COUNT(DISTINCT person_id)
                    FROM person_media_links
                    WHERE role = 'Narrator'
                      AND (@hasBatchScope = 0 OR media_asset_id IN (SELECT asset_id FROM lineage WHERE asset_id IS NOT NULL))
                ) AS NarratorCount,
                (
                    SELECT COUNT(DISTINCT person_id)
                    FROM person_media_links
                    WHERE role IN ('Artist', 'Performer', 'Composer')
                      AND (@hasBatchScope = 0 OR media_asset_id IN (SELECT asset_id FROM lineage WHERE asset_id IS NOT NULL))
                ) AS MusicArtistCount,
                (
                    SELECT COUNT(DISTINCT person_id)
                    FROM person_media_links
                    WHERE role IN ('Screenwriter', 'Illustrator', 'Composer')
                      AND (@hasBatchScope = 0 OR media_asset_id IN (SELECT asset_id FROM lineage WHERE asset_id IS NOT NULL))
                ) AS CreatorCrewCount,
                (
                    SELECT COUNT(DISTINCT p.id)
                    FROM persons p
                    WHERE p.wikidata_qid IS NOT NULL
                      AND p.wikidata_qid <> ''
                      AND (
                            @hasBatchScope = 0
                            OR EXISTS (
                                SELECT 1
                                FROM person_media_links pml
                                WHERE pml.person_id = p.id
                                  AND pml.media_asset_id IN (SELECT asset_id FROM lineage WHERE asset_id IS NOT NULL)
                            )
                      )
                ) AS DeepPeopleCount,
                (
                    SELECT COUNT(*)
                    FROM (
                        SELECT id FROM collection_relationships
                        WHERE @hasBatchScope = 0 OR collection_id IN (SELECT id FROM batch_collections)
                        UNION ALL
                        SELECT id FROM entity_relationships
                        WHERE @hasBatchScope = 0
                           OR subject_qid IN (SELECT qid FROM batch_work_qids)
                           OR object_qid IN (SELECT qid FROM batch_work_qids)
                           OR context_work_qid IN (SELECT qid FROM batch_work_qids)
                        UNION ALL
                        SELECT entity_id FROM fictional_entity_work_links
                        WHERE @hasBatchScope = 0 OR work_qid IN (SELECT qid FROM batch_work_qids)
                        UNION ALL
                        SELECT series_qid FROM series_members
                        WHERE @hasBatchScope = 0
                           OR work_qid IN (SELECT qid FROM batch_work_qids)
                           OR series_qid IN (SELECT qid FROM batch_work_qids)
                    )
                ) AS UniverseLinkCount,
                (
                    SELECT COUNT(DISTINCT asset_id)
                    FROM lineage
                    WHERE @hasBatchScope = 0
                       OR collection_id IS NOT NULL
                       OR parent_collection_id IS NOT NULL
                       OR root_collection_id IS NOT NULL
                       OR work_qid IN (SELECT qid FROM batch_work_qids)
                       OR parent_work_qid IN (SELECT qid FROM batch_work_qids)
                       OR root_work_qid IN (SELECT qid FROM batch_work_qids)
                ) AS FilesWithUniverseLinks,
                (
                    SELECT COUNT(DISTINCT series_qid)
                    FROM series_members
                    WHERE @hasBatchScope = 0
                       OR work_qid IN (SELECT qid FROM batch_work_qids)
                       OR series_qid IN (SELECT qid FROM batch_work_qids)
                ) AS SeriesShelfCount,
                (
                    SELECT COUNT(DISTINCT id)
                    FROM collections
                    WHERE wikidata_qid IS NOT NULL
                      AND wikidata_qid <> ''
                      AND (@hasBatchScope = 0 OR id IN (SELECT id FROM batch_collections))
                ) AS UniverseRootCount,
                (
                    SELECT COUNT(DISTINCT fe.id)
                    FROM fictional_entities fe
                    WHERE @hasBatchScope = 0
                       OR EXISTS (
                            SELECT 1
                            FROM fictional_entity_work_links fewl
                            WHERE fewl.entity_id = fe.id
                              AND fewl.work_qid IN (SELECT qid FROM batch_work_qids)
                       )
                ) AS FictionalEntityCount,
                (
                    SELECT COUNT(*)
                    FROM entity_relationships
                    WHERE @hasBatchScope = 0
                       OR subject_qid IN (SELECT qid FROM batch_work_qids)
                       OR object_qid IN (SELECT qid FROM batch_work_qids)
                       OR context_work_qid IN (SELECT qid FROM batch_work_qids)
                ) AS AdaptationStoryLinkCount,
                (
                    SELECT COUNT(*)
                    FROM entity_assets ea
                    WHERE ea.asset_type IN ('Background', 'Logo', 'SeasonPoster', 'SeasonThumb', 'EpisodeStill')
                      AND (@hasBatchScope = 0 OR ea.entity_id IN (SELECT id FROM entity_scope))
                ) AS DeepArtworkCount,
                (
                    SELECT COUNT(DISTINCT cv.entity_id)
                    FROM canonical_values cv
                    WHERE cv.key = 'stage3_enhanced_at'
                      AND (@hasBatchScope = 0 OR cv.entity_id IN (SELECT id FROM entity_scope))
                ) AS DeepArtworkFiles,
                (
                    SELECT COUNT(*)
                    FROM entity_assets ea
                    WHERE ea.asset_type = 'Headshot'
                      AND (@hasBatchScope = 0 OR ea.entity_id IN (SELECT id FROM entity_scope))
                ) AS HeadshotCount,
                (
                    SELECT COUNT(*)
                    FROM entity_assets ea
                    WHERE ea.asset_type IN ('Background', 'Logo')
                      AND (@hasBatchScope = 0 OR ea.entity_id IN (SELECT id FROM entity_scope))
                ) AS BackdropBannerLogoCount,
                (
                    SELECT COUNT(*)
                    FROM entity_assets ea
                    WHERE ea.asset_type = 'Background'
                      AND (@hasBatchScope = 0 OR ea.entity_id IN (SELECT id FROM entity_scope))
                ) AS BackdropBannerCount,
                (
                    SELECT COUNT(*)
                    FROM entity_assets ea
                    WHERE ea.asset_type = 'Logo'
                      AND (@hasBatchScope = 0 OR ea.entity_id IN (SELECT id FROM entity_scope))
                ) AS LogoCount,
                (
                    SELECT COUNT(*)
                    FROM entity_assets ea
                    WHERE ea.asset_type IN ('SeasonPoster', 'SeasonThumb', 'EpisodeStill')
                      AND (@hasBatchScope = 0 OR ea.entity_id IN (SELECT id FROM entity_scope))
                ) AS SeasonEpisodeArtworkCount,
                (
                    SELECT COUNT(*)
                    FROM entity_assets ea
                    WHERE ea.asset_type = 'CoverArt'
                      AND (@hasBatchScope = 0 OR ea.entity_id IN (SELECT id FROM entity_scope))
                ) AS MusicArtworkCount;
            """, new { batchIds = scopedBatchIds, hasBatchScope }).ConfigureAwait(false);

        return counts ?? new NumberedStageArtifactCounts();
    }

    private static IngestionStageProgressDto CreateNumberedStage(
        int stageNumber,
        string stageKey,
        string label,
        int completedFiles,
        int totalFiles,
        string artifactLabel,
        int artifactCount,
        IngestionCurrentActivityDto? activity,
        List<IngestionStageDetailItemDto>? detailItems = null,
        bool terminalBatch = false)
    {
        completedFiles = Math.Max(0, completedFiles);
        totalFiles = Math.Max(0, totalFiles);
        if (totalFiles > 0)
        {
            completedFiles = Math.Clamp(completedFiles, 0, totalFiles);
        }

        var percent = totalFiles > 0
            ? Math.Clamp(completedFiles * 100d / totalFiles, 0, 100)
            : 0;
        var active = Math.Max(0, activity?.ActiveCount ?? 0);
        var queued = Math.Max(0, activity?.QueuedCount ?? 0);
        var lastUpdated = activity?.LastUpdatedTime;
        var isStale = active > 0
            && lastUpdated.HasValue
            && DateTimeOffset.UtcNow - lastUpdated.Value.ToUniversalTime() > ActiveBatchFreshness;
        var activeItem = ResolveActiveItemLabel(activity, active);
        var activeGroup = ResolveActiveGroupLabel(stageKey, activity, active, queued);
        var activeGroupCount = activeGroup is null ? 0 : Math.Max(active + queued, activity?.CurrentBatch?.BatchSize ?? 0);
        var labelAccuracy = isStale
            ? "Stale"
            : activeGroup is not null
                ? "GroupedLookup"
                : !string.IsNullOrWhiteSpace(activeItem)
                    ? "ExactItem"
                    : active > 0
                        ? "BatchOnly"
                        : "None";

        return new IngestionStageProgressDto
        {
            StageNumber = stageNumber,
            StageKey = stageKey,
            Label = label,
            CompletedFiles = completedFiles,
            TotalFiles = totalFiles,
            PercentComplete = percent,
            ActiveCount = active,
            QueuedCount = queued,
            StatusLabel = ResolveNumberedStageStatus(percent, active, queued, artifactCount, stageKey, terminalBatch),
            ActiveItemLabel = string.IsNullOrWhiteSpace(activeItem) ? null : activeItem,
            ActiveGroupLabel = activeGroup,
            ActiveGroupCount = activeGroupCount,
            LabelAccuracy = labelAccuracy,
            ArtifactLabel = artifactLabel,
            ArtifactCount = Math.Max(0, artifactCount),
            LastUpdatedTime = lastUpdated,
            IsStale = isStale,
            DetailItems = detailItems ?? [],
        };
    }

    private static bool IsTerminalBatchForStageProgress(IngestionBatch batch) =>
        batch.CompletedAt.HasValue
        || string.Equals(batch.Status, "completed", StringComparison.OrdinalIgnoreCase)
        || FailedBatchStatuses.Contains(batch.Status, StringComparer.OrdinalIgnoreCase)
        || InterruptedBatchStatuses.Contains(batch.Status, StringComparer.OrdinalIgnoreCase);

    private static List<IngestionStageDetailItemDto> DetailItems(params IngestionStageDetailItemDto[] items) =>
        items
            .Where(item => !string.IsNullOrWhiteSpace(item.Label) && !string.IsNullOrWhiteSpace(item.Value))
            .OrderBy(item => DetailTerminalSort(item.Label))
            .ToList();

    private static int DetailTerminalSort(string label)
    {
        if (label.Contains("failed", StringComparison.OrdinalIgnoreCase))
        {
            return 2;
        }

        if (label.Contains("unresolved", StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        return 0;
    }

    private static IngestionStageDetailItemDto Detail(
        string label,
        int value,
        string tone = "neutral",
        string? icon = null) =>
        new()
        {
            Label = label,
            Value = FormatDetailCount(value),
            Tone = tone,
            Icon = icon,
        };

    private static string FormatDetailCount(int count) =>
        Math.Max(0, count).ToString("N0", CultureInfo.InvariantCulture);

    private static void ApplyBatchCategoryFallbacks(
        IReadOnlyList<IngestionBatch> displayBatches,
        NumberedStageArtifactCounts counts)
    {
        if (displayBatches.Count == 0 || counts.HasMediaBreakdown)
        {
            return;
        }

        foreach (var batch in displayBatches)
        {
            if (string.IsNullOrWhiteSpace(batch.Category) || batch.FilesTotal <= 0)
            {
                continue;
            }

            var category = NormalizeCategory(batch.Category);
            switch (category)
            {
                case "Movies":
                    counts.MoviesCount += batch.FilesTotal;
                    break;
                case "TV Shows":
                    counts.TvShowsCount += batch.FilesTotal;
                    break;
                case "Books":
                    counts.BooksCount += batch.FilesTotal;
                    break;
                case "Audiobooks":
                    counts.AudiobooksCount += batch.FilesTotal;
                    break;
                case "Music":
                    counts.MusicCount += batch.FilesTotal;
                    break;
                case "Comics":
                    counts.ComicsCount += batch.FilesTotal;
                    break;
            }
        }
    }

    private static IngestionCurrentActivityDto? ActivityFor(
        IReadOnlyList<IngestionCurrentActivityDto> activities,
        params string[] keys)
    {
        foreach (var key in keys)
        {
            var match = activities.FirstOrDefault(activity =>
                activity.StageKey.Equals(key, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return match;
            }
        }

        return null;
    }

    private static string? ResolveActiveItemLabel(IngestionCurrentActivityDto? activity, int activeCount)
    {
        if (activity is null || activeCount != 1)
        {
            return null;
        }

        return StringHelpers.FirstNonBlankOr(string.Empty,
            activity.CurrentItem,
            activity.CurrentBatch?.ActiveItems.FirstOrDefault(),
            activity.SampleItems.FirstOrDefault());
    }

    private static string? ResolveActiveGroupLabel(
        string stageKey,
        IngestionCurrentActivityDto? activity,
        int activeCount,
        int queuedCount)
    {
        if (activity is null)
        {
            return null;
        }

        if (activeCount + queuedCount <= 0)
        {
            return null;
        }

        var groupCount = Math.Max(activeCount + queuedCount, activity.CurrentBatch?.BatchSize ?? 0);
        if (groupCount <= 1)
        {
            return null;
        }

        return stageKey switch
        {
            "wikidata" => $"Resolving Wikidata batch: {groupCount:N0} files",
            "retail" => $"Matching retail batch: {groupCount:N0} files",
            "deep_artwork" => $"Fetching deep artwork for {groupCount:N0} files",
            "relationships" => $"Building relationships for {groupCount:N0} files",
            "people" => $"Resolving people for {groupCount:N0} files",
            _ => $"Working on {groupCount:N0} files",
        };
    }

    private static string ResolveNumberedStageStatus(
        double percent,
        int active,
        int queued,
        int artifactCount,
        string stageKey,
        bool terminalBatch = false)
    {
        if (stageKey.Equals("review", StringComparison.OrdinalIgnoreCase) && artifactCount > 0)
        {
            return "Needs review";
        }

        if (active > 0)
        {
            return "Active";
        }

        if (queued > 0)
        {
            return "Queued";
        }

        if (terminalBatch)
        {
            return "Complete";
        }

        if (percent >= 100)
        {
            return "Complete";
        }

        if (percent > 0)
        {
            return "In progress";
        }

        return "Pending";
    }

    private static int ParseMetricValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return 0;
        }

        var digits = new string(value.Where(char.IsDigit).ToArray());
        return int.TryParse(digits, out var parsed) ? parsed : 0;
    }

    private async Task<List<IngestionCurrentActivityDto>> ReadCurrentActivitiesAsync(
        Guid batchId,
        IReadOnlyList<IngestionPipelineStageDto> stages,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = _db.CreateConnection();
        var rows = await conn.QueryAsync<CurrentActivityRow>("""
            WITH latest_jobs AS (
                SELECT
                    entity_id,
                    state,
                    media_type,
                    lease_owner,
                    lease_expires_at,
                    updated_at,
                    ROW_NUMBER() OVER (
                        PARTITION BY entity_id
                        ORDER BY updated_at DESC, created_at DESC
                    ) AS rn
                FROM identity_jobs
                WHERE ingestion_run_id = @batchId
            )
            SELECT
                '' AS EntityId,
                lj.state AS State,
                lj.media_type AS MediaType,
                lj.lease_owner AS LeaseOwner,
                lj.lease_expires_at AS LeaseExpiresAt,
                lj.updated_at AS UpdatedAt,
                ma.file_path_root AS SourcePath,
                COALESCE(
                    (
                        SELECT cv.value
                        FROM canonical_values cv
                        WHERE cv.entity_id = lj.entity_id
                          AND cv.key IN ('title', 'episode_title')
                          AND cv.value IS NOT NULL
                          AND cv.value <> ''
                        ORDER BY CASE cv.key WHEN 'title' THEN 0 ELSE 1 END
                        LIMIT 1
                    ),
                    (
                        SELECT cv.value
                        FROM canonical_values cv
                        WHERE cv.entity_id = w.id
                          AND cv.key = 'title'
                          AND cv.value IS NOT NULL
                          AND cv.value <> ''
                        LIMIT 1
                    ),
                    ''
                ) AS Title
            FROM latest_jobs lj
            LEFT JOIN media_assets ma ON ma.id = lj.entity_id
            LEFT JOIN editions e ON e.id = ma.edition_id
            LEFT JOIN works w ON w.id = e.work_id
            WHERE lj.rn = 1
              AND lj.state IN @states
            ORDER BY
                CASE lj.state
                    WHEN 'RetailSearching' THEN 10
                    WHEN 'BridgeSearching' THEN 20
                    WHEN 'Hydrating' THEN 30
                    WHEN 'UniverseEnriching' THEN 40
                    ELSE 90
                END,
                lj.updated_at DESC
            LIMIT @limit;
            """, new { batchId, states = CurrentActivityStates, limit = 10 });

        return rows
            .Select(row => ToCurrentActivity(row, stages))
            .ToList();
    }

    private async Task<List<IngestionCurrentActivityDto>> ReadTaskActivitiesAsync(
        IReadOnlyCollection<Guid> batchIds,
        IReadOnlyList<IngestionPipelineStageDto> stages,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var hasBatchScope = batchIds.Count > 0 ? 1 : 0;
        var identityBatchIdValues = ToBatchIdBlobs(batchIds);
        using var conn = _db.CreateConnection();
        var rows = (await conn.QueryAsync<CurrentActivityRow>("""
            WITH latest_jobs AS (
                SELECT
                    entity_id,
                    state,
                    media_type,
                    lease_owner,
                    lease_expires_at,
                    updated_at,
                    ROW_NUMBER() OVER (
                        PARTITION BY entity_id
                        ORDER BY updated_at DESC, created_at DESC
                    ) AS rn
                FROM identity_jobs
                WHERE (@hasBatchScope = 0 OR ingestion_run_id IN @batchIds)
            )
            SELECT
                '' AS EntityId,
                lj.state AS State,
                lj.media_type AS MediaType,
                lj.lease_owner AS LeaseOwner,
                lj.lease_expires_at AS LeaseExpiresAt,
                lj.updated_at AS UpdatedAt,
                ma.file_path_root AS SourcePath,
                COALESCE(
                    (
                        SELECT cv.value
                        FROM canonical_values cv
                        WHERE cv.entity_id = lj.entity_id
                          AND cv.key IN ('title', 'episode_title')
                          AND cv.value IS NOT NULL
                          AND cv.value <> ''
                        ORDER BY CASE cv.key WHEN 'title' THEN 0 ELSE 1 END
                        LIMIT 1
                    ),
                    (
                        SELECT cv.value
                        FROM canonical_values cv
                        WHERE cv.entity_id = w.id
                          AND cv.key = 'title'
                          AND cv.value IS NOT NULL
                          AND cv.value <> ''
                        LIMIT 1
                    ),
                    ''
                ) AS Title
            FROM latest_jobs lj
            LEFT JOIN media_assets ma ON ma.id = lj.entity_id
            LEFT JOIN editions e ON e.id = ma.edition_id
            LEFT JOIN works w ON w.id = e.work_id
            WHERE lj.rn = 1
            ORDER BY lj.updated_at DESC;
            """, new { batchIds = identityBatchIdValues, hasBatchScope })).AsList();

        var metrics = await conn.QueryFirstOrDefaultAsync<ActivityMetricCounts>("""
            SELECT
                (SELECT COUNT(*) FROM entity_assets WHERE COALESCE(asset_class, 'Artwork') = 'Artwork') AS ArtworkCount,
                (
                    (SELECT COUNT(*) FROM collection_relationships)
                  + (SELECT COUNT(*) FROM entity_relationships)
                  + (SELECT COUNT(*) FROM fictional_entity_work_links)
                  + (SELECT COUNT(*) FROM character_performer_links)
                  + (SELECT COUNT(*) FROM series_members)
                  + (SELECT COUNT(*) FROM series_manifest_items)
                ) AS RelationshipCount,
                (SELECT COUNT(*) FROM persons) AS PersonCount,
                (SELECT COUNT(*) FROM review_queue WHERE status = 'Pending' AND review_ready_at IS NOT NULL) AS IssueCount;
            """) ?? new ActivityMetricCounts();
        var operationProgress = await ReadOperationProgressAsync(conn, identityBatchIdValues, hasBatchScope);
        var artworkOperation = operationProgress.GetValueOrDefault("artwork");
        var wikidataOperation = operationProgress.GetValueOrDefault("wikidata");
        var relationshipsOperation = operationProgress.GetValueOrDefault("relationships");
        var peopleOperation = operationProgress.GetValueOrDefault("people");
        var artworkRows = await ReadArtworkWorkerRowsAsync(conn, identityBatchIdValues, hasBatchScope);
        var seriesRows = await ReadSeriesWorkerRowsAsync(conn, identityBatchIdValues, hasBatchScope);
        var peopleRows = await ReadPeopleWorkerRowsAsync(conn, identityBatchIdValues, hasBatchScope);
        var artworkDisplayRows = MergeActivityRows(artworkRows, artworkOperation?.Rows);
        var wikidataDisplayRows = wikidataOperation?.Rows ?? [];
        var seriesDisplayRows = MergeActivityRows(seriesRows, relationshipsOperation?.Rows);
        var peopleDisplayRows = MergeActivityRows(peopleRows, peopleOperation?.Rows);
        var artworkProgress = await ReadArtworkProgressAsync(conn, identityBatchIdValues, hasBatchScope, artworkOperation);
        var linkedQids = await ReadDistinctWikidataQidCountAsync(conn, identityBatchIdValues, hasBatchScope);
        var wikidataProgress = BuildOpenEndedOperationProgressOverride(wikidataOperation, linkedQids, "QIDs");
        var relationshipsProgress = await ReadRelationshipsProgressAsync(conn, identityBatchIdValues, hasBatchScope, relationshipsOperation);
        var peopleProgress = await ReadPeopleProgressAsync(conn, identityBatchIdValues, hasBatchScope, peopleOperation);

        var result = new List<IngestionCurrentActivityDto>
        {
            BuildTaskActivity(
                "artwork",
                "Fetching artwork",
                "Retrieving covers and posters from providers.",
                "retail",
                rows,
                stages,
                activeStates: [
                    nameof(IdentityJobState.RetailSearching),
                    nameof(IdentityJobState.Hydrating),
                ],
                relevantStates: [
                    nameof(IdentityJobState.Queued),
                    nameof(IdentityJobState.RetailSearching),
                    ..RetailMatchedStates,
                    ..RetailReviewStates,
                    ..RetailNoMatchStates,
                    nameof(IdentityJobState.Failed),
                ],
                completedStates: [..RetailMatchedStates, ..RetailReviewStates, ..RetailNoMatchStates],
                reviewStates: [..RetailReviewStates, ..RetailNoMatchStates, nameof(IdentityJobState.Failed)],
                metricLabel: "Artwork assets",
                metricValue: metrics.ArtworkCount.ToString("N0"),
                metricTone: "info",
                countUnit: "artwork assets",
                displayRows: artworkDisplayRows,
                progressOverride: artworkProgress),
            BuildTaskActivity(
                "wikidata",
                "Linking Wikidata QIDs",
                "Matching works to canonical entities.",
                "wikidata",
                rows,
                stages,
                activeStates: [nameof(IdentityJobState.BridgeSearching)],
                relevantStates: [
                    nameof(IdentityJobState.RetailMatched),
                    nameof(IdentityJobState.BridgeSearching),
                    ..WikidataResolvedStates,
                    ..WikidataReviewStates,
                ],
                completedStates: WikidataResolvedStates,
                reviewStates: WikidataReviewStates,
                metricLabel: "Entity QIDs resolved",
                metricValue: linkedQids.ToString("N0"),
                metricTone: "success",
                countUnit: "QIDs",
                displayRows: wikidataDisplayRows,
                progressOverride: wikidataProgress),
            BuildTaskActivity(
                "relationships",
                "Relationships",
                "Building series and relationship graphs.",
                "enrichment",
                rows,
                stages,
                activeStates: [nameof(IdentityJobState.UniverseEnriching)],
                relevantStates: [
                    nameof(IdentityJobState.QidResolved),
                    nameof(IdentityJobState.Hydrating),
                    nameof(IdentityJobState.UniverseEnriching),
                    ..EnrichmentCompleteStates,
                ],
                completedStates: EnrichmentCompleteStates,
                reviewStates: [],
                metricLabel: "Links created",
                metricValue: metrics.RelationshipCount.ToString("N0"),
                metricTone: "success",
                countUnit: "items",
                displayRows: seriesDisplayRows,
                progressOverride: relationshipsProgress),
            BuildTaskActivity(
                "people",
                "People & cast enrichment",
                "Enriching authors, narrators, cast, and characters.",
                "enrichment",
                rows,
                stages,
                activeStates: [
                    nameof(IdentityJobState.Hydrating),
                    nameof(IdentityJobState.UniverseEnriching),
                ],
                relevantStates: [
                    nameof(IdentityJobState.QidResolved),
                    nameof(IdentityJobState.Hydrating),
                    nameof(IdentityJobState.UniverseEnriching),
                    ..EnrichmentCompleteStates,
                ],
                completedStates: EnrichmentCompleteStates,
                reviewStates: [],
                metricLabel: "People resolved",
                metricValue: metrics.PersonCount.ToString("N0"),
                metricTone: "warning",
                countUnit: "people",
                displayRows: peopleDisplayRows,
                progressOverride: peopleProgress),
        };

        return result
            .Where(activity => activity.TotalCount > 0 || activity.ActiveCount > 0 || activity.ProcessedCount > 0)
            .ToList();
    }

    private static async Task<int> ReadDistinctWikidataQidCountAsync(
        SqliteConnection conn,
        IReadOnlyCollection<byte[]> identityBatchIds,
        int hasBatchScope)
    {
        return await conn.ExecuteScalarAsync<int>("""
            WITH batch_assets AS (
                SELECT DISTINCT entity_id AS asset_id
                FROM identity_jobs
                WHERE (@hasBatchScope = 0 OR ingestion_run_id IN @batchIds)
                  AND entity_id IS NOT NULL
            ),
            batch_works AS (
                SELECT DISTINCT e.work_id
                FROM batch_assets ba
                JOIN media_assets ma ON ma.id = ba.asset_id
                JOIN editions e ON e.id = ma.edition_id
                WHERE e.work_id IS NOT NULL
            ),
            batch_collections AS (
                SELECT DISTINCT w.collection_id
                FROM batch_works bw
                JOIN works w ON w.id = bw.work_id
                WHERE w.collection_id IS NOT NULL
            ),
            batch_work_qids AS (
                SELECT DISTINCT w.wikidata_qid AS qid
                FROM batch_works bw
                JOIN works w ON w.id = bw.work_id
                WHERE w.wikidata_qid IS NOT NULL AND w.wikidata_qid <> ''
                UNION
                SELECT DISTINCT cv.value AS qid
                FROM batch_works bw
                JOIN canonical_values cv ON cv.entity_id = bw.work_id
                WHERE cv.key = 'wikidata_qid'
                  AND cv.value IS NOT NULL
                  AND cv.value <> ''
            ),
            batch_targets AS (
                SELECT asset_id AS entity_id FROM batch_assets
                UNION
                SELECT work_id AS entity_id FROM batch_works
                UNION
                SELECT collection_id AS entity_id FROM batch_collections
            ),
            entity_qids AS (
                SELECT resolved_qid AS qid
                FROM identity_jobs
                WHERE (@hasBatchScope = 0 OR ingestion_run_id IN @batchIds)
                  AND resolved_qid IS NOT NULL
                  AND resolved_qid <> ''
                  AND resolved_qid <> 'NF'
                UNION
                SELECT w.wikidata_qid AS qid
                FROM works w
                WHERE w.wikidata_qid IS NOT NULL
                  AND w.wikidata_qid <> ''
                  AND (@hasBatchScope = 0 OR w.id IN (SELECT work_id FROM batch_works))
                UNION
                SELECT e.wikidata_qid AS qid
                FROM editions e
                JOIN media_assets ma ON ma.edition_id = e.id
                WHERE e.wikidata_qid IS NOT NULL
                  AND e.wikidata_qid <> ''
                  AND (@hasBatchScope = 0 OR ma.id IN (SELECT asset_id FROM batch_assets))
                UNION
                SELECT c.wikidata_qid AS qid
                FROM collections c
                WHERE c.wikidata_qid IS NOT NULL
                  AND c.wikidata_qid <> ''
                  AND (@hasBatchScope = 0 OR c.id IN (SELECT collection_id FROM batch_collections))
                UNION
                SELECT cv.value AS qid
                FROM canonical_values cv
                WHERE cv.key = 'wikidata_qid'
                  AND cv.value IS NOT NULL
                  AND cv.value <> ''
                  AND (@hasBatchScope = 0 OR cv.entity_id IN (SELECT entity_id FROM batch_targets))
                UNION
                SELECT p.wikidata_qid AS qid
                FROM persons p
                WHERE p.wikidata_qid IS NOT NULL
                  AND p.wikidata_qid <> ''
                  AND (
                        @hasBatchScope = 0
                        OR EXISTS (
                            SELECT 1
                            FROM person_media_links pml
                            JOIN batch_assets ba ON ba.asset_id = pml.media_asset_id
                            WHERE pml.person_id = p.id
                        )
                  )
                UNION
                SELECT fe.wikidata_qid AS qid
                FROM fictional_entities fe
                WHERE fe.wikidata_qid IS NOT NULL
                  AND fe.wikidata_qid <> ''
                  AND (
                        @hasBatchScope = 0
                        OR EXISTS (
                            SELECT 1
                            FROM fictional_entity_work_links fewl
                            JOIN batch_work_qids bwq ON bwq.qid = fewl.work_qid
                            WHERE fewl.entity_id = fe.id
                        )
                  )
                UNION
                SELECT smi.item_qid AS qid
                FROM series_manifest_items smi
                WHERE smi.item_qid IS NOT NULL
                  AND smi.item_qid <> ''
                  AND (
                        @hasBatchScope = 0
                        OR smi.collection_id IN (SELECT collection_id FROM batch_collections)
                        OR smi.linked_work_id IN (SELECT work_id FROM batch_works)
                  )
            )
            SELECT COUNT(DISTINCT qid)
            FROM entity_qids
            WHERE qid LIKE 'Q%';
            """, new { batchIds = identityBatchIds, hasBatchScope });
    }

    private static async Task<IReadOnlyDictionary<string, TaskOperationProgress>> ReadOperationProgressAsync(
        SqliteConnection conn,
        IReadOnlyCollection<byte[]> batchIds,
        int hasBatchScope)
    {
        var rows = (await conn.QueryAsync<TaskOperationRow>("""
            SELECT
                mo.operation_type AS OperationType,
                mo.status AS Status,
                mo.stage AS Stage,
                mo.provider_id AS ProviderId,
                mo.source_path AS SourcePath,
                mo.result_summary AS ResultSummary,
                mo.lease_owner AS LeaseOwner,
                mo.lease_expires_at AS LeaseExpiresAt,
                mo.updated_at AS UpdatedAt,
                COALESCE(
                    (
                        SELECT cv.value
                        FROM canonical_values cv
                        WHERE cv.entity_id = mo.entity_id
                          AND cv.key IN ('title', 'show_name', 'episode_title')
                          AND cv.value IS NOT NULL
                          AND cv.value <> ''
                        ORDER BY CASE cv.key WHEN 'title' THEN 0 WHEN 'show_name' THEN 1 ELSE 2 END
                        LIMIT 1
                    ),
                    mo.result_summary,
                    mo.operation_type
                ) AS Title
            FROM media_operations mo
            WHERE mo.operation_type IN @operationTypes
              AND (@hasBatchScope = 0 OR mo.batch_id IN @batchIds)
            ORDER BY
                CASE
                    WHEN mo.status IN @activeStatuses THEN 0
                    WHEN mo.status IN @queuedStatuses THEN 1
                    ELSE 2
                END,
                mo.updated_at DESC;
            """, new
        {
            batchIds,
            hasBatchScope,
            operationTypes = VisibleOperationTypes,
            activeStatuses = ActiveOperationStatuses,
            queuedStatuses = QueuedOperationStatuses,
        })).AsList();

        return rows
            .GroupBy(row => ResolveOperationTaskKey(row.OperationType), StringComparer.OrdinalIgnoreCase)
            .Where(group => !string.IsNullOrWhiteSpace(group.Key))
            .ToDictionary(
                group => group.Key,
                group =>
                {
                    var operationRows = group.ToList();
                    var completed = operationRows.Count(row => IsTerminalOperationStatus(row.Status));
                    var active = operationRows.Count(row => IsActiveOperationStatus(row.Status));
                    var queued = operationRows.Count(row => IsQueuedOperationStatus(row.Status));
                    var displayRows = operationRows
                        .Take(12)
                        .Select(row => ToOperationActivityRow(group.Key, row))
                        .ToList();

                    return new TaskOperationProgress(
                        Processed: completed,
                        Total: operationRows.Count,
                        Active: active,
                        Queued: queued,
                        LastUpdated: LatestUpdated(displayRows),
                        Rows: displayRows);
                },
                StringComparer.OrdinalIgnoreCase);
    }

    private static async Task<TaskProgressOverride?> ReadArtworkProgressAsync(
        SqliteConnection conn,
        IReadOnlyCollection<byte[]> identityBatchIds,
        int hasBatchScope,
        TaskOperationProgress? operationProgress)
    {
        var counts = await conn.QueryFirstOrDefaultAsync<TaskProgressCountRow>("""
            WITH batch_targets AS (
                SELECT DISTINCT COALESCE(gp.id, p.id, w.id, ma.id) AS entity_id
                FROM identity_jobs ij
                LEFT JOIN media_assets ma ON ma.id = ij.entity_id
                LEFT JOIN editions e ON e.id = ma.edition_id
                LEFT JOIN works w ON w.id = e.work_id
                LEFT JOIN works p ON p.id = w.parent_work_id
                LEFT JOIN works gp ON gp.id = p.parent_work_id
                WHERE (@hasBatchScope = 0 OR ij.ingestion_run_id IN @batchIds)
                  AND COALESCE(gp.id, p.id, w.id, ma.id) IS NOT NULL
                UNION
                SELECT DISTINCT pml.person_id AS entity_id
                FROM identity_jobs ij
                JOIN person_media_links pml ON pml.media_asset_id = ij.entity_id
                WHERE (@hasBatchScope = 0 OR ij.ingestion_run_id IN @batchIds)
            )
            SELECT
                COUNT(*) AS Total,
                COALESCE(SUM(CASE
                    WHEN image_url IS NOT NULL
                      OR local_image_path IS NOT NULL
                      OR local_image_path_s IS NOT NULL
                      OR local_image_path_m IS NOT NULL
                      OR local_image_path_l IS NOT NULL
                    THEN 1 ELSE 0 END), 0) AS Processed,
                MAX(COALESCE(updated_at, created_at)) AS UpdatedAt
            FROM entity_assets
            WHERE COALESCE(asset_class, 'Artwork') = 'Artwork'
              AND (@hasBatchScope = 0 OR entity_id IN (SELECT entity_id FROM batch_targets));
            """, new { batchIds = identityBatchIds, hasBatchScope }) ?? new TaskProgressCountRow();

        var recent = await ReadRecentActivityProgressAsync(
            conn,
            identityBatchIds,
            hasBatchScope,
            [SystemActionType.CoverArtSaved, SystemActionType.HeroBannerGenerated]);
        var operation = BuildOperationProgressOverride(operationProgress);
        var hasArtifactCounts = counts.Total > 0 || counts.Processed > 0;
        var processed = hasArtifactCounts ? counts.Processed : operation?.Processed ?? 0;
        var active = Math.Max(operation?.Active ?? 0, recent.Active);
        var queued = operation?.Queued ?? 0;
        if (processed <= 0 && active <= 0 && queued <= 0)
        {
            return null;
        }

        return new TaskProgressOverride(
            processed,
            0,
            active,
            queued,
            ParseDate(counts.UpdatedAt) ?? operation?.LastUpdated ?? recent.LastUpdated,
            PreferExact: true,
            CountUnit: "artwork assets");
    }

    private static TaskProgressOverride? BuildOperationProgressOverride(TaskOperationProgress? operationProgress) =>
        operationProgress is null
            ? null
            : new TaskProgressOverride(
                operationProgress.Processed,
                operationProgress.Total,
                operationProgress.Active,
                operationProgress.Queued,
                operationProgress.LastUpdated,
                PreferExact: true);

    private static TaskProgressOverride? BuildOpenEndedOperationProgressOverride(
        TaskOperationProgress? operationProgress,
        int processed,
        string countUnit)
    {
        var active = operationProgress?.Active ?? 0;
        var queued = operationProgress?.Queued ?? 0;
        if (processed <= 0 && active <= 0 && queued <= 0)
        {
            return null;
        }

        return new TaskProgressOverride(
            Math.Max(0, processed),
            0,
            active,
            queued,
            operationProgress?.LastUpdated,
            PreferExact: true,
            CountUnit: countUnit);
    }

    private static IReadOnlyList<CurrentActivityRow> MergeActivityRows(
        IReadOnlyList<CurrentActivityRow> primary,
        IReadOnlyList<CurrentActivityRow>? secondary)
    {
        if (secondary is not { Count: > 0 })
        {
            return primary;
        }

        if (primary.Count == 0)
        {
            return secondary;
        }

        return secondary.Concat(primary)
            .OrderBy(row => TaskSort(
                row,
                [
                    nameof(IdentityJobState.RetailSearching),
                    nameof(IdentityJobState.BridgeSearching),
                    nameof(IdentityJobState.Hydrating),
                    nameof(IdentityJobState.UniverseEnriching),
                ],
                EnrichmentCompleteStates,
                WikidataReviewStates))
            .ThenByDescending(row => ParseDate(row.UpdatedAt))
            .Take(20)
            .ToList();
    }

    private static async Task<TaskProgressOverride?> ReadPeopleProgressAsync(
        SqliteConnection conn,
        IReadOnlyCollection<byte[]> identityBatchIds,
        int hasBatchScope,
        TaskOperationProgress? operationProgress)
    {
        var counts = await conn.QueryFirstOrDefaultAsync<TaskProgressCountRow>("""
            WITH batch_assets AS (
                SELECT DISTINCT entity_id
                FROM identity_jobs
                WHERE (@hasBatchScope = 0 OR ingestion_run_id IN @batchIds)
                  AND entity_id IS NOT NULL
                  AND entity_id <> ''
            ),
            linked_people AS (
                SELECT DISTINCT
                    p.id,
                    p.enriched_at,
                    p.wikidata_qid,
                    p.biography,
                    p.headshot_url,
                    p.local_headshot_path,
                    p.created_at
                FROM batch_assets ba
                JOIN person_media_links pml ON pml.media_asset_id = ba.entity_id
                JOIN persons p ON p.id = pml.person_id
                UNION
                SELECT DISTINCT
                    p.id,
                    p.enriched_at,
                    p.wikidata_qid,
                    p.biography,
                    p.headshot_url,
                    p.local_headshot_path,
                    p.created_at
                FROM persons p
                WHERE @hasBatchScope = 0
            )
            SELECT
                COUNT(*) AS Total,
                COALESCE(SUM(CASE
                    WHEN enriched_at IS NOT NULL
                      OR wikidata_qid IS NOT NULL
                      OR biography IS NOT NULL
                      OR headshot_url IS NOT NULL
                      OR local_headshot_path IS NOT NULL
                    THEN 1 ELSE 0 END), 0) AS Processed,
                MAX(COALESCE(enriched_at, created_at)) AS UpdatedAt
            FROM linked_people;
            """, new { batchIds = identityBatchIds, hasBatchScope }) ?? new TaskProgressCountRow();

        var recent = await ReadRecentActivityProgressAsync(conn, identityBatchIds, hasBatchScope, PeopleActivityTypes);
        var operation = BuildOperationProgressOverride(operationProgress);
        var active = Math.Max(operation?.Active ?? 0, recent.Active);
        var queued = operation?.Queued ?? 0;
        var hasPeopleCounts = counts.Total > 0 || counts.Processed > 0;
        var processed = hasPeopleCounts ? counts.Total : operation?.Processed ?? 0;
        if (processed <= 0 && active <= 0 && queued <= 0)
        {
            return null;
        }

        return new TaskProgressOverride(
            processed,
            0,
            active,
            queued,
            ParseDate(counts.UpdatedAt) ?? operation?.LastUpdated ?? recent.LastUpdated,
            PreferExact: true,
            CountUnit: "people");
    }

    private static async Task<TaskProgressOverride?> ReadRelationshipsProgressAsync(
        SqliteConnection conn,
        IReadOnlyCollection<byte[]> identityBatchIds,
        int hasBatchScope,
        TaskOperationProgress? operationProgress)
    {
        var jobCounts = await conn.QueryFirstOrDefaultAsync<TaskProgressCountRow>("""
            WITH latest_jobs AS (
                SELECT
                    entity_id,
                    state,
                    lease_owner,
                    lease_expires_at,
                    updated_at,
                    ROW_NUMBER() OVER (
                        PARTITION BY entity_id
                        ORDER BY updated_at DESC, created_at DESC
                    ) AS rn
                FROM identity_jobs
                WHERE (@hasBatchScope = 0 OR ingestion_run_id IN @batchIds)
            ),
            job_states AS (
                SELECT entity_id, state, lease_owner, lease_expires_at, updated_at
                FROM latest_jobs
                WHERE rn = 1
                  AND state IN @relationshipStates
            ),
            stage3_flags AS (
                SELECT
                    entity_id,
                    MAX(CASE WHEN key IN ('stage3_enriched_at', 'stage3_enhanced_at') THEN 1 ELSE 0 END) AS Stage3Done
                FROM canonical_values
                WHERE key IN ('stage3_enriched_at', 'stage3_enhanced_at')
                GROUP BY entity_id
            )
            SELECT
                COUNT(*) AS Total,
                COALESCE(SUM(CASE
                    WHEN js.state IN @completeStates THEN 1
                    WHEN COALESCE(sf.Stage3Done, 0) = 1 THEN 1
                    ELSE 0 END), 0) AS Processed,
                COALESCE(SUM(CASE
                    WHEN js.state = @universeState
                     AND js.lease_owner IS NOT NULL
                     AND js.lease_expires_at IS NOT NULL
                     AND js.lease_expires_at > @now
                    THEN 1 ELSE 0 END), 0) AS Active,
                COALESCE(SUM(CASE
                    WHEN js.state IN @queuedStates
                     AND js.state NOT IN @completeStates
                     AND COALESCE(sf.Stage3Done, 0) = 0
                    THEN 1 ELSE 0 END), 0) AS Queued,
                MAX(js.updated_at) AS UpdatedAt
            FROM job_states js
            LEFT JOIN stage3_flags sf ON sf.entity_id = js.entity_id;
            """, new
        {
            batchIds = identityBatchIds,
            hasBatchScope,
            now = DateTimeOffset.UtcNow.ToString("O"),
            relationshipStates = new[]
                {
                    nameof(IdentityJobState.QidResolved),
                    nameof(IdentityJobState.Hydrating),
                    nameof(IdentityJobState.UniverseEnriching),
                    nameof(IdentityJobState.Ready),
                    nameof(IdentityJobState.ReadyWithoutUniverse),
                },
            completeStates = EnrichmentCompleteStates,
            queuedStates = new[]
                {
                    nameof(IdentityJobState.QidResolved),
                    nameof(IdentityJobState.Hydrating),
                    nameof(IdentityJobState.UniverseEnriching),
                },
            universeState = nameof(IdentityJobState.UniverseEnriching),
        }) ?? new TaskProgressCountRow();

        var artifactCounts = await conn.QueryFirstOrDefaultAsync<TaskProgressCountRow>("""
            WITH batch_assets AS (
                SELECT DISTINCT ij.entity_id
                FROM identity_jobs ij
                WHERE (@hasBatchScope = 0 OR ij.ingestion_run_id IN @batchIds)
                  AND ij.entity_id IS NOT NULL
                  AND ij.entity_id <> ''
            ),
            batch_works AS (
                SELECT DISTINCT
                    w.id AS work_id,
                    w.collection_id AS collection_id,
                    COALESCE(
                        (
                            SELECT cv.value
                            FROM canonical_values cv
                            WHERE cv.entity_id = w.id
                              AND cv.key = 'wikidata_qid'
                              AND cv.value IS NOT NULL
                              AND cv.value <> ''
                            LIMIT 1
                        ),
                        (
                            SELECT cv.value
                            FROM canonical_values cv
                            WHERE cv.entity_id = ma.id
                              AND cv.key = 'wikidata_qid'
                              AND cv.value IS NOT NULL
                              AND cv.value <> ''
                            LIMIT 1
                        )
                    ) AS qid
                FROM batch_assets ba
                JOIN media_assets ma ON ma.id = ba.entity_id
                JOIN editions e ON e.id = ma.edition_id
                JOIN works w ON w.id = e.work_id
            ),
            batch_collections AS (
                SELECT DISTINCT collection_id
                FROM batch_works
                WHERE collection_id IS NOT NULL
                  AND collection_id <> ''
            ),
            batch_qids AS (
                SELECT DISTINCT qid
                FROM batch_works
                WHERE qid IS NOT NULL
                  AND qid <> ''
            ),
            counts AS (
                SELECT COUNT(*) AS ItemCount, MAX(discovered_at) AS UpdatedAt
                FROM collection_relationships
                WHERE @hasBatchScope = 0
                   OR collection_id IN (SELECT collection_id FROM batch_collections)
                UNION ALL
                SELECT COUNT(*) AS ItemCount, MAX(discovered_at) AS UpdatedAt
                FROM entity_relationships
                WHERE @hasBatchScope = 0
                   OR subject_qid IN (SELECT qid FROM batch_qids)
                   OR object_qid IN (SELECT qid FROM batch_qids)
                   OR context_work_qid IN (SELECT qid FROM batch_qids)
                UNION ALL
                SELECT COUNT(*) AS ItemCount, NULL AS UpdatedAt
                FROM fictional_entity_work_links
                WHERE @hasBatchScope = 0
                   OR work_qid IN (SELECT qid FROM batch_qids)
                UNION ALL
                SELECT COUNT(*) AS ItemCount, NULL AS UpdatedAt
                FROM character_performer_links
                WHERE @hasBatchScope = 0
                   OR work_qid IN (SELECT qid FROM batch_qids)
                UNION ALL
                SELECT COUNT(*) AS ItemCount, MAX(created_at) AS UpdatedAt
                FROM series_members
                WHERE @hasBatchScope = 0
                   OR work_qid IN (SELECT qid FROM batch_qids)
                   OR series_qid IN (SELECT qid FROM batch_qids)
                UNION ALL
                SELECT COUNT(*) AS ItemCount, MAX(COALESCE(updated_at, last_hydrated_at, created_at)) AS UpdatedAt
                FROM series_manifest_items
                WHERE @hasBatchScope = 0
                   OR collection_id IN (SELECT collection_id FROM batch_collections)
                   OR linked_work_id IN (SELECT work_id FROM batch_works)
                   OR item_qid IN (SELECT qid FROM batch_qids)
                   OR series_qid IN (SELECT qid FROM batch_qids)
            )
            SELECT
                COALESCE(SUM(ItemCount), 0) AS Total,
                COALESCE(SUM(ItemCount), 0) AS Processed,
                MAX(UpdatedAt) AS UpdatedAt
            FROM counts;
            """, new { batchIds = identityBatchIds, hasBatchScope }) ?? new TaskProgressCountRow();

        var recent = await ReadRecentActivityProgressAsync(conn, identityBatchIds, hasBatchScope, RelationshipActivityTypes);
        var operation = BuildOperationProgressOverride(operationProgress);
        var active = Math.Max(Math.Max(jobCounts.Active, operation?.Active ?? 0), recent.Active);
        var queued = Math.Max(jobCounts.Queued, operation?.Queued ?? 0);
        var processed = artifactCounts.Processed > 0
            ? artifactCounts.Processed
            : operation?.Processed ?? 0;
        if (processed <= 0 && active <= 0 && queued <= 0)
        {
            return null;
        }

        return new TaskProgressOverride(
            processed,
            0,
            active,
            queued,
            ParseDate(artifactCounts.UpdatedAt) ?? ParseDate(jobCounts.UpdatedAt) ?? operation?.LastUpdated ?? recent.LastUpdated,
            PreferExact: true,
            CountUnit: "links");
    }

    private static async Task<RecentActivityProgress> ReadRecentActivityProgressAsync(
        SqliteConnection conn,
        IReadOnlyCollection<byte[]> batchIds,
        int hasBatchScope,
        IReadOnlyCollection<string> actionTypes)
    {
        var recentCutoff = DateTimeOffset.UtcNow.Subtract(ActiveActivityFreshness).ToString("O");
        var row = await conn.QueryFirstOrDefaultAsync<TaskProgressCountRow>("""
            SELECT
                COUNT(*) AS Active,
                MAX(occurred_at) AS UpdatedAt
            FROM system_activity
            WHERE action_type IN @actionTypes
              AND occurred_at >= @recentCutoff
              AND (@hasBatchScope = 0 OR ingestion_run_id IN @batchIds);
            """, new { batchIds, hasBatchScope, actionTypes, recentCutoff }) ?? new TaskProgressCountRow();

        return new RecentActivityProgress(row.Active > 0 ? 1 : 0, ParseDate(row.UpdatedAt));
    }

    private static CurrentActivityRow ToOperationActivityRow(string taskKey, TaskOperationRow row)
    {
        var completed = IsTerminalOperationStatus(row.Status);
        var active = IsActiveOperationStatus(row.Status);
        var noResult = string.Equals(row.Status, MediaOperationStatus.NoResult, StringComparison.OrdinalIgnoreCase)
            || string.Equals(row.Status, MediaOperationStatus.MissingConfirmed, StringComparison.OrdinalIgnoreCase)
            || string.Equals(row.Status, MediaOperationStatus.NotApplicable, StringComparison.OrdinalIgnoreCase);
        var state = taskKey switch
        {
            "wikidata" => active
                ? nameof(IdentityJobState.BridgeSearching)
                : noResult ? nameof(IdentityJobState.QidNoMatch)
                : completed ? nameof(IdentityJobState.QidResolved) : nameof(IdentityJobState.RetailMatched),
            "relationships" => active
                ? nameof(IdentityJobState.UniverseEnriching)
                : completed ? nameof(IdentityJobState.Ready) : nameof(IdentityJobState.QidResolved),
            "people" => active
                ? nameof(IdentityJobState.Hydrating)
                : completed ? nameof(IdentityJobState.Ready) : nameof(IdentityJobState.QidResolved),
            "artwork" => active
                ? nameof(IdentityJobState.Hydrating)
                : completed ? nameof(IdentityJobState.Ready) : nameof(IdentityJobState.RetailMatched),
            _ => row.Stage ?? row.Status,
        };

        return new CurrentActivityRow
        {
            EntityId = string.Empty,
            State = state,
            MediaType = taskKey,
            LeaseOwner = row.LeaseOwner,
            LeaseExpiresAt = row.LeaseExpiresAt,
            UpdatedAt = row.UpdatedAt,
            SourcePath = StringHelpers.FirstNonBlankOr(string.Empty, row.ProviderId, row.SourcePath),
            Title = StringHelpers.FirstNonBlankOr(string.Empty, row.Title, row.ResultSummary, row.OperationType),
        };
    }

    private static string ResolveOperationTaskKey(string? operationType) =>
        operationType switch
        {
            MediaOperationType.IdentityWikidataBridge => "wikidata",
            MediaOperationType.EnrichmentCoverArt => "artwork",
            MediaOperationType.EnrichmentPeople => "people",
            MediaOperationType.EnrichmentRelationships => "relationships",
            _ => string.Empty,
        };

    private static bool IsActiveOperationStatus(string? status) =>
        !string.IsNullOrWhiteSpace(status)
        && ActiveOperationStatuses.Contains(status, StringComparer.OrdinalIgnoreCase);

    private static bool IsQueuedOperationStatus(string? status) =>
        !string.IsNullOrWhiteSpace(status)
        && QueuedOperationStatuses.Contains(status, StringComparer.OrdinalIgnoreCase);

    private static bool IsTerminalOperationStatus(string? status) =>
        !string.IsNullOrWhiteSpace(status)
        && TerminalOperationStatuses.Contains(status, StringComparer.OrdinalIgnoreCase);

    private static async Task<IReadOnlyList<CurrentActivityRow>> ReadArtworkWorkerRowsAsync(
        SqliteConnection conn,
        IReadOnlyCollection<byte[]> batchIds,
        int hasBatchScope)
    {
        var rows = (await conn.QueryAsync<CurrentActivityRow>("""
            WITH latest_jobs AS (
                SELECT
                    entity_id,
                    state,
                    media_type,
                    lease_owner,
                    lease_expires_at,
                    updated_at,
                    ROW_NUMBER() OVER (
                        PARTITION BY entity_id
                        ORDER BY updated_at DESC, created_at DESC
                    ) AS rn
                FROM identity_jobs
                WHERE (@hasBatchScope = 0 OR ingestion_run_id IN @batchIds)
            )
            SELECT
                '' AS EntityId,
                lj.state AS State,
                lj.media_type AS MediaType,
                lj.lease_owner AS LeaseOwner,
                lj.lease_expires_at AS LeaseExpiresAt,
                lj.updated_at AS UpdatedAt,
                ma.file_path_root AS SourcePath,
                COALESCE(
                    (
                        SELECT cv.value
                        FROM canonical_values cv
                        WHERE cv.entity_id = lj.entity_id
                          AND cv.key IN ('title', 'episode_title')
                          AND cv.value IS NOT NULL
                          AND cv.value <> ''
                        ORDER BY CASE cv.key WHEN 'title' THEN 0 ELSE 1 END
                        LIMIT 1
                    ),
                    (
                        SELECT cv.value
                        FROM canonical_values cv
                        WHERE cv.entity_id = w.id
                          AND cv.key = 'title'
                          AND cv.value IS NOT NULL
                          AND cv.value <> ''
                        LIMIT 1
                    ),
                    ''
                ) AS Title,
                (
                    SELECT COUNT(*)
                    FROM entity_assets ea
                    WHERE ea.entity_type = 'Work'
                      AND ea.entity_id = COALESCE(gp.id, p.id, w.id)
                      AND COALESCE(ea.asset_class, 'Artwork') = 'Artwork'
                ) AS ArtworkAssetCount
            FROM latest_jobs lj
            LEFT JOIN media_assets ma ON ma.id = lj.entity_id
            LEFT JOIN editions e ON e.id = ma.edition_id
            LEFT JOIN works w ON w.id = e.work_id
            LEFT JOIN works p ON p.id = w.parent_work_id
            LEFT JOIN works gp ON gp.id = p.parent_work_id
            WHERE lj.rn = 1
            ORDER BY lj.updated_at DESC;
            """, new { batchIds, hasBatchScope })).AsList();

        return rows;
    }

    private static async Task<IReadOnlyList<CurrentActivityRow>> ReadSeriesWorkerRowsAsync(
        SqliteConnection conn,
        IReadOnlyCollection<byte[]> identityBatchIds,
        int hasBatchScope)
    {
        var batchRows = (await conn.QueryAsync<WorkerItemRow>("""
            WITH batch_assets AS (
                SELECT DISTINCT entity_id
                FROM identity_jobs
                WHERE (@hasBatchScope = 0 OR ingestion_run_id IN @batchIds)
                  AND entity_id IS NOT NULL
                  AND entity_id <> ''
            ),
            batch_collections AS (
                SELECT DISTINCT
                    COALESCE(c.display_name, c.wikidata_qid) AS Title,
                    NULL AS Detail,
                    COALESCE(c.created_at, datetime('now')) AS UpdatedAt
                FROM batch_assets ba
                JOIN media_assets ma ON ma.id = ba.entity_id
                JOIN editions e ON e.id = ma.edition_id
                JOIN works w ON w.id = e.work_id
                JOIN collections c ON c.id = w.collection_id
                WHERE COALESCE(c.display_name, c.wikidata_qid) IS NOT NULL
                  AND COALESCE(c.display_name, c.wikidata_qid) <> ''
            ),
            activity_collections AS (
                SELECT DISTINCT
                    collection_name AS Title,
                    detail AS Detail,
                    occurred_at AS UpdatedAt
                FROM system_activity
                WHERE (@hasBatchScope = 0 OR ingestion_run_id IN @activityBatchIds)
                  AND action_type IN @actionTypes
            )
            SELECT Title, Detail, MAX(UpdatedAt) AS UpdatedAt
            FROM (
                SELECT * FROM batch_collections
                UNION ALL
                SELECT * FROM activity_collections
            )
            WHERE COALESCE(Title, Detail) IS NOT NULL
              AND COALESCE(Title, Detail) <> ''
            GROUP BY COALESCE(Title, Detail)
            ORDER BY MAX(UpdatedAt) DESC
            LIMIT 10;
            """, new
        {
            batchIds = identityBatchIds,
            activityBatchIds = identityBatchIds,
            hasBatchScope,
            actionTypes = RelationshipActivityTypes
        })).AsList();

        if (batchRows.Count > 0)
        {
            return ToCompletedWorkerRows(batchRows, "Series");
        }

        var fallbackRows = (await conn.QueryAsync<WorkerItemRow>("""
            SELECT
                COALESCE(smh.series_label, c.display_name, smh.series_qid) AS Title,
                NULL AS Detail,
                COALESCE(smh.last_hydrated_at, smh.updated_at, smh.created_at) AS UpdatedAt
            FROM series_manifest_hydrations smh
            LEFT JOIN collections c ON c.id = smh.collection_id
            WHERE COALESCE(smh.series_label, c.display_name, smh.series_qid) IS NOT NULL
              AND COALESCE(smh.series_label, c.display_name, smh.series_qid) <> ''
            ORDER BY COALESCE(smh.last_hydrated_at, smh.updated_at, smh.created_at) DESC
            LIMIT 10;
            """)).AsList();

        return ToCompletedWorkerRows(fallbackRows, "Series");
    }

    private static async Task<IReadOnlyList<CurrentActivityRow>> ReadPeopleWorkerRowsAsync(
        SqliteConnection conn,
        IReadOnlyCollection<byte[]> identityBatchIds,
        int hasBatchScope)
    {
        var batchRows = (await conn.QueryAsync<WorkerItemRow>("""
            WITH batch_assets AS (
                SELECT DISTINCT entity_id
                FROM identity_jobs
                WHERE (@hasBatchScope = 0 OR ingestion_run_id IN @batchIds)
                  AND entity_id IS NOT NULL
                  AND entity_id <> ''
            ),
            batch_people AS (
                SELECT DISTINCT
                    p.name AS Title,
                    NULL AS Detail,
                    COALESCE(p.enriched_at, p.created_at) AS UpdatedAt
                FROM batch_assets ba
                JOIN person_media_links pml ON pml.media_asset_id = ba.entity_id
                JOIN persons p ON p.id = pml.person_id
                WHERE p.name IS NOT NULL
                  AND p.name <> ''
            ),
            activity_people AS (
                SELECT DISTINCT
                    collection_name AS Title,
                    detail AS Detail,
                    occurred_at AS UpdatedAt
                FROM system_activity
                WHERE (@hasBatchScope = 0 OR ingestion_run_id IN @activityBatchIds)
                  AND action_type IN @actionTypes
            )
            SELECT Title, Detail, MAX(UpdatedAt) AS UpdatedAt
            FROM (
                SELECT * FROM batch_people
                UNION ALL
                SELECT * FROM activity_people
            )
            WHERE COALESCE(Title, Detail) IS NOT NULL
              AND COALESCE(Title, Detail) <> ''
            GROUP BY COALESCE(Title, Detail)
            ORDER BY MAX(UpdatedAt) DESC
            LIMIT 10;
            """, new
        {
            batchIds = identityBatchIds,
            activityBatchIds = identityBatchIds,
            hasBatchScope,
            actionTypes = PeopleActivityTypes
        })).AsList();

        if (batchRows.Count > 0)
        {
            return ToCompletedWorkerRows(batchRows, "Person");
        }

        var fallbackRows = (await conn.QueryAsync<WorkerItemRow>("""
            SELECT
                name AS Title,
                NULL AS Detail,
                COALESCE(enriched_at, created_at) AS UpdatedAt
            FROM persons
            WHERE name IS NOT NULL
              AND name <> ''
            ORDER BY COALESCE(enriched_at, created_at) DESC
            LIMIT 10;
            """)).AsList();

        return ToCompletedWorkerRows(fallbackRows, "Person");
    }

    private static IReadOnlyList<CurrentActivityRow> ToCompletedWorkerRows(
        IEnumerable<WorkerItemRow> rows,
        string mediaType)
    {
        var result = new List<CurrentActivityRow>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            var title = CleanWorkerItemTitle(row.Title, row.Detail);
            if (string.IsNullOrWhiteSpace(title) || !seen.Add(title))
            {
                continue;
            }

            result.Add(new CurrentActivityRow
            {
                EntityId = title,
                State = nameof(IdentityJobState.Ready),
                MediaType = mediaType,
                UpdatedAt = row.UpdatedAt,
                Title = title,
            });
            if (result.Count >= 10)
            {
                break;
            }
        }

        return result;
    }

    private static string CleanWorkerItemTitle(string? title, string? detail)
    {
        var value = StringHelpers.FirstNonBlankOr(string.Empty, title, ExtractQuotedTitle(detail), detail);
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        value = value.Trim();
        return value.Length <= 100 ? value : value[..100].TrimEnd();
    }

    private static string? ExtractQuotedTitle(string? detail)
    {
        if (string.IsNullOrWhiteSpace(detail))
        {
            return null;
        }

        var firstQuote = detail.IndexOf('"');
        if (firstQuote < 0)
        {
            return null;
        }

        var secondQuote = detail.IndexOf('"', firstQuote + 1);
        if (secondQuote <= firstQuote + 1)
        {
            return null;
        }

        return detail[(firstQuote + 1)..secondQuote].Trim();
    }

    private static IngestionCurrentActivityDto BuildTaskActivity(
        string taskKey,
        string message,
        string detail,
        string progressStageKey,
        IReadOnlyList<CurrentActivityRow> rows,
        IReadOnlyList<IngestionPipelineStageDto> stages,
        IReadOnlyCollection<string> activeStates,
        IReadOnlyCollection<string> relevantStates,
        IReadOnlyCollection<string> completedStates,
        IReadOnlyCollection<string> reviewStates,
        string metricLabel,
        string metricValue,
        string metricTone,
        string countUnit = "files",
        IReadOnlyList<CurrentActivityRow>? displayRows = null,
        TaskProgressOverride? progressOverride = null)
    {
        var relevant = rows
            .Where(row => ContainsState(relevantStates, row.State))
            .OrderBy(row => TaskSort(row, activeStates, completedStates, reviewStates))
            .ThenByDescending(row => ParseDate(row.UpdatedAt))
            .ToList();
        var active = relevant.Where(row => IsFreshActive(row, activeStates)).ToList();
        var completed = relevant.Where(row => ContainsState(completedStates, row.State)).ToList();
        var review = relevant.Where(row => ContainsState(reviewStates, row.State)).ToList();
        var displayRelevant = displayRows is { Count: > 0 }
            ? displayRows
                .Where(row => ContainsState(relevantStates, row.State))
                .OrderBy(row => TaskSort(row, activeStates, completedStates, reviewStates))
                .ThenByDescending(row => ParseDate(row.UpdatedAt))
                .ToList()
            : relevant;
        var displayActive = displayRelevant.Where(row => IsFreshActive(row, activeStates)).ToList();
        var progress = ResolveActivityProgress(progressStageKey, stages);
        var openEndedDiscovery = IsOpenEndedDiscoveryTask(taskKey, progressOverride?.CountUnit ?? countUnit);
        var total = openEndedDiscovery
            ? 0
            : progressOverride?.PreferExact == true
            ? Math.Max(0, progressOverride.Total)
            : Math.Max(Math.Max(progress.Total, relevant.Count), progressOverride?.Total ?? 0);
        var processed = openEndedDiscovery
            ? Math.Max(progressOverride?.Processed ?? 0, ParseMetricCount(metricValue))
            : progressOverride?.PreferExact == true
            ? Math.Max(0, progressOverride.Processed)
            : Math.Max(Math.Max(progress.Count, completed.Count + review.Count), progressOverride?.Processed ?? 0);
        processed = total > 0
            ? Math.Clamp(processed, 0, total)
            : Math.Max(0, processed);
        var rawActiveCount = Math.Max(active.Count, progressOverride?.Active ?? 0);
        var remainingAfterProcessed = total > 0 ? Math.Max(0, total - processed) : int.MaxValue;
        var activeCount = progressOverride?.PreferExact == true
            ? total > 0 ? Math.Min(rawActiveCount, remainingAfterProcessed) : rawActiveCount
            : rawActiveCount;
        var queued = progressOverride?.PreferExact == true
            ? total > 0
                ? Math.Min(Math.Max(0, progressOverride.Queued), Math.Max(0, remainingAfterProcessed - activeCount))
                : Math.Max(0, progressOverride.Queued)
            : Math.Max(0, total - processed - activeCount);
        var samples = active.Count > 0
            ? (displayActive.Count > 0 ? displayActive : displayRelevant.Take(10).ToList())
            : displayRelevant.Where(row => !ContainsState(completedStates, row.State)).Take(8).ToList();
        if (samples.Count == 0)
        {
            samples = displayRelevant.Take(8).ToList();
        }
        var currentItem = samples.Select(DisplayActivityItem).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

        return new IngestionCurrentActivityDto
        {
            StageKey = taskKey,
            Message = message,
            Detail = detail,
            CurrentItem = currentItem,
            Source = StringHelpers.FirstNonBlankOr(string.Empty, active.Select(row => ShortPath(row.SourcePath)).FirstOrDefault(), relevant.Select(row => ShortPath(row.SourcePath)).FirstOrDefault()),
            ProcessedCount = processed,
            TotalCount = total,
            CountUnit = progressOverride?.CountUnit ?? countUnit,
            PercentComplete = total > 0 ? Math.Clamp(processed * 100d / total, 0, 100) : 0,
            LastUpdatedTime = progressOverride?.LastUpdated ?? LatestUpdated(relevant),
            QueuedCount = queued,
            ActiveCount = activeCount,
            SampleItems = samples.Select(DisplayActivityItem).Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase).Take(10).ToList(),
            MetricLabel = metricLabel,
            MetricValue = metricValue,
            MetricTone = metricTone,
            CurrentBatch = BuildCurrentBatch(displayRelevant, activeStates, completedStates, reviewStates),
        };
    }

    private static IngestionActivityBatchDto? BuildCurrentBatch(
        IReadOnlyList<CurrentActivityRow> relevant,
        IReadOnlyCollection<string> activeStates,
        IReadOnlyCollection<string> completedStates,
        IReadOnlyCollection<string> reviewStates)
    {
        if (relevant.Count == 0)
        {
            return null;
        }

        var totalBatches = Math.Max(1, (int)Math.Ceiling(relevant.Count / (double)ActivityBatchSize));
        var firstActiveIndex = IndexOf(relevant, row => IsFreshActive(row, activeStates));
        var firstPendingIndex = IndexOf(relevant, row =>
            !IsFreshActive(row, activeStates)
            && !ContainsState(completedStates, row.State)
            && !ContainsState(reviewStates, row.State));
        var startIndex = Math.Max(0, firstActiveIndex >= 0 ? firstActiveIndex : firstPendingIndex >= 0 ? firstPendingIndex : 0);
        var batchNumber = Math.Min(totalBatches, startIndex / ActivityBatchSize + 1);
        var batchRows = relevant
            .Skip((batchNumber - 1) * ActivityBatchSize)
            .Take(ActivityBatchSize)
            .ToList();

        var active = batchRows.Where(row => IsFreshActive(row, activeStates)).ToList();
        var completed = batchRows.Where(row => ContainsState(completedStates, row.State)).ToList();
        var review = batchRows.Where(row => ContainsState(reviewStates, row.State)).ToList();
        var pending = batchRows
            .Where(row => !IsFreshActive(row, activeStates)
                && !ContainsState(completedStates, row.State)
                && !ContainsState(reviewStates, row.State))
            .ToList();

        return new IngestionActivityBatchDto
        {
            BatchNumber = batchNumber,
            BatchSize = batchRows.Count,
            TotalBatches = totalBatches,
            CompletedCount = completed.Count,
            ActiveCount = active.Count,
            PendingCount = pending.Count,
            ReviewCount = review.Count,
            ActiveItems = active.Select(DisplayActivityItem).Where(value => !string.IsNullOrWhiteSpace(value)).Take(10).ToList(),
            CompletedPreview = completed.Select(DisplayActivityItem).Where(value => !string.IsNullOrWhiteSpace(value)).Take(10).ToList(),
            PendingPreview = pending.Select(DisplayActivityItem).Where(value => !string.IsNullOrWhiteSpace(value)).Take(10).ToList(),
            ReviewPreview = review.Select(DisplayActivityItem).Where(value => !string.IsNullOrWhiteSpace(value)).Take(10).ToList(),
        };
    }

    private static bool IsOpenEndedDiscoveryTask(string taskKey, string? countUnit) =>
        taskKey.Equals("artwork", StringComparison.OrdinalIgnoreCase)
        || taskKey.Equals("wikidata", StringComparison.OrdinalIgnoreCase)
        || taskKey.Equals("relationships", StringComparison.OrdinalIgnoreCase)
        || taskKey.Equals("people", StringComparison.OrdinalIgnoreCase)
        || string.Equals(countUnit, "artwork assets", StringComparison.OrdinalIgnoreCase)
        || string.Equals(countUnit, "QIDs", StringComparison.OrdinalIgnoreCase)
        || string.Equals(countUnit, "links", StringComparison.OrdinalIgnoreCase)
        || string.Equals(countUnit, "people", StringComparison.OrdinalIgnoreCase);

    private static int ParseMetricCount(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return 0;
        }

        var digits = new string(value.Where(char.IsDigit).ToArray());
        return int.TryParse(digits, out var parsed) ? parsed : 0;
    }

    private static int TaskSort(
        CurrentActivityRow row,
        IReadOnlyCollection<string> activeStates,
        IReadOnlyCollection<string> completedStates,
        IReadOnlyCollection<string> reviewStates)
    {
        if (IsFreshActive(row, activeStates))
        {
            return 0;
        }

        if (!ContainsState(completedStates, row.State) && !ContainsState(reviewStates, row.State))
        {
            return 1;
        }

        return ContainsState(reviewStates, row.State) ? 2 : 3;
    }

    private static bool ContainsState(IReadOnlyCollection<string> states, string? state) =>
        !string.IsNullOrWhiteSpace(state) && states.Contains(state, StringComparer.OrdinalIgnoreCase);

    private static bool IsFreshActive(CurrentActivityRow row, IReadOnlyCollection<string> activeStates)
    {
        if (!ContainsState(activeStates, row.State))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(row.LeaseOwner) || !string.IsNullOrWhiteSpace(row.LeaseExpiresAt))
        {
            var leaseExpires = ParseDate(row.LeaseExpiresAt);
            return !string.IsNullOrWhiteSpace(row.LeaseOwner)
                && leaseExpires is not null
                && leaseExpires.Value.ToUniversalTime() > DateTimeOffset.UtcNow;
        }

        var updated = ParseDate(row.UpdatedAt);
        return updated is null || DateTimeOffset.UtcNow - updated.Value.ToUniversalTime() <= ActiveActivityFreshness;
    }

    private static int IndexOf(IReadOnlyList<CurrentActivityRow> rows, Func<CurrentActivityRow, bool> predicate)
    {
        for (var index = 0; index < rows.Count; index++)
        {
            if (predicate(rows[index]))
            {
                return index;
            }
        }

        return -1;
    }

    private static DateTimeOffset? LatestUpdated(IReadOnlyList<CurrentActivityRow> rows)
    {
        var values = rows
            .Select(row => ParseDate(row.UpdatedAt))
            .Where(value => value.HasValue)
            .Select(value => value!.Value)
            .ToList();
        return values.Count > 0 ? values.Max() : null;
    }

    private static string DisplayActivityItem(CurrentActivityRow row) =>
        FormatArtworkAssetCount(
            StringHelpers.FirstNonBlankOr(string.Empty, row.Title, ShortPath(row.SourcePath), row.MediaType, "Current file"),
            row.ArtworkAssetCount);

    private static string FormatArtworkAssetCount(string title, int? artworkAssetCount) =>
        artworkAssetCount.HasValue && !string.IsNullOrWhiteSpace(title)
            ? $"{title} - {artworkAssetCount.Value.ToString("N0", System.Globalization.CultureInfo.CurrentCulture)}"
            : title;

    private static List<IngestionReviewReasonDto> BuildReviewReasons(
        IReadOnlyList<ReviewCountRow> reviewRows)
    {
        var triggerCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in reviewRows)
        {
            triggerCounts[row.Trigger] = triggerCounts.GetValueOrDefault(row.Trigger) + ToInt(row.Count);
        }

        return
        [
            Review("unmatched", "Unmatched", SumMatching(triggerCounts, "RetailMatchFailed", "StagedUnidentifiable", "MovieMatchedAsTv"), "Items could not be matched to a known catalogue record."),
            Review("low_confidence", "Low Confidence", SumMatching(triggerCounts, "LowConfidence", "RetailMatchAmbiguous", "ArbiterNeedsReview"), "Items matched below the confidence threshold."),
            Review("duplicates", "Duplicates", 0, "Possible duplicate files or editions need confirmation."),
            Review("missing_artwork", "Missing Artwork", SumMatching(triggerCounts, "ArtworkUnconfirmed"), "Artwork is missing or needs confirmation."),
            Review("missing_wikidata", "Missing Wikidata Identity", SumMatching(triggerCounts, "MissingQid", "WikidataBridgeFailed", "MultipleQidMatches"), "Items need canonical identity or Wikidata confirmation."),
            Review("naming", "Naming Issues", SumMatching(triggerCounts, "RootWatchFolder", "PlaceholderTitle", "AmbiguousMediaType"), "File or folder names do not provide enough context."),
            Review("provider_failures", "Provider Failures", SumMatching(triggerCounts, "WritebackFailed"), "A provider or writeback step could not complete."),
            Review("metadata_conflicts", "Metadata Conflicts", SumMatching(triggerCounts, "MetadataConflict", "UserFixMatch", "LanguageMismatch", "UserReport"), "Conflicting metadata requires a human decision."),
        ];
    }

    private async Task<IReadOnlyList<IngestionBatch>> ReconcileCompletedBatchesAsync(
        IReadOnlyList<IngestionBatch> recentBatches,
        CancellationToken ct)
    {
        var changed = false;
        var hasTrackedBatch = recentBatches.Any(batch => batch.FilesProcessed > 0 || HasOutcomeCounters(batch));

        foreach (var batch in recentBatches)
        {
            var isActiveStatus = ActiveBatchStatuses.Contains(batch.Status, StringComparer.OrdinalIgnoreCase);
            var isCompletedWithReviewCounters = string.Equals(batch.Status, "completed", StringComparison.OrdinalIgnoreCase)
                && batch.FilesReview + batch.FilesNoMatch + batch.FilesFailed > 0;
            if (!isActiveStatus && !isCompletedWithReviewCounters)
            {
                continue;
            }

            var snapshot = await ReadBatchTerminalSnapshotAsync(batch.Id, ct);
            var hasSnapshotRows = snapshot.HasRows;
            var identified = hasSnapshotRows ? snapshot.Identified : batch.FilesIdentified;
            var review = hasSnapshotRows ? snapshot.Review : batch.FilesReview;
            var noMatch = hasSnapshotRows ? snapshot.NoMatch + snapshot.OperationNoMatch : batch.FilesNoMatch;
            var failed = hasSnapshotRows ? snapshot.Failed + snapshot.OperationFailed : batch.FilesFailed;
            var skipped = hasSnapshotRows ? snapshot.OperationSkipped + snapshot.OperationOnlyTerminal : 0;
            var terminal = identified + review + noMatch + failed + skipped;
            if (batch.FilesTotal > 0)
            {
                terminal = Math.Clamp(terminal, 0, batch.FilesTotal);
            }
            var age = DateTimeOffset.UtcNow - batch.StartedAt.ToUniversalTime();
            // Queue state survives restarts. An expired lease or a long provider wait
            // is recoverable work, not a failed file or an abandoned batch.
            var noActivePipelineWork = !snapshot.HasOutstandingWork;
            var isNoWorkBatch = batch.FilesTotal > 0
                && terminal == 0
                && batch.FilesProcessed == 0
                && snapshot.TotalJobs == 0
                && snapshot.LogRows == 0
                && noActivePipelineWork
                && ((hasTrackedBatch && age > NoWorkBatchGrace) || age > NoWorkBatchMaximumAge);
            var isStaleUntrackedBatch = batch.FilesTotal > 0
                && terminal == 0
                && batch.FilesProcessed == 0
                && snapshot.TotalJobs == 0
                && noActivePipelineWork
                && DateTimeOffset.UtcNow - batch.StartedAt.ToUniversalTime() > TimeSpan.FromMinutes(30);
            var allFilesTerminal = batch.FilesTotal <= 0
                ? noActivePipelineWork
                : terminal >= batch.FilesTotal || (batch.FilesProcessed >= batch.FilesTotal && noActivePipelineWork);

            if ((!allFilesTerminal && !isStaleUntrackedBatch && !isNoWorkBatch) || !noActivePipelineWork)
            {
                continue;
            }

            var processed = isStaleUntrackedBatch || isNoWorkBatch
                ? batch.FilesTotal
                : Math.Max(batch.FilesProcessed, terminal);
            if (batch.FilesTotal > 0)
            {
                processed = Math.Clamp(processed, 0, batch.FilesTotal);
            }

            var nextStatus = failed > 0 && failed >= Math.Max(1, batch.FilesTotal) ? "failed" : "completed";
            if (batch.FilesProcessed == processed
                && batch.FilesIdentified == identified
                && batch.FilesReview == review
                && batch.FilesNoMatch == noMatch
                && batch.FilesFailed == failed
                && string.Equals(batch.Status, nextStatus, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            await _batchRepository.UpdateCountsAsync(
                batch.Id,
                batch.FilesTotal,
                processed,
                identified,
                review,
                noMatch,
                failed,
                ct);

            await _batchRepository.CompleteAsync(
                batch.Id,
                nextStatus,
                ct);

            changed = true;
        }

        return changed
            ? await _batchRepository.GetRecentAsync(12, ct)
            : recentBatches;
    }

    private async Task<BatchTerminalSnapshot> ReadBatchTerminalSnapshotAsync(Guid batchId, CancellationToken ct)
    {
        using var conn = _db.CreateConnection();
        return await conn.QueryFirstOrDefaultAsync<BatchTerminalSnapshot>(
            $"""
            WITH latest_jobs AS (
                SELECT
                    entity_id,
                    state,
                    lease_owner,
                    lease_expires_at,
                    updated_at,
                    ROW_NUMBER() OVER (
                        PARTITION BY entity_id
                        ORDER BY updated_at DESC, created_at DESC
                    ) AS rn
                FROM identity_jobs
                WHERE ingestion_run_id = @batchId
            ),
            job_states AS (
                SELECT entity_id, state, lease_owner, lease_expires_at, updated_at
                FROM latest_jobs
                WHERE rn = 1
            ),
            pending_reviews AS (
                SELECT DISTINCT entity_id
                FROM review_queue
                WHERE status = 'Pending'
                  AND review_ready_at IS NOT NULL
            )
            SELECT
                (SELECT CASE WHEN {IngestionBatchActivitySql.HasOutstandingWork} THEN 1 ELSE 0 END
                    FROM ingestion_batches b WHERE b.id = @batchId) AS HasOutstandingWork,
                COALESCE(SUM(CASE
                    WHEN js.state IN ('Ready', 'ReadyWithoutUniverse') AND pr.entity_id IS NULL THEN 1
                    ELSE 0
                END), 0) AS Identified,
                COALESCE(SUM(CASE
                    WHEN pr.entity_id IS NOT NULL THEN 1
                    WHEN js.state IN ('QidNeedsReview', 'RetailMatchedNeedsReview') THEN 1
                    ELSE 0
                END), 0) AS Review,
                COALESCE(SUM(CASE WHEN js.state IN ('RetailNoMatch', 'QidNoMatch') AND pr.entity_id IS NULL THEN 1 ELSE 0 END), 0) AS NoMatch,
                COALESCE(SUM(CASE WHEN js.state = 'Failed' AND pr.entity_id IS NULL THEN 1 ELSE 0 END), 0) AS Failed,
                COALESCE(SUM(CASE WHEN js.state = 'Queued' THEN 1 ELSE 0 END), 0) AS Queued,
                COALESCE(SUM(CASE
                    WHEN js.state IN ('RetailSearching', 'BridgeSearching', 'Hydrating', 'UniverseEnriching')
                         AND (
                            (
                                js.lease_owner IS NOT NULL
                                AND js.lease_expires_at IS NOT NULL
                                AND js.lease_expires_at > @now
                            )
                            OR julianday(js.updated_at) > julianday(@staleCutoff)
                         )
                        THEN 1 ELSE 0
                END), 0)
                + (
                    SELECT COUNT(*)
                    FROM media_operations mo
                    WHERE mo.batch_id = @batchId
                      AND mo.status = 'running'
                      AND (
                        (
                            mo.lease_owner IS NOT NULL
                            AND mo.lease_expires_at IS NOT NULL
                            AND mo.lease_expires_at > @now
                        )
                        OR julianday(mo.updated_at) > julianday(@staleCutoff)
                      )
                ) AS Active,
                COALESCE(SUM(CASE
                    WHEN js.state IN ('RetailSearching', 'BridgeSearching', 'Hydrating', 'UniverseEnriching')
                         AND (
                            (
                                js.lease_owner IS NULL
                                OR js.lease_expires_at IS NULL
                                OR js.lease_expires_at <= @now
                            )
                            AND COALESCE(julianday(js.updated_at), 0) <= julianday(@staleCutoff)
                         )
                        THEN 1 ELSE 0
                END), 0) AS StaleActive,
                (
                    SELECT COUNT(*)
                    FROM media_operations mo
                    WHERE mo.batch_id = @batchId
                      AND mo.status = 'running'
                      AND (
                        (
                            mo.lease_owner IS NULL
                            OR mo.lease_expires_at IS NULL
                            OR mo.lease_expires_at <= @now
                        )
                        AND COALESCE(julianday(mo.updated_at), 0) <= julianday(@staleCutoff)
                      )
                ) AS StaleRunningOperations,
                (
                    SELECT COUNT(*)
                    FROM media_operations mo
                    WHERE mo.batch_id = @batchId
                      AND mo.status IN ('pending', 'queued', 'leased', 'running', 'retry_waiting', 'interrupted', 'failed_retryable')
                ) AS OperationOutstanding,
                COUNT(js.entity_id) AS TotalJobs,
                (
                    SELECT COUNT(*)
                    FROM ingestion_log il
                    WHERE il.ingestion_run_id = @batchId
                ) AS LogRows,
                (
                    SELECT COUNT(*)
                    FROM media_operations mo
                    WHERE mo.batch_id = @batchId
                      AND mo.operation_type = 'ingestion.file'
                      AND mo.status IN ('succeeded', 'no_result', 'missing_confirmed', 'not_applicable', 'skipped', 'blocked', 'failed_terminal', 'dead_lettered', 'cancelled')
                ) AS FileOperationsTerminal,
                (
                    SELECT COUNT(*)
                    FROM media_operations mo
                    WHERE mo.batch_id = @batchId
                      AND mo.operation_type = 'ingestion.file'
                      AND mo.status IN ('no_result', 'missing_confirmed')
                ) AS OperationNoMatch,
                (
                    SELECT COUNT(*)
                    FROM media_operations mo
                    WHERE mo.batch_id = @batchId
                      AND mo.operation_type = 'ingestion.file'
                      AND mo.status IN ('not_applicable', 'skipped')
                ) AS OperationSkipped,
                (
                    SELECT COUNT(*)
                    FROM media_operations mo
                    WHERE mo.batch_id = @batchId
                      AND mo.operation_type = 'ingestion.file'
                      AND mo.status IN ('blocked', 'failed_terminal', 'dead_lettered', 'cancelled')
                ) AS OperationFailed
            FROM job_states js
            LEFT JOIN pending_reviews pr ON pr.entity_id = js.entity_id;
            """,
            new
            {
                batchId = GuidSql.ToBlob(batchId),
                now = DateTimeOffset.UtcNow.ToString("O"),
                staleCutoff = DateTimeOffset.UtcNow.Subtract(ActiveBatchFreshness).ToString("O"),
            }) ?? new BatchTerminalSnapshot();
    }

    private static IngestionOperationsJobDto ToActiveJob(
        IngestionBatch batch,
        IReadOnlyDictionary<string, int> pipelineRows,
        IReadOnlyList<IngestionPipelineStageDto> stages)
    {
        var currentStage = ResolveBatchStage(batch, pipelineRows, stages);
        var stageKey = ResolveActivityStageKey(currentStage);
        var progress = ResolveActivityProgress(stageKey, stages);
        var total = Math.Max(0, batch.FilesTotal > 0 ? batch.FilesTotal : progress.Total);
        var processed = Math.Clamp(Math.Max(0, batch.FilesProcessed), 0, total);
        var percent = total > 0
            ? Math.Clamp(processed * 100d / total, 0, 100)
            : 0;

        return new IngestionOperationsJobDto
        {
            JobId = batch.Id,
            JobType = "Ingestion batch",
            MediaType = batch.Category,
            SourceFolder = batch.SourcePath,
            CurrentStage = currentStage,
            CurrentItem = null,
            ProcessedCount = processed,
            TotalCount = total,
            PercentComplete = percent,
            Status = batch.Status,
            Elapsed = DateTimeOffset.UtcNow - batch.StartedAt.ToUniversalTime(),
            LastUpdatedTime = batch.UpdatedAt,
            WarningSummary = batch.FilesFailed > 0 ? $"{batch.FilesFailed:N0} failed" : null,
        };
    }

    private static IngestionCurrentActivityDto ToCurrentActivity(
        CurrentActivityRow row,
        IReadOnlyList<IngestionPipelineStageDto> stages)
    {
        var stageKey = ResolveActivityStageKey(row.State);
        var progress = ResolveActivityProgress(stageKey, stages);
        var currentItem = StringHelpers.FirstNonBlankOr(string.Empty, row.Title, ShortPath(row.SourcePath), row.MediaType, "Current file");
        var total = Math.Max(0, progress.Total);
        var processed = Math.Clamp(Math.Max(0, progress.Count), 0, Math.Max(0, total));

        return new IngestionCurrentActivityDto
        {
            StageKey = stageKey,
            Message = ResolveActivityMessage(row.State),
            Detail = $"Working on {currentItem}",
            CurrentItem = currentItem,
            Source = ShortPath(row.SourcePath),
            ProcessedCount = processed,
            TotalCount = total,
            PercentComplete = total > 0 ? Math.Clamp(processed * 100d / total, 0, 100) : 0,
            LastUpdatedTime = ParseDate(row.UpdatedAt),
            QueuedCount = total > 0 ? Math.Max(0, total - processed - 1) : 0,
            ActiveCount = 1,
            SampleItems = [currentItem],
        };
    }

    private static IngestionCurrentActivityDto ToCurrentActivity(
        IngestionOperationsJobDto job,
        IReadOnlyList<IngestionPipelineStageDto> stages)
    {
        if (!job.Status.Equals("running", StringComparison.OrdinalIgnoreCase)
            && !job.Status.Equals("processing", StringComparison.OrdinalIgnoreCase)
            && !job.Status.Equals("active", StringComparison.OrdinalIgnoreCase))
        {
            return new IngestionCurrentActivityDto();
        }

        var stageKey = ResolveActivityStageKey(job.CurrentStage);
        var progress = ResolveActivityProgress(stageKey, stages);
        var total = Math.Max(0, progress.Total > 0 ? progress.Total : job.TotalCount);
        var processed = Math.Clamp(Math.Max(0, progress.Total > 0 ? progress.Count : job.ProcessedCount), 0, Math.Max(0, total));
        var item = StringHelpers.FirstNonBlankOr(string.Empty, job.CurrentItem, job.CurrentStage, "Ingestion is running");

        return new IngestionCurrentActivityDto
        {
            StageKey = stageKey,
            Message = ResolveActivityMessage(job.CurrentStage),
            Detail = item,
            CurrentItem = item,
            Source = ShortPath(job.SourceFolder),
            ProcessedCount = processed,
            TotalCount = total,
            PercentComplete = total > 0 ? Math.Clamp(processed * 100d / total, 0, 100) : Math.Clamp(job.PercentComplete, 0, 100),
            LastUpdatedTime = job.LastUpdatedTime,
            QueuedCount = total > 0 ? Math.Max(0, total - processed - 1) : 0,
            ActiveCount = 1,
            SampleItems = [item],
        };
    }

    private static string ResolveActivityStageKey(string? stateOrStage)
    {
        var value = stateOrStage ?? string.Empty;
        if (value.Contains("Bridge", StringComparison.OrdinalIgnoreCase)
            || value.Contains("Qid", StringComparison.OrdinalIgnoreCase)
            || value.Contains("Wikidata", StringComparison.OrdinalIgnoreCase)
            || value.Contains("canonical", StringComparison.OrdinalIgnoreCase))
        {
            return "wikidata";
        }

        if (value.Contains("Retail", StringComparison.OrdinalIgnoreCase)
            || value.Contains("match", StringComparison.OrdinalIgnoreCase)
            || value.Contains("identify", StringComparison.OrdinalIgnoreCase))
        {
            return "retail";
        }

        if (value.Contains("Hydrat", StringComparison.OrdinalIgnoreCase)
            || value.Contains("Universe", StringComparison.OrdinalIgnoreCase)
            || value.Contains("Enrich", StringComparison.OrdinalIgnoreCase))
        {
            return "enrichment";
        }

        if (value.Contains("scan", StringComparison.OrdinalIgnoreCase)
            || value.Contains("parse", StringComparison.OrdinalIgnoreCase))
        {
            return "scanning";
        }

        return "scanning";
    }

    private static (int Count, int Total) ResolveActivityProgress(
        string stageKey,
        IReadOnlyList<IngestionPipelineStageDto> stages)
    {
        var byKey = stages.ToDictionary(stage => stage.Key, StringComparer.OrdinalIgnoreCase);
        var duplicateOrSkipped = CountStage(byKey, "duplicate") + CountStage(byKey, "skipped");
        var failed = CountStage(byKey, "failed");

        return stageKey switch
        {
            "retail" => (
                CountStage(byKey, "matched") + CountStage(byKey, "retail_review") + duplicateOrSkipped + failed,
                TotalStage(byKey, "matched")),
            "wikidata" => (
                CountStage(byKey, "canonicalized") + CountStage(byKey, "wikidata_review"),
                TotalStage(byKey, "canonicalized")),
            "enrichment" => (
                CountStage(byKey, "enriched") + CountStage(byKey, "wikidata_review"),
                TotalStage(byKey, "enriched")),
            _ => (
                CountStage(byKey, "detected"),
                TotalStage(byKey, "detected")),
        };
    }

    private static int CountStage(IReadOnlyDictionary<string, IngestionPipelineStageDto> stages, string key) =>
        stages.TryGetValue(key, out var stage) ? stage.Count : 0;

    private static int TotalStage(IReadOnlyDictionary<string, IngestionPipelineStageDto> stages, string key) =>
        stages.TryGetValue(key, out var stage) ? stage.TotalCount : 0;

    private static string ResolveActivityMessage(string? stateOrStage)
    {
        var value = stateOrStage ?? string.Empty;
        if (value.Contains(nameof(IdentityJobState.RetailSearching), StringComparison.OrdinalIgnoreCase)
            || value.Contains("Retail", StringComparison.OrdinalIgnoreCase)
            || value.Contains("match", StringComparison.OrdinalIgnoreCase)
            || value.Contains("identify", StringComparison.OrdinalIgnoreCase))
        {
            return "Matching metadata";
        }

        if (value.Contains(nameof(IdentityJobState.BridgeSearching), StringComparison.OrdinalIgnoreCase)
            || value.Contains("Wikidata", StringComparison.OrdinalIgnoreCase)
            || value.Contains("Qid", StringComparison.OrdinalIgnoreCase)
            || value.Contains("Bridge", StringComparison.OrdinalIgnoreCase))
        {
            return "Checking Wikidata identity";
        }

        if (value.Contains(nameof(IdentityJobState.Hydrating), StringComparison.OrdinalIgnoreCase))
        {
            return "Adding metadata";
        }

        if (value.Contains(nameof(IdentityJobState.UniverseEnriching), StringComparison.OrdinalIgnoreCase)
            || value.Contains("Enrich", StringComparison.OrdinalIgnoreCase)
            || value.Contains("Universe", StringComparison.OrdinalIgnoreCase))
        {
            return "Enriching relationships";
        }

        if (value.Contains("scan", StringComparison.OrdinalIgnoreCase))
        {
            return "Scanning files";
        }

        return "Processing files";
    }

    private async Task<Dictionary<Guid, BatchActivityStats>> ReadBatchStatsAsync(
        IReadOnlyList<IngestionBatch> batches,
        CancellationToken ct)
    {
        if (batches.Count == 0)
        {
            return [];
        }

        var result = batches.ToDictionary(batch => batch.Id, _ => new BatchActivityStats());
        using var conn = _db.CreateConnection();

        foreach (var batch in batches)
        {
            ct.ThrowIfCancellationRequested();
            var rows = (await conn.QueryAsync<StageCountRow>("""
                SELECT action_type AS Key, COUNT(DISTINCT COALESCE(entity_id, CAST(id AS TEXT))) AS Count
                FROM system_activity
                WHERE ingestion_run_id = @batchId
                GROUP BY action_type
                """, new { batchId = batch.Id })).AsList();

            var byType = rows.ToDictionary(row => row.Key, row => ToInt(row.Count), StringComparer.OrdinalIgnoreCase);
            var mediaRows = (await conn.QueryAsync<StageCountRow>("""
                WITH latest_jobs AS (
                    SELECT
                        entity_id,
                        media_type,
                        ROW_NUMBER() OVER (
                            PARTITION BY entity_id
                            ORDER BY updated_at DESC, created_at DESC
                        ) AS rn
                    FROM identity_jobs
                    WHERE ingestion_run_id = @batchId
                )
                SELECT media_type AS Key, COUNT(*) AS Count
                FROM latest_jobs
                WHERE rn = 1
                  AND media_type IS NOT NULL
                  AND media_type <> ''
                GROUP BY media_type
                """, new { batchId = batch.Id })).AsList();
            var mediaCounts = mediaRows.ToDictionary(row => row.Key, row => ToInt(row.Count), StringComparer.OrdinalIgnoreCase);
            ApplyBatchCategoryFallback(batch, mediaCounts);
            var pendingReviewCount = await conn.ExecuteScalarAsync<int>("""
                SELECT COUNT(DISTINCT rq.id)
                FROM review_queue rq
                INNER JOIN identity_jobs ij ON ij.entity_id = rq.entity_id
                WHERE rq.status = 'Pending'
                  AND rq.review_ready_at IS NOT NULL
                  AND ij.ingestion_run_id = @batchId
                """, new { batchId = GuidSql.ToBlob(batch.Id) });

            result[batch.Id] = new BatchActivityStats(
                MoviesCount: CountMediaTypes(mediaCounts, "Movies", "Movie"),
                TvShowsCount: CountMediaTypes(mediaCounts, "TV", "TV Shows", "TvShows", "Shows"),
                BooksCount: CountMediaTypes(mediaCounts, "Books", "Book"),
                AudiobooksCount: CountMediaTypes(mediaCounts, "Audiobooks", "Audiobook"),
                MusicCount: CountMediaTypes(mediaCounts, "Music", "MusicTrack", "MusicAlbum", "Album", "Song"),
                ComicsCount: CountMediaTypes(mediaCounts, "Comics", "Comic"),
                PeopleGeneratedCount: Count(byType, SystemActionType.PersonHydrated)
                    + Count(byType, SystemActionType.PersonMerged),
                ArtworkDownloadedCount: Count(byType, SystemActionType.CoverArtSaved),
                MetadataUpdatedCount: Count(byType, SystemActionType.MetadataHydrated)
                    + Count(byType, SystemActionType.NarrativeRootResolved)
                    + Count(byType, SystemActionType.RelationshipDiscovered)
                    + Count(byType, SystemActionType.CharacterEnriched)
                    + Count(byType, SystemActionType.LocationEnriched)
                    + Count(byType, SystemActionType.OrganizationEnriched),
                PendingReviewCount: pendingReviewCount);
        }

        return result;
    }

    private async Task<List<IngestionOperationsBatchDto>> BuildRecentBatchDtosAsync(
        IReadOnlyList<DisplayBatchGroup> recentBatchGroups,
        IReadOnlyList<IngestionBatch> recentBatches,
        IReadOnlyDictionary<Guid, BatchActivityStats> batchStats,
        LibraryItemLifecycleCounts lifecycle,
        LibraryItemProjectionSummary projection,
        CancellationToken ct)
    {
        if (recentBatchGroups.Count == 0)
        {
            return [];
        }

        var recentById = recentBatches.ToDictionary(batch => batch.Id);
        var result = new List<IngestionOperationsBatchDto>(recentBatchGroups.Count);

        foreach (var group in OrderRecentBatchGroupsForDisplay(recentBatchGroups))
        {
            ct.ThrowIfCancellationRequested();

            var sourceBatches = group.SourceBatchIds
                .Select(id => recentById.GetValueOrDefault(id))
                .Where(batch => batch is not null)
                .Cast<IngestionBatch>()
                .ToList();
            if (sourceBatches.Count == 0)
            {
                sourceBatches = [group.Batch];
            }

            var stats = AggregateBatchStats(group.SourceBatchIds, batchStats);
            var groupPipelineRows = await ReadIdentityStateCountsAsync(group.SourceBatchIds, ct).ConfigureAwait(false);
            var groupIngestionRows = await ReadIngestionStatusCountsAsync(group.SourceBatchIds, ct).ConfigureAwait(false);
            var groupStages = BuildPipelineStages(
                groupPipelineRows,
                groupIngestionRows,
                lifecycle,
                projection,
                group.Batch,
                stats.PendingReviewCount);
            var groupActivities = await ReadTaskActivitiesAsync(
                group.SourceBatchIds.ToArray(),
                groupStages,
                ct).ConfigureAwait(false);
            var stageProgress = await BuildNumberedStageProgressAsync(
                sourceBatches,
                groupStages,
                groupActivities,
                stats.PendingReviewCount,
                Math.Max(0, group.Batch.FilesTotal),
                ct).ConfigureAwait(false);
            var progressSnapshots = new List<IngestionBatchProgressSnapshot>(group.SourceBatchIds.Count);
            foreach (var batchId in group.SourceBatchIds)
            {
                progressSnapshots.Add(await _batchRepository.GetProgressSnapshotAsync(batchId, ct).ConfigureAwait(false));
            }

            var dto = ToRecentBatchWithStageProgress(group.Batch, stats, stageProgress);
            dto.OutstandingOperations = progressSnapshots.Sum(progress => Math.Max(0, progress.OutstandingOperations));
            dto.ActiveOperations = progressSnapshots.Sum(progress => Math.Max(0, progress.ActiveOperations));
            dto.QueuedOperations = progressSnapshots.Sum(progress => Math.Max(0, progress.QueuedOperations));
            dto.RetryWaitingOperations = progressSnapshots.Sum(progress => Math.Max(0, progress.RetryWaitingOperations));
            result.Add(dto);
        }

        return result;
    }

    private static IReadOnlyList<DisplayBatchGroup> OrderRecentBatchGroupsForDisplay(
        IReadOnlyList<DisplayBatchGroup> recentBatchGroups)
    {
        if (recentBatchGroups.Count <= 1)
        {
            return recentBatchGroups;
        }

        var selected = SelectDisplayBatchGroup(recentBatchGroups);
        return recentBatchGroups
            .OrderByDescending(group => ReferenceEquals(group, selected))
            .ThenByDescending(group =>
                ActiveBatchStatuses.Contains(group.Batch.Status, StringComparer.OrdinalIgnoreCase)
                && ShouldShowActiveBatch(group.Batch))
            .ThenByDescending(group => Math.Max(group.Batch.FilesTotal, group.Batch.FilesProcessed))
            .ThenByDescending(group => group.Batch.StartedAt)
            .ToList();
    }

    private static IngestionOperationsBatchDto ToRecentBatch(
        IngestionBatch batch,
        BatchActivityStats? stats) =>
        ToRecentBatchWithStageProgress(batch, stats, null);

    private static IngestionOperationsBatchDto ToRecentBatchWithStageProgress(
        IngestionBatch batch,
        BatchActivityStats? stats,
        List<IngestionStageProgressDto>? stageProgress = null)
    {
        var source = StringHelpers.FirstNonBlankOr(string.Empty, batch.Category, ShortPath(batch.SourcePath), "Library scan");
        var reviewCount = Math.Max(0, stats?.PendingReviewCount ?? batch.FilesReview);
        var hasActiveStageWork = HasActiveStageWork(stageProgress);
        var status = hasActiveStageWork ? "running" : batch.Status;
        var completedAt = hasActiveStageWork ? null : batch.CompletedAt;
        var duration = (completedAt ?? DateTimeOffset.UtcNow) - batch.StartedAt;
        return new IngestionOperationsBatchDto
        {
            BatchId = batch.Id,
            StartedAt = batch.StartedAt,
            CompletedAt = completedAt,
            Source = source,
            MediaType = batch.Category,
            TotalFiles = batch.FilesTotal,
            ProcessedFiles = batch.FilesProcessed,
            MoviesCount = stats?.MoviesCount ?? 0,
            TvShowsCount = stats?.TvShowsCount ?? 0,
            BooksCount = stats?.BooksCount ?? 0,
            AudiobooksCount = stats?.AudiobooksCount ?? 0,
            MusicCount = stats?.MusicCount ?? 0,
            ComicsCount = stats?.ComicsCount ?? 0,
            RegisteredCount = batch.FilesIdentified,
            ReviewCount = reviewCount,
            FailedCount = batch.FilesFailed,
            PeopleGeneratedCount = stats?.PeopleGeneratedCount ?? 0,
            ArtworkDownloadedCount = stats?.ArtworkDownloadedCount ?? 0,
            MetadataUpdatedCount = stats?.MetadataUpdatedCount ?? 0,
            DurationSeconds = duration.TotalSeconds > 0 ? (int)Math.Round(duration.TotalSeconds) : null,
            Status = status,
            Summary = $"{batch.FilesIdentified:N0} registered, {reviewCount:N0} review",
            StageProgress = stageProgress ?? [],
        };
    }

    private static bool HasActiveStageWork(IReadOnlyList<IngestionStageProgressDto>? stageProgress) =>
        stageProgress is not null
        && stageProgress.Any(stage =>
            stage.StageNumber >= 3
            && (stage.ActiveCount > 0
                || stage.QueuedCount > 0
                || string.Equals(stage.StatusLabel, "Active", StringComparison.OrdinalIgnoreCase)
                || string.Equals(stage.StatusLabel, "Queued", StringComparison.OrdinalIgnoreCase)));

    private static BatchActivityStats AggregateBatchStats(
        IReadOnlyCollection<Guid> batchIds,
        IReadOnlyDictionary<Guid, BatchActivityStats> statsByBatch)
    {
        if (batchIds.Count == 0)
        {
            return new();
        }

        var stats = batchIds
            .Select(id => statsByBatch.GetValueOrDefault(id))
            .Where(stats => stats is not null)
            .Cast<BatchActivityStats>()
            .ToList();

        if (stats.Count == 0)
        {
            return new();
        }

        return new BatchActivityStats(
            MoviesCount: stats.Sum(item => item.MoviesCount),
            TvShowsCount: stats.Sum(item => item.TvShowsCount),
            BooksCount: stats.Sum(item => item.BooksCount),
            AudiobooksCount: stats.Sum(item => item.AudiobooksCount),
            MusicCount: stats.Sum(item => item.MusicCount),
            ComicsCount: stats.Sum(item => item.ComicsCount),
            PeopleGeneratedCount: stats.Sum(item => item.PeopleGeneratedCount),
            ArtworkDownloadedCount: stats.Sum(item => item.ArtworkDownloadedCount),
            MetadataUpdatedCount: stats.Sum(item => item.MetadataUpdatedCount),
            PendingReviewCount: stats.Sum(item => item.PendingReviewCount));
    }

    private sealed record DisplayBatchGroup(
        IngestionBatch Batch,
        IReadOnlyCollection<Guid> SourceBatchIds);

    private sealed record BatchActivityStats(
        int MoviesCount = 0,
        int TvShowsCount = 0,
        int BooksCount = 0,
        int AudiobooksCount = 0,
        int MusicCount = 0,
        int ComicsCount = 0,
        int PeopleGeneratedCount = 0,
        int ArtworkDownloadedCount = 0,
        int MetadataUpdatedCount = 0,
        int PendingReviewCount = 0);

    private static IngestionProviderHealthDto ToProviderDto(ProviderConfig provider, ProviderHealthRecord? health)
    {
        var status = !provider.Enabled
            ? "Disabled"
            : health?.Status switch
            {
                ProviderHealthStatus.Healthy => "Healthy",
                ProviderHealthStatus.Degraded => "Degraded",
                ProviderHealthStatus.Down => "Offline",
                _ => ProviderLooksConfigured(provider) ? "Unknown" : "Missing Configuration",
            };

        return new IngestionProviderHealthDto
        {
            ProviderId = provider.Name,
            DisplayName = DisplayProviderName(provider.Name),
            Status = status,
            LastSuccessfulCall = health?.LastSuccessAt,
            LastError = health?.LastFailureReason,
            Warning = health is { ConsecutiveFailures: > 0 }
                ? $"{health.ConsecutiveFailures} consecutive failure(s)"
                : provider.ThrottleMs > 0 ? $"Throttled at {provider.ThrottleMs}ms" : null,
        };
    }

    private static IngestionProviderActivityDto ToProviderActivityDto(ProviderActivitySnapshot activity) => new()
    {
        ProviderName = activity.ProviderName,
        ActiveRequests = activity.ActiveRequests,
        WaitingRequests = activity.WaitingRequests,
        RequestsTotal = activity.RequestsTotal,
        RequestsLastMinute = activity.RequestsLastMinute,
        MaxActiveLastMinute = activity.MaxActiveLastMinute,
        ErrorsTotal = activity.ErrorsTotal,
        ErrorsLastMinute = activity.ErrorsLastMinute,
        ThrottleWaitMsTotal = activity.ThrottleWaitMsTotal,
        WaitMsLastMinute = activity.WaitMsLastMinute,
        AverageWaitMs = activity.AverageWaitMs,
        AverageLatencyMs = activity.AverageLatencyMs,
        LastSuccessAt = activity.LastSuccessAt,
        LastRequestAt = activity.LastRequestAt,
        LastError = activity.LastError,
    };

    private static bool IsIngestionProvider(ProviderConfig provider)
    {
        var id = provider.Name.ToLowerInvariant();
        if (id.Contains("apple") || id.Contains("tmdb") || id.Contains("musicbrainz")
            || id.Contains("wikidata") || id.Contains("wikipedia") || id.Contains("comic")
            || id.Contains("open_library") || id.Contains("audible"))
        {
            return true;
        }

        return provider.CapabilityTags.Any(tag =>
            tag.Contains("cover", StringComparison.OrdinalIgnoreCase)
            || tag.Contains("identity", StringComparison.OrdinalIgnoreCase)
            || tag.Contains("metadata", StringComparison.OrdinalIgnoreCase));
    }

    private static bool ProviderLooksConfigured(ProviderConfig provider) =>
        provider.Endpoints.Count > 0 || provider.AvailableFields.Count > 0 || provider.CapabilityTags.Count > 0;

    private static FolderProbe ProbeFolder(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return new("Unavailable", null, null);
        }

        try
        {
            if (!Directory.Exists(path))
            {
                return new("Unreachable", false, false);
            }

            _ = Directory.EnumerateFileSystemEntries(path).Take(1).ToList();
            return new("Reachable", true, true);
        }
        catch (UnauthorizedAccessException)
        {
            return new("Permission issue", true, false);
        }
        catch (IOException)
        {
            return new("Unavailable", false, false);
        }
    }

    private static IReadOnlyList<string> EffectiveSourcePaths(LibraryFolderConfig library)
    {
        return library.ScannableSources.Select(source => source.Path).ToList();
    }

    private static string ResolveIntent(string area, string category) => area.ToLowerInvariant() switch
    {
        LibraryAreas.Watch => "Watch",
        LibraryAreas.Listen => "Listen",
        LibraryAreas.View => "View",
        LibraryAreas.Read => "Read",
        _ => category,
    };

    private static string NormalizeCategory(string category) => category.Trim() switch
    {
        "" => "Library",
        "Movie" => "Movies",
        "TV" => "TV Shows",
        "Audiobook" => "Audiobooks",
        "Book" => "Books",
        "Comic" => "Comics",
        var value => value,
    };

    private static string ResolvePurpose(LibraryFolderConfig library)
    {
        if (library.Sources.All(source => !source.AllowsFileMutation))
        {
            return "archive";
        }

        return library.AcceptedIntakeModes.Contains(
                LibraryIntakeModes.IncomingFolder,
                StringComparer.OrdinalIgnoreCase)
            ? "incoming"
            : "primary";
    }

    private static string ResolveBatchStage(
        IngestionBatch batch,
        IReadOnlyDictionary<string, int> pipelineRows,
        IReadOnlyList<IngestionPipelineStageDto> stages)
    {
        var scanningProgress = ResolveActivityProgress("scanning", stages);
        var retailProgress = ResolveActivityProgress("retail", stages);
        var wikidataProgress = ResolveActivityProgress("wikidata", stages);
        var enrichmentProgress = ResolveActivityProgress("enrichment", stages);

        if (IsIncomplete(scanningProgress))
        {
            return "Scanning source folder";
        }

        if (Count(pipelineRows, nameof(IdentityJobState.RetailSearching)) > 0
            || Count(pipelineRows, nameof(IdentityJobState.Queued)) > 0)
        {
            return "Retail identification";
        }

        if (Count(pipelineRows, nameof(IdentityJobState.BridgeSearching)) > 0)
        {
            return "Wikidata matching";
        }

        if (Count(pipelineRows, nameof(IdentityJobState.Hydrating)) > 0
            || Count(pipelineRows, nameof(IdentityJobState.UniverseEnriching)) > 0)
        {
            return "Enrichment";
        }

        if (IsIncomplete(retailProgress))
        {
            return "Retail identification";
        }

        if (IsIncomplete(wikidataProgress))
        {
            return "Wikidata matching";
        }

        if (IsIncomplete(enrichmentProgress))
        {
            return "Enrichment";
        }

        if (batch.FilesFailed > 0 || batch.FilesNoMatch > 0)
        {
            return "Attention needed";
        }

        if (batch.FilesReview > 0)
        {
            return "Review decisions";
        }

        if (batch.FilesIdentified > 0)
        {
            return "Registering library items";
        }

        return "Scanning source folder";
    }

    private static bool IsIncomplete((int Count, int Total) progress) =>
        progress.Total > 0 && progress.Count < progress.Total;

    private static bool IsActiveActivity(IngestionCurrentActivityDto activity) =>
        activity.ActiveCount > 0
        || (activity.QueuedCount > 0
            && activity.TotalCount > 0
            && activity.ProcessedCount < activity.TotalCount
            && IsFreshQueuedActivity(activity.LastUpdatedTime));

    private static bool IsFreshQueuedActivity(DateTimeOffset? updatedAt)
    {
        if (!updatedAt.HasValue)
        {
            return true;
        }

        return DateTimeOffset.UtcNow - updatedAt.Value.ToUniversalTime() <= ActiveActivityFreshness;
    }

    private static string ResolveHealthLabel(int review, int providerWarnings, int failedJobs, int activeJobs)
    {
        if (failedJobs > 0 || providerWarnings > 0)
        {
            return "Needs attention";
        }

        if (review > 0)
        {
            return "Review needed";
        }

        return activeJobs > 0 ? "Working" : "Healthy";
    }

    private static int ProviderSortKey(string name)
    {
        var lower = name.ToLowerInvariant();
        if (lower.Contains("apple"))
        {
            return 0;
        }

        if (lower.Contains("tmdb"))
        {
            return 1;
        }

        if (lower.Contains("musicbrainz"))
        {
            return 2;
        }

        if (lower.Contains("wikidata"))
        {
            return 3;
        }

        if (lower.Contains("wikipedia"))
        {
            return 4;
        }

        if (lower.Contains("comic"))
        {
            return 5;
        }

        return 20;
    }

    private static string DisplayProviderName(string name) => name.Replace("_", " ") switch
    {
        "apple api" => "Apple Books",
        "tmdb" => "TMDB",
        "musicbrainz" => "MusicBrainz",
        "wikidata reconciliation" => "Wikidata",
        "open library" => "Open Library",
        var value => System.Globalization.CultureInfo.CurrentCulture.TextInfo.ToTitleCase(value),
    };

    private static IngestionPipelineStageDto Stage(string key, string label, int count, int totalCount, string helper) =>
        new()
        {
            Key = key,
            Label = label,
            Count = Math.Max(0, count),
            TotalCount = Math.Max(0, totalCount),
            Helper = helper
        };

    private static IngestionReviewReasonDto Review(string key, string label, int count, string explanation) =>
        new() { Key = key, Label = label, Count = Math.Max(0, count), Explanation = explanation };

    private static int Count(IReadOnlyDictionary<string, int> counts, string key) =>
        counts.TryGetValue(key, out var count) ? count : 0;

    private static int ClampStageCount(int count, int total) =>
        total > 0 ? Math.Clamp(Math.Max(0, count), 0, total) : Math.Max(0, count);

    private static int CountMediaTypes(IReadOnlyDictionary<string, int> counts, params string[] keys) =>
        keys.Sum(key => Count(counts, key));

    private static void ApplyBatchCategoryFallback(IngestionBatch batch, Dictionary<string, int> mediaCounts)
    {
        if (mediaCounts.Values.Sum() > 0 || string.IsNullOrWhiteSpace(batch.Category) || batch.FilesTotal <= 0)
        {
            return;
        }

        var category = NormalizeCategory(batch.Category);
        if (category is "Movies" or "TV Shows" or "Books" or "Audiobooks" or "Music" or "Comics")
        {
            mediaCounts[category] = batch.FilesTotal;
        }
    }

    private static int SumStates(IReadOnlyDictionary<string, int> counts, IEnumerable<string> states) =>
        states.Sum(state => Count(counts, state));

    private static int ToInt(long value) =>
        value > int.MaxValue ? int.MaxValue : (int)Math.Max(0, value);

    private static int SumMatching(IReadOnlyDictionary<string, int> counts, params string[] keys) =>
        keys.Sum(key => Count(counts, key));

    private static DateTimeOffset? ParseDate(string? value) =>
        DateTimeOffset.TryParse(value, out var parsed) ? parsed : null;

    private static string ShortPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        var trimmed = path.TrimEnd('\\', '/');
        var index = Math.Max(trimmed.LastIndexOf('\\'), trimmed.LastIndexOf('/'));
        return index >= 0 ? trimmed[(index + 1)..] : trimmed;
    }


    private sealed class StageCountRow
    {
        public string Key { get; set; } = "";
        public long Count { get; set; }
    }

    private sealed class ReviewCountRow
    {
        public string Trigger { get; set; } = "";
        public string? Detail { get; set; }
        public long Count { get; set; }
    }

    private sealed class FolderStatsRow
    {
        public string SourcePath { get; set; } = "";
        public string? LastScan { get; set; }
        public long ItemCount { get; set; }
        public long UnresolvedCount { get; set; }
    }

    private sealed class CurrentActivityRow
    {
        public string EntityId { get; set; } = "";
        public string State { get; set; } = "";
        public string? MediaType { get; set; }
        public string? LeaseOwner { get; set; }
        public string? LeaseExpiresAt { get; set; }
        public string? UpdatedAt { get; set; }
        public string? SourcePath { get; set; }
        public string? Title { get; set; }
        public int? ArtworkAssetCount { get; set; }
    }

    private sealed class RecentIdentityJobRow
    {
        public Guid JobId { get; set; }
        public string? State { get; set; }
        public string? MediaType { get; set; }
        public string? LastError { get; set; }
        public string? CreatedAt { get; set; }
        public string? UpdatedAt { get; set; }
        public string? SourcePath { get; set; }
        public string? Title { get; set; }
    }

    private sealed class WorkerItemRow
    {
        public string? Title { get; init; }
        public string? Detail { get; init; }
        public string? UpdatedAt { get; init; }
    }

    private sealed class TaskOperationRow
    {
        public string OperationType { get; init; } = "";
        public string Status { get; init; } = "";
        public string? Stage { get; init; }
        public string? ProviderId { get; init; }
        public string? SourcePath { get; init; }
        public string? ResultSummary { get; init; }
        public string? LeaseOwner { get; init; }
        public string? LeaseExpiresAt { get; init; }
        public string? UpdatedAt { get; init; }
        public string? Title { get; init; }
    }

    private sealed class TaskProgressCountRow
    {
        public int Processed { get; init; }
        public int Total { get; init; }
        public int Active { get; init; }
        public int Queued { get; init; }
        public string? UpdatedAt { get; init; }
    }

    private sealed class ActivityMetricCounts
    {
        public int ArtworkCount { get; init; }
        public int RelationshipCount { get; init; }
        public int PersonCount { get; init; }
        public int IssueCount { get; init; }
    }

    private sealed class NumberedStageArtifactCounts
    {
        public int ProviderMatches { get; init; }
        public int BooksCount { get; set; }
        public int MoviesCount { get; set; }
        public int TvShowsCount { get; set; }
        public int MusicCount { get; set; }
        public int ComicsCount { get; set; }
        public int AudiobooksCount { get; set; }
        public int RetailBooksCount { get; init; }
        public int RetailMoviesCount { get; init; }
        public int RetailTvShowsCount { get; init; }
        public int RetailMusicCount { get; init; }
        public int RetailComicsCount { get; init; }
        public int RetailAudiobooksCount { get; init; }
        public int MetadataFields { get; init; }
        public int CoverUrls { get; init; }
        public int PrimaryArtworkCount { get; init; }
        public int FilesWithQid { get; init; }
        public int MediaQidCount { get; init; }
        public int RelatedQidCount { get; init; }
        public int RelevantQidCount { get; init; }
        public int RetailUnresolvedCount { get; init; }
        public int RetailFailedCount { get; init; }
        public int WikidataUnresolvedCount { get; init; }
        public int WikidataFailedCount { get; init; }
        public int PeopleCount { get; init; }
        public int CastCount { get; init; }
        public int DirectorCount { get; init; }
        public int AuthorCount { get; init; }
        public int NarratorCount { get; init; }
        public int MusicArtistCount { get; init; }
        public int CreatorCrewCount { get; init; }
        public int DeepPeopleCount { get; init; }
        public int UniverseLinkCount { get; init; }
        public int FilesWithUniverseLinks { get; init; }
        public int SeriesShelfCount { get; init; }
        public int UniverseRootCount { get; init; }
        public int FictionalEntityCount { get; init; }
        public int AdaptationStoryLinkCount { get; init; }
        public int DeepArtworkCount { get; init; }
        public int DeepArtworkFiles { get; init; }
        public int HeadshotCount { get; init; }
        public int BackdropBannerLogoCount { get; init; }
        public int BackdropBannerCount { get; init; }
        public int LogoCount { get; init; }
        public int SeasonEpisodeArtworkCount { get; init; }
        public int MusicArtworkCount { get; init; }

        public bool HasMediaBreakdown =>
            BooksCount + MoviesCount + TvShowsCount + MusicCount + ComicsCount + AudiobooksCount > 0;

        public int TotalArtworkCount =>
            PrimaryArtworkCount + HeadshotCount + BackdropBannerCount + LogoCount + SeasonEpisodeArtworkCount + MusicArtworkCount;

        public int RetailArtifactCount =>
            ProviderMatches + MetadataFields + CoverUrls + PrimaryArtworkCount;
    }

    private sealed class BatchTerminalSnapshot
    {
        public bool HasOutstandingWork { get; init; }
        public int Identified { get; init; }
        public int Review { get; init; }
        public int NoMatch { get; init; }
        public int Failed { get; init; }
        public int Queued { get; init; }
        public int Active { get; init; }
        public int StaleActive { get; init; }
        public int StaleRunningOperations { get; init; }
        public int TotalJobs { get; init; }
        public int LogRows { get; init; }
        public int OperationNoMatch { get; init; }
        public int OperationSkipped { get; init; }
        public int OperationFailed { get; init; }
        public int FileOperationsTerminal { get; init; }
        public int OperationOutstanding { get; init; }
        public int OperationOnlyTerminal => Math.Max(0, FileOperationsTerminal - TotalJobs);
        public int OperationTerminal => OperationNoMatch + OperationSkipped + OperationFailed + OperationOnlyTerminal;
        public bool HasRows => TotalJobs > 0 || LogRows > 0 || FileOperationsTerminal > 0 || StaleRunningOperations > 0;
    }

    private sealed record BatchSummaryTotals(int Total, int Registered, int Review);

    private sealed record FolderProbe(string Label, bool? IsReachable, bool? PermissionsValid);

    private sealed record TaskOperationProgress(
        int Processed,
        int Total,
        int Active,
        int Queued,
        DateTimeOffset? LastUpdated,
        IReadOnlyList<CurrentActivityRow> Rows);

    private sealed record TaskProgressOverride(
        int Processed,
        int Total,
        int Active,
        int Queued,
        DateTimeOffset? LastUpdated,
        bool PreferExact,
        string? CountUnit = null);

    private sealed record RecentActivityProgress(int Active, DateTimeOffset? LastUpdated);
}
