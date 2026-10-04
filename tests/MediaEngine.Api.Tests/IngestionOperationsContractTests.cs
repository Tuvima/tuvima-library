using System.Collections;
using System.Reflection;
using MediaEngine.Api.Endpoints;
using MediaEngine.Api.Models;
using MediaEngine.Api.Services;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;

namespace MediaEngine.Api.Tests;

public sealed class IngestionOperationsContractTests
{
    [Fact]
    public void IngestionEndpoints_ExposeLibraryOperationsSnapshot()
    {
        var repoRoot = FindRepoRoot();
        var endpointSource = File.ReadAllText(Path.Combine(
            repoRoot,
            "src",
            "MediaEngine.Api",
            "Endpoints",
            "IngestionEndpoints.cs"));

        Assert.Contains("/operations", endpointSource, StringComparison.Ordinal);
        Assert.Contains("/presentation", endpointSource, StringComparison.Ordinal);
        Assert.Contains("/media-groups", endpointSource, StringComparison.Ordinal);
        Assert.Contains("/recent-additions", endpointSource, StringComparison.Ordinal);
        Assert.Contains("/media-groups/{groupId:guid}/children", endpointSource, StringComparison.Ordinal);
        Assert.Contains("IIngestionOperationsStatusService", endpointSource, StringComparison.Ordinal);
        Assert.Contains("IIngestionPresentationReadService", endpointSource, StringComparison.Ordinal);
        Assert.Contains("GetIngestionOperationsSnapshot", endpointSource, StringComparison.Ordinal);
    }

    [Fact]
    public void OperationsService_UsesRealRepositoriesAndConfiguration()
    {
        var repoRoot = FindRepoRoot();
        var source = File.ReadAllText(Path.Combine(
            repoRoot,
            "src",
            "MediaEngine.Api",
            "Services",
            "IngestionOperationsStatusService.cs"));

        Assert.Contains("IDatabaseConnection", source, StringComparison.Ordinal);
        Assert.Contains("IProviderHealthRepository", source, StringComparison.Ordinal);
        Assert.Contains("IIngestionBatchRepository", source, StringComparison.Ordinal);
        Assert.Contains("ILibraryItemRepository", source, StringComparison.Ordinal);
        Assert.Contains("LoadLibraries", source, StringComparison.Ordinal);
        Assert.Contains("LoadAllProviders", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Dune Messiah", source, StringComparison.Ordinal);
    }

    [Fact]
    public void OperationsService_ReportsPipelineStagesAsFileCounts()
    {
        var repoRoot = FindRepoRoot();
        var source = File.ReadAllText(Path.Combine(
            repoRoot,
            "src",
            "MediaEngine.Api",
            "Services",
            "IngestionOperationsStatusService.cs"));

        Assert.Contains("CurrentActivities", source, StringComparison.Ordinal);
        Assert.Contains("EnrichmentCompleteStates", source, StringComparison.Ordinal);
        Assert.Contains("BuildPipelineStages(batchPipelineRows, batchIngestionRows", source, StringComparison.Ordinal);
        Assert.Contains("ToActiveJob(batch, batchPipelineRows, batchStages)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ProcessedCount = batch.FilesProcessed", source, StringComparison.Ordinal);
        Assert.DoesNotContain("projection.EnrichedStage3", source, StringComparison.Ordinal);
        Assert.DoesNotContain("EnrichedStates", source, StringComparison.Ordinal);
    }

    [Fact]
    public void OperationsService_IgnoresStaleHarnessManifestForDifferentActiveRun()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepoRoot(),
            "src",
            "MediaEngine.Api",
            "Services",
            "IngestionOperationsStatusService.cs"));

        Assert.Contains("LoadManifestExpectedOutcomes(summaryTotals.Total)", source, StringComparison.Ordinal);
        Assert.Contains("counts.ExpectedResolved > activeFileTotal", source, StringComparison.Ordinal);
        Assert.Contains("return null;", source, StringComparison.Ordinal);
    }

    [Fact]
    public void OperationsService_ReconcilesCompletedCountersAgainstTerminalLifecycleStates()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepoRoot(),
            "src",
            "MediaEngine.Api",
            "Services",
            "IngestionOperationsStatusService.cs"));

        Assert.Contains("isCompletedWithReviewCounters", source, StringComparison.Ordinal);
        Assert.Contains("js.state IN ('Ready', 'ReadyWithoutUniverse')", source, StringComparison.Ordinal);
        Assert.Contains("review_ready_at IS NOT NULL", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ma.status = 'Normal'", source, StringComparison.Ordinal);
        Assert.Contains("hasSnapshotRows ? snapshot.Review : batch.FilesReview", source, StringComparison.Ordinal);
        Assert.Contains("activity.QueuedCount > 0", source, StringComparison.Ordinal);
    }

    [Fact]
    public void OperationsService_UserFacingReviewCountsExcludeNoMatchAndFailures()
    {
        var batch = new IngestionBatch
        {
            Id = Guid.NewGuid(),
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            CompletedAt = DateTimeOffset.UtcNow,
            Status = "completed",
            FilesTotal = 12,
            FilesProcessed = 12,
            FilesIdentified = 6,
            FilesReview = 2,
            FilesNoMatch = 3,
            FilesFailed = 1,
        };
        var lifecycle = new LibraryItemLifecycleCounts(6, 99, 0, 0, 0, 0, new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase));
        var projection = new LibraryItemProjectionSummary(12, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

        var summaryMethod = typeof(IngestionOperationsStatusService).GetMethod(
            "BuildSummaryTotals",
            BindingFlags.Static | BindingFlags.NonPublic);
        var stagesMethod = typeof(IngestionOperationsStatusService).GetMethod(
            "BuildPipelineStages",
            BindingFlags.Static | BindingFlags.NonPublic);
        var recentBatchMethod = typeof(IngestionOperationsStatusService).GetMethod(
            "ToRecentBatch",
            BindingFlags.Static | BindingFlags.NonPublic);

        Assert.NotNull(summaryMethod);
        Assert.NotNull(stagesMethod);
        Assert.NotNull(recentBatchMethod);

        var summary = summaryMethod.Invoke(null, [batch, lifecycle, projection, 4])
            ?? throw new InvalidOperationException("BuildSummaryTotals returned null.");
        var stages = Assert.IsAssignableFrom<IEnumerable<IngestionPipelineStageDto>>(stagesMethod.Invoke(
            null,
            [
                new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
                new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
                lifecycle,
                projection,
                batch,
                4,
            ]));
        var recentBatch = Assert.IsType<IngestionOperationsBatchDto>(recentBatchMethod.Invoke(null, [batch, null]));

        Assert.Equal(4, GetIntProperty(summary, "Review"));
        Assert.Equal(4, stages.Single(stage => stage.Key == "needs_review").Count);
        Assert.Equal(2, recentBatch.ReviewCount);
        Assert.Contains("2 review", recentBatch.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void OperationsService_RecentBatchStatusStaysRunningWhileBatchStagesAreActive()
    {
        var startedAt = DateTimeOffset.UtcNow.AddMinutes(-10);
        var batch = new IngestionBatch
        {
            Id = Guid.NewGuid(),
            StartedAt = startedAt,
            CreatedAt = startedAt,
            UpdatedAt = startedAt.AddMinutes(5),
            CompletedAt = startedAt.AddMinutes(6),
            Status = "completed",
            FilesTotal = 20,
            FilesProcessed = 20,
            FilesIdentified = 20,
        };
        var stageProgress = new List<IngestionStageProgressDto>
        {
            new()
            {
                StageNumber = 3,
                StageKey = "wikidata",
                CompletedFiles = 12,
                TotalFiles = 20,
                ActiveCount = 1,
                StatusLabel = "Active",
            },
        };
        var recentBatchMethod = typeof(IngestionOperationsStatusService).GetMethod(
            "ToRecentBatchWithStageProgress",
            BindingFlags.Static | BindingFlags.NonPublic);

        Assert.NotNull(recentBatchMethod);

        var recentBatch = Assert.IsType<IngestionOperationsBatchDto>(
            recentBatchMethod.Invoke(null, [batch, null, stageProgress]));

        Assert.Equal("running", recentBatch.Status);
        Assert.Null(recentBatch.CompletedAt);
        Assert.Single(recentBatch.StageProgress);
    }

    [Fact]
    public void OperationsService_RecentBatchStatusUsesCompletedWhenStageHasNoActiveWork()
    {
        var startedAt = DateTimeOffset.UtcNow.AddMinutes(-10);
        var completedAt = startedAt.AddMinutes(6);
        var batch = new IngestionBatch
        {
            Id = Guid.NewGuid(),
            StartedAt = startedAt,
            CreatedAt = startedAt,
            UpdatedAt = completedAt,
            CompletedAt = completedAt,
            Status = "completed",
            FilesTotal = 20,
            FilesProcessed = 20,
            FilesIdentified = 10,
            FilesReview = 10,
        };
        var stageProgress = new List<IngestionStageProgressDto>
        {
            new()
            {
                StageNumber = 3,
                StageKey = "wikidata",
                CompletedFiles = 19,
                TotalFiles = 20,
                ActiveCount = 0,
                QueuedCount = 0,
                StatusLabel = "In progress",
            },
        };
        var recentBatchMethod = typeof(IngestionOperationsStatusService).GetMethod(
            "ToRecentBatchWithStageProgress",
            BindingFlags.Static | BindingFlags.NonPublic);

        Assert.NotNull(recentBatchMethod);

        var recentBatch = Assert.IsType<IngestionOperationsBatchDto>(
            recentBatchMethod.Invoke(null, [batch, null, stageProgress]));

        Assert.Equal("completed", recentBatch.Status);
        Assert.Equal(completedAt, recentBatch.CompletedAt);
        Assert.Single(recentBatch.StageProgress);
    }

    [Fact]
    public void OperationsService_RecentBatchOrderingKeepsMeaningfulFullBatchFirst()
    {
        var startedAt = DateTimeOffset.UtcNow.AddMinutes(-10);
        var fullBatch = new IngestionBatch
        {
            Id = Guid.NewGuid(),
            StartedAt = startedAt,
            CreatedAt = startedAt,
            UpdatedAt = DateTimeOffset.UtcNow.AddSeconds(-30),
            Status = "running",
            FilesTotal = 97,
            FilesProcessed = 20,
            FilesIdentified = 10,
            FilesReview = 9,
        };
        var followOnBatch = new IngestionBatch
        {
            Id = Guid.NewGuid(),
            StartedAt = DateTimeOffset.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            Status = "running",
            FilesTotal = 4,
            FilesProcessed = 0,
        };
        var buildGroupsMethod = typeof(IngestionOperationsStatusService).GetMethod(
            "BuildRecentBatchGroups",
            BindingFlags.Static | BindingFlags.NonPublic);
        var orderGroupsMethod = typeof(IngestionOperationsStatusService).GetMethod(
            "OrderRecentBatchGroupsForDisplay",
            BindingFlags.Static | BindingFlags.NonPublic);

        Assert.NotNull(buildGroupsMethod);
        Assert.NotNull(orderGroupsMethod);

        var groups = buildGroupsMethod.Invoke(null, [new[] { followOnBatch, fullBatch }])
            ?? throw new InvalidOperationException("BuildRecentBatchGroups returned null.");
        var ordered = Assert.IsAssignableFrom<IEnumerable>(orderGroupsMethod.Invoke(null, [groups]))
            .Cast<object>()
            .ToList();
        var firstBatch = Assert.IsType<IngestionBatch>(
            ordered[0].GetType().GetProperty("Batch")?.GetValue(ordered[0]));

        Assert.Equal(fullBatch.Id, firstBatch.Id);
    }

    [Fact]
    public void OperationsService_SynthesizesActiveJobFromCurrentActivityWhenBatchTimestampIsStale()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepoRoot(),
            "src",
            "MediaEngine.Api",
            "Services",
            "IngestionOperationsStatusService.cs"));

        Assert.Contains("activeJobs.Count == 0", source, StringComparison.Ordinal);
        Assert.Contains("currentActivities.Any(IsActiveActivity)", source, StringComparison.Ordinal);
        Assert.Contains("activeJobs.Add(ToActiveJob(displayBatch, scopedPipelineRows, pipelineStages))", source, StringComparison.Ordinal);
    }

    [Fact]
    public void OperationsService_NumberedStageStatusTreatsTerminalPartialStageAsComplete()
    {
        var statusMethod = typeof(IngestionOperationsStatusService).GetMethod(
            "ResolveNumberedStageStatus",
            BindingFlags.Static | BindingFlags.NonPublic);

        Assert.NotNull(statusMethod);

        var status = Assert.IsType<string>(statusMethod.Invoke(null, [96d, 0, 0, 70, "wikidata", true]));

        Assert.Equal("Complete", status);
    }

    [Fact]
    public void OperationsService_ReviewReasonsComeOnlyFromPendingReviewQueueRows()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepoRoot(),
            "src",
            "MediaEngine.Api",
            "Services",
            "IngestionOperationsStatusService.cs"));

        Assert.Contains("ReviewReasons = BuildReviewReasons(reviewRows)", source, StringComparison.Ordinal);
        Assert.Contains("triggerCounts[row.Trigger]", source, StringComparison.Ordinal);
        Assert.DoesNotContain("BuildReviewReasons(reviewRows, lifecycle.TriggerCounts)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("foreach (var kv in triggerCounts)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("allCounts[row.Detail]", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SumContains(triggerCounts", source, StringComparison.Ordinal);
    }

    [Fact]
    public void OperationsService_CountsOnlyReviewReadyPendingRows()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepoRoot(),
            "src",
            "MediaEngine.Api",
            "Services",
            "IngestionOperationsStatusService.cs"));
        var progressRepositorySource = File.ReadAllText(Path.Combine(
            FindRepoRoot(),
            "src",
            "MediaEngine.Storage",
            "IngestionBatchRepository.cs"));

        Assert.Contains("review_ready_at IS NOT NULL", source, StringComparison.Ordinal);
        Assert.Contains("review_ready_at IS NOT NULL", progressRepositorySource, StringComparison.Ordinal);
        Assert.DoesNotContain("WHEN ma.status = 'Normal'", source, StringComparison.Ordinal);
        Assert.Contains("var processed = Math.Clamp(Math.Max(0, batch.FilesProcessed), 0, total);", source, StringComparison.Ordinal);
    }

    [Fact]
    public void BatchProgressService_DoesNotReusePersistedFailedCountWhenJobSnapshotExists()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepoRoot(),
            "src",
            "MediaEngine.Providers",
            "Services",
            "BatchProgressService.cs"));

        Assert.Contains("snapshot.TotalJobs > 0", source, StringComparison.Ordinal);
        Assert.Contains("snapshot.PipelineFailed", source, StringComparison.Ordinal);
        Assert.Contains("batch.FilesFailed", source, StringComparison.Ordinal);
        Assert.DoesNotContain("var failed = Math.Max(batch.FilesFailed, snapshot.PipelineFailed);", source, StringComparison.Ordinal);
    }

    [Fact]
    public void BatchCompletion_WaitsForDurableChildOperationsAndRunsWithoutADashboardClient()
    {
        var root = FindRepoRoot();
        var progressSource = File.ReadAllText(Path.Combine(
            root, "src", "MediaEngine.Providers", "Services", "BatchProgressService.cs"));
        var repositorySource = File.ReadAllText(Path.Combine(
            root, "src", "MediaEngine.Storage", "IngestionBatchRepository.cs"));
        var hostedServicesSource = File.ReadAllText(Path.Combine(
            root, "src", "MediaEngine.Api", "DependencyInjection", "TuvimaHostedServiceCollectionExtensions.cs"));
        var reconcilerSource = File.ReadAllText(Path.Combine(
            root, "src", "MediaEngine.Api", "Services", "IngestionBatchProgressHostedService.cs"));

        Assert.Contains("snapshot.OutstandingOperations == 0", progressSource, StringComparison.Ordinal);
        Assert.Contains("failed_retryable", repositorySource, StringComparison.Ordinal);
        Assert.Contains("AddHostedService<IngestionBatchProgressHostedService>()", hostedServicesSource, StringComparison.Ordinal);
        Assert.Contains("GetActiveAsync", reconcilerSource, StringComparison.Ordinal);
        Assert.Contains("EmitProgressAsync", reconcilerSource, StringComparison.Ordinal);
    }

    [Fact]
    public void ReviewEndpoints_UseReadyOnlyReviewQueueReadService()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepoRoot(),
            "src",
            "MediaEngine.Api",
            "Endpoints",
            "ReviewEndpoints.cs"));

        Assert.Contains("IReviewQueueReadService reviewReadService", source, StringComparison.Ordinal);
        Assert.Contains("reviewReadService.GetPendingAsync(paged.Limit, ct)", source, StringComparison.Ordinal);
        Assert.Contains("reviewReadService.GetPendingCountAsync(ct)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("libraryItemRepo.GetDetailAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Status: \"InReview\"", source, StringComparison.Ordinal);
        Assert.DoesNotContain("GetFourStateCountsAsync", source, StringComparison.Ordinal);
    }

    [Fact]
    public void OperationsSnapshot_ExposesBatchAwareCurrentActivityContract()
    {
        var dtoSource = File.ReadAllText(Path.Combine(
            FindRepoRoot(),
            "src",
            "MediaEngine.Contracts",
            "Ingestion",
            "IngestionOperationsDtos.cs"));
        var serviceSource = File.ReadAllText(Path.Combine(
            FindRepoRoot(),
            "src",
            "MediaEngine.Api",
            "Services",
            "IngestionOperationsStatusService.cs"));

        Assert.Contains("current_batch", dtoSource, StringComparison.Ordinal);
        Assert.Contains("sample_items", dtoSource, StringComparison.Ordinal);
        Assert.Contains("queued_count", dtoSource, StringComparison.Ordinal);
        Assert.Contains("count_unit", dtoSource, StringComparison.Ordinal);
        Assert.Contains("IngestionActivityBatchDto", dtoSource, StringComparison.Ordinal);
        Assert.Contains("ActivityBatchSize = 50", serviceSource, StringComparison.Ordinal);
        Assert.Contains("Linking Wikidata QIDs", serviceSource, StringComparison.Ordinal);
        Assert.Contains("BuildCurrentBatch", serviceSource, StringComparison.Ordinal);
        Assert.Contains("ActiveActivityFreshness", serviceSource, StringComparison.Ordinal);
        Assert.Contains("IsFreshActive", serviceSource, StringComparison.Ordinal);
        Assert.DoesNotContain("Metadata validation", serviceSource, StringComparison.Ordinal);
        Assert.DoesNotContain("\"validation\"", serviceSource, StringComparison.Ordinal);
        Assert.DoesNotContain("\"summary\" =>", serviceSource, StringComparison.Ordinal);
    }

    [Fact]
    public void OperationsSnapshot_ExposesUsefulProviderActivityTelemetry()
    {
        var dtoSource = File.ReadAllText(Path.Combine(
            FindRepoRoot(),
            "src",
            "MediaEngine.Contracts",
            "Ingestion",
            "IngestionOperationsDtos.cs"));
        var serviceSource = File.ReadAllText(Path.Combine(
            FindRepoRoot(),
            "src",
            "MediaEngine.Api",
            "Services",
            "IngestionOperationsStatusService.cs"));
        var broadcastSource = File.ReadAllText(Path.Combine(
            FindRepoRoot(),
            "src",
            "MediaEngine.Api",
            "Services",
            "ProviderActivityBroadcastService.cs"));

        foreach (var jsonName in new[]
        {
            "waiting_requests",
            "max_active_last_minute",
            "wait_ms_last_minute",
            "average_wait_ms",
            "last_success_at",
        })
        {
            Assert.Contains(jsonName, dtoSource, StringComparison.Ordinal);
        }

        Assert.Contains("WaitingRequests = activity.WaitingRequests", serviceSource, StringComparison.Ordinal);
        Assert.Contains("MaxActiveLastMinute = activity.MaxActiveLastMinute", serviceSource, StringComparison.Ordinal);
        Assert.Contains("WaitMsLastMinute = activity.WaitMsLastMinute", serviceSource, StringComparison.Ordinal);
        Assert.Contains("AverageWaitMs = activity.AverageWaitMs", serviceSource, StringComparison.Ordinal);
        Assert.Contains("LastSuccessAt = activity.LastSuccessAt", serviceSource, StringComparison.Ordinal);
        Assert.Contains("snapshot.WaitingRequests > 0", broadcastSource, StringComparison.Ordinal);
        Assert.Contains("snapshot.WaitMsLastMinute", broadcastSource, StringComparison.Ordinal);
        Assert.Contains("snapshot.LastSuccessAt", broadcastSource, StringComparison.Ordinal);
    }

    [Fact]
    public void OperationsSnapshot_ExposesNumberedStageProgressContract()
    {
        var dtoSource = File.ReadAllText(Path.Combine(
            FindRepoRoot(),
            "src",
            "MediaEngine.Contracts",
            "Ingestion",
            "IngestionOperationsDtos.cs"));
        var serviceSource = File.ReadAllText(Path.Combine(
            FindRepoRoot(),
            "src",
            "MediaEngine.Api",
            "Services",
            "IngestionOperationsStatusService.cs"));

        Assert.Contains("stage_progress", dtoSource, StringComparison.Ordinal);
        Assert.Contains("stage_number", dtoSource, StringComparison.Ordinal);
        Assert.Contains("active_group_label", dtoSource, StringComparison.Ordinal);
        Assert.Contains("label_accuracy", dtoSource, StringComparison.Ordinal);
        Assert.Contains("artifact_count", dtoSource, StringComparison.Ordinal);
        Assert.Contains("detail_items", dtoSource, StringComparison.Ordinal);
        Assert.Contains("IngestionStageDetailItemDto", dtoSource, StringComparison.Ordinal);
        Assert.Contains("public List<IngestionStageProgressDto> StageProgress", dtoSource, StringComparison.Ordinal);
        Assert.Contains("BuildNumberedStageProgressAsync", serviceSource, StringComparison.Ordinal);
        Assert.Contains("BuildRecentBatchDtosAsync", serviceSource, StringComparison.Ordinal);
        Assert.Contains("DetailItems(", serviceSource, StringComparison.Ordinal);
        Assert.Contains("\"Matches\"", serviceSource, StringComparison.Ordinal);
        Assert.Contains("\"Cover art assets\"", serviceSource, StringComparison.Ordinal);
        Assert.Contains("\"Relevant QIDs\"", serviceSource, StringComparison.Ordinal);
        Assert.Contains("\"Unresolved\"", serviceSource, StringComparison.Ordinal);
        Assert.Contains("RetailUnresolvedCount", serviceSource, StringComparison.Ordinal);
        Assert.Contains("WikidataUnresolvedCount", serviceSource, StringComparison.Ordinal);
        Assert.Contains("DetailTerminalSort", serviceSource, StringComparison.Ordinal);
        Assert.Contains("\"Relationship links\"", serviceSource, StringComparison.Ordinal);
        Assert.Contains("\"Retail Match\"", serviceSource, StringComparison.Ordinal);
        Assert.Contains("\"Universes\"", serviceSource, StringComparison.Ordinal);
        Assert.Contains("\"Artwork\"", serviceSource, StringComparison.Ordinal);
        Assert.Contains("\"Resolving Wikidata batch:", serviceSource, StringComparison.Ordinal);
        Assert.Contains("\"GroupedLookup\"", serviceSource, StringComparison.Ordinal);
        Assert.DoesNotContain("activeGroup is null ? \"", serviceSource, StringComparison.Ordinal);
    }

    [Fact]
    public void CurrentSchema_IncludesIngestionBatchArtifactLedger()
    {
        var schema = File.ReadAllText(Path.Combine(
            FindRepoRoot(),
            "src",
            "MediaEngine.Storage",
            "Schema",
            "schema.sql"));

        Assert.Contains("CREATE TABLE IF NOT EXISTS ingestion_batch_artifacts", schema, StringComparison.Ordinal);
        Assert.Contains("artifact_type", schema, StringComparison.Ordinal);
        Assert.Contains("parent_entity_id", schema, StringComparison.Ordinal);
        Assert.Contains("provider_id", schema, StringComparison.Ordinal);
        Assert.Contains("detail_json", schema, StringComparison.Ordinal);
        Assert.Contains("idx_ingestion_batch_artifacts_batch", schema, StringComparison.Ordinal);
        Assert.DoesNotContain("ALTER TABLE ingestion_batch_artifacts", schema, StringComparison.OrdinalIgnoreCase);

        var storageRegistrations = File.ReadAllText(Path.Combine(
            FindRepoRoot(),
            "src",
            "MediaEngine.Api",
            "DependencyInjection",
            "TuvimaStorageServiceCollectionExtensions.cs"));
        Assert.Contains(
            "IIngestionBatchArtifactRepository",
            storageRegistrations,
            StringComparison.Ordinal);
    }

    [Fact]
    public void OperationsSnapshot_UsesDurableOperationsAndRealWorkerArtifactCounts()
    {
        var serviceSource = File.ReadAllText(Path.Combine(
            FindRepoRoot(),
            "src",
            "MediaEngine.Api",
            "Services",
            "IngestionOperationsStatusService.cs"));

        Assert.Contains("ReadOperationProgressAsync", serviceSource, StringComparison.Ordinal);
        Assert.Contains("ReadOperationProgressAsync(conn, identityBatchIdValues, hasBatchScope)", serviceSource, StringComparison.Ordinal);
        Assert.Contains("MediaOperationType.IdentityWikidataBridge", serviceSource, StringComparison.Ordinal);
        Assert.Contains("MediaOperationType.EnrichmentCoverArt", serviceSource, StringComparison.Ordinal);
        Assert.Contains("ReadPeopleProgressAsync", serviceSource, StringComparison.Ordinal);
        Assert.Contains("ReadRelationshipsProgressAsync", serviceSource, StringComparison.Ordinal);
        Assert.Contains("ReadArtworkProgressAsync", serviceSource, StringComparison.Ordinal);
        Assert.Contains("IsOpenEndedDiscoveryTask(taskKey", serviceSource, StringComparison.Ordinal);
        Assert.Contains("taskKey.Equals(\"relationships\"", serviceSource, StringComparison.Ordinal);
        Assert.Contains("countUnit: \"artwork assets\"", serviceSource, StringComparison.Ordinal);
        Assert.Contains("BuildOpenEndedOperationProgressOverride(wikidataOperation, linkedQids, \"QIDs\")", serviceSource, StringComparison.Ordinal);
        Assert.Contains("countUnit: \"people\"", serviceSource, StringComparison.Ordinal);
        Assert.Contains("CountUnit: \"links\"", serviceSource, StringComparison.Ordinal);
        Assert.Contains("processed,\n            0,", serviceSource.Replace("\r\n", "\n", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Contains("stage3_enriched_at", serviceSource, StringComparison.Ordinal);
        Assert.Contains("p.enriched_at", serviceSource, StringComparison.Ordinal);
        Assert.Contains("FROM entity_assets", serviceSource, StringComparison.Ordinal);
        Assert.Contains("FROM persons", serviceSource, StringComparison.Ordinal);
        Assert.Contains("FROM series_manifest_items", serviceSource, StringComparison.Ordinal);
        Assert.Contains("TaskProgressOverride", serviceSource, StringComparison.Ordinal);
        Assert.DoesNotContain("ReadOperationProgressAsync(conn, textBatchIdValues, hasBatchScope)", serviceSource, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Wikidata matching", "wikidata")]
    [InlineData("BridgeSearching", "wikidata")]
    [InlineData("Retail identification", "retail")]
    [InlineData("RetailSearching", "retail")]
    [InlineData("UniverseEnriching", "enrichment")]
    public void OperationsService_MapsActivityLabelsToCorrectProgressStage(string label, string expectedKey)
    {
        var method = typeof(IngestionOperationsStatusService).GetMethod(
            "ResolveActivityStageKey",
            BindingFlags.Static | BindingFlags.NonPublic);

        Assert.NotNull(method);
        var actual = Assert.IsType<string>(method.Invoke(null, [label]));
        Assert.Equal(expectedKey, actual);
    }

    [Fact]
    public void OperationsService_DoesNotReportRetailCompleteAsActiveStageWhenWikidataIsPending()
    {
        var method = typeof(IngestionOperationsStatusService).GetMethod(
            "ResolveBatchStage",
            BindingFlags.Static | BindingFlags.NonPublic);

        var batch = new IngestionBatch
        {
            FilesTotal = 43,
            FilesProcessed = 43,
            FilesIdentified = 28,
            FilesReview = 14,
            Status = "running",
        };
        var pipelineRows = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["RetailMatched"] = 31,
            ["RetailMatchedNeedsReview"] = 11,
        };
        var stages = new List<IngestionPipelineStageDto>
        {
            Stage("detected", 43, 43),
            Stage("matched", 29, 43),
            Stage("retail_review", 13, 43),
            Stage("duplicate", 1, 43),
            Stage("canonicalized", 0, 31),
            Stage("wikidata_review", 0, 31),
            Stage("enriched", 0, 31),
        };

        Assert.NotNull(method);
        var actual = Assert.IsType<string>(method.Invoke(null, [batch, pipelineRows, stages]));
        Assert.Equal("Wikidata matching", actual);
    }

    [Fact]
    public void OperationsService_TreatsTerminalFailuresAsRetailActivityProgress()
    {
        var progress = ResolveActivityProgress("retail",
        [
            Stage("matched", 91, 117),
            Stage("retail_review", 6, 117),
            Stage("duplicate", 1, 117),
            Stage("failed", 19, 117),
        ]);

        Assert.Equal(117, progress.Count);
        Assert.Equal(117, progress.Total);
    }

    [Fact]
    public void OperationsService_TreatsWikidataTerminalReviewAsEnrichmentActivityProgress()
    {
        var progress = ResolveActivityProgress("enrichment",
        [
            Stage("enriched", 90, 91),
            Stage("wikidata_review", 1, 91),
        ]);

        Assert.Equal(91, progress.Count);
        Assert.Equal(91, progress.Total);
    }

    [Fact]
    public void OperationsService_AggregatesCoStartedBatchesForFullScanProgress()
    {
        var started = DateTimeOffset.UtcNow.AddMinutes(-10);
        var batches = new List<IngestionBatch>
        {
            Batch(started.AddSeconds(20), total: 12, processed: 12, identified: 8, review: 4),
            Batch(started.AddSeconds(10), total: 56, processed: 54, identified: 50, review: 4),
            Batch(started.AddSeconds(70), total: 28, processed: 20, identified: 18, review: 2),
            Batch(started.AddHours(-3), total: 2, processed: 2, identified: 2, review: 0),
        };

        var selectMethod = typeof(IngestionOperationsStatusService).GetMethod(
            "SelectDisplayBatches",
            BindingFlags.Static | BindingFlags.NonPublic);
        var aggregateMethod = typeof(IngestionOperationsStatusService).GetMethod(
            "AggregateDisplayBatch",
            BindingFlags.Static | BindingFlags.NonPublic);
        var recentGroupMethod = typeof(IngestionOperationsStatusService).GetMethod(
            "BuildRecentBatchGroups",
            BindingFlags.Static | BindingFlags.NonPublic);

        Assert.NotNull(selectMethod);
        Assert.NotNull(aggregateMethod);
        Assert.NotNull(recentGroupMethod);

        var selected = Assert.IsAssignableFrom<IReadOnlyList<IngestionBatch>>(
            selectMethod.Invoke(null, [batches]));
        var aggregate = Assert.IsType<IngestionBatch>(
            aggregateMethod.Invoke(null, [selected]));
        var recentGroups = Assert.IsAssignableFrom<IEnumerable>(
            recentGroupMethod.Invoke(null, [batches]));
        var groupObjects = recentGroups.Cast<object>().ToList();
        var firstGroupBatch = Assert.IsType<IngestionBatch>(
            groupObjects[0].GetType().GetProperty("Batch")!.GetValue(groupObjects[0]));
        var firstSourceBatchIds = Assert.IsAssignableFrom<IEnumerable>(
            groupObjects[0].GetType().GetProperty("SourceBatchIds")!.GetValue(groupObjects[0]));

        Assert.Equal(3, selected.Count);
        Assert.Equal(96, aggregate.FilesTotal);
        Assert.Equal(86, aggregate.FilesProcessed);
        Assert.Equal(76, aggregate.FilesIdentified);
        Assert.Equal(10, aggregate.FilesReview);
        Assert.Equal("Multiple source folders", aggregate.SourcePath);
        Assert.Equal("Mixed", aggregate.Category);
        Assert.Equal(2, groupObjects.Count);
        Assert.Equal(96, firstGroupBatch.FilesTotal);
        Assert.Equal(3, firstSourceBatchIds.Cast<object>().Count());
    }

    [Fact]
    public void OperationsService_PreservesCompletedFullRunSummaryOverTinyFollowOnBatch()
    {
        var started = DateTimeOffset.UtcNow.AddMinutes(-30);
        var fullRun = new List<IngestionBatch>
        {
            Batch(started.AddSeconds(10), total: 31, processed: 31, identified: 26, review: 5),
            Batch(started.AddSeconds(20), total: 28, processed: 28, identified: 23, review: 5),
            Batch(started.AddSeconds(40), total: 38, processed: 38, identified: 31, review: 7),
        };
        var tinyFollowOn = Batch(
            DateTimeOffset.UtcNow.AddMinutes(-1),
            total: 4,
            processed: 4,
            identified: 4,
            review: 0);
        tinyFollowOn.Category = "Movies";

        var selectMethod = typeof(IngestionOperationsStatusService).GetMethod(
            "SelectDisplayBatches",
            BindingFlags.Static | BindingFlags.NonPublic);
        var aggregateMethod = typeof(IngestionOperationsStatusService).GetMethod(
            "AggregateDisplayBatch",
            BindingFlags.Static | BindingFlags.NonPublic);

        Assert.NotNull(selectMethod);
        Assert.NotNull(aggregateMethod);

        var selected = Assert.IsAssignableFrom<IReadOnlyList<IngestionBatch>>(
            selectMethod.Invoke(null, [new List<IngestionBatch> { tinyFollowOn }.Concat(fullRun).ToList()]));
        var aggregate = Assert.IsType<IngestionBatch>(
            aggregateMethod.Invoke(null, [selected]));

        Assert.Equal(3, selected.Count);
        Assert.Equal(97, aggregate.FilesTotal);
        Assert.Equal(97, aggregate.FilesProcessed);
        Assert.Equal(80, aggregate.FilesIdentified);
        Assert.Equal(17, aggregate.FilesReview);
        Assert.Equal("Mixed", aggregate.Category);
    }

    [Fact]
    public void OperationsService_ExcludesCompletedNoOutcomeRescansFromCurrentDisplayBatch()
    {
        var started = DateTimeOffset.UtcNow.AddMinutes(-10);
        var currentRun = Batch(started, total: 131, processed: 131, identified: 52, review: 27);
        currentRun.Status = "running";
        currentRun.CompletedAt = null;
        var noOutcomeRescan = new IngestionBatch
        {
            Id = Guid.NewGuid(),
            StartedAt = started.AddMinutes(2),
            CreatedAt = started.AddMinutes(2),
            UpdatedAt = started.AddMinutes(3),
            CompletedAt = started.AddMinutes(3),
            Status = "completed",
            SourcePath = "Multiple source folders",
            FilesTotal = 152,
            FilesProcessed = 152,
            FilesIdentified = 0,
            FilesReview = 0,
            FilesNoMatch = 0,
            FilesFailed = 0,
        };
        var activeNoOutcomeRescan = new IngestionBatch
        {
            Id = Guid.NewGuid(),
            StartedAt = started.AddMinutes(1),
            CreatedAt = started.AddMinutes(1),
            UpdatedAt = DateTimeOffset.UtcNow,
            Status = "running",
            SourcePath = "Multiple source folders",
            FilesTotal = 149,
            FilesProcessed = 149,
            FilesIdentified = 0,
            FilesReview = 0,
            FilesNoMatch = 0,
            FilesFailed = 0,
        };

        var selectMethod = typeof(IngestionOperationsStatusService).GetMethod(
            "SelectDisplayBatches",
            BindingFlags.Static | BindingFlags.NonPublic);
        var aggregateMethod = typeof(IngestionOperationsStatusService).GetMethod(
            "AggregateDisplayBatch",
            BindingFlags.Static | BindingFlags.NonPublic);
        var recentGroupMethod = typeof(IngestionOperationsStatusService).GetMethod(
            "BuildRecentBatchGroups",
            BindingFlags.Static | BindingFlags.NonPublic);

        Assert.NotNull(selectMethod);
        Assert.NotNull(aggregateMethod);
        Assert.NotNull(recentGroupMethod);

        var selected = Assert.IsAssignableFrom<IReadOnlyList<IngestionBatch>>(
            selectMethod.Invoke(null, [new List<IngestionBatch> { noOutcomeRescan, activeNoOutcomeRescan, currentRun }]));
        var aggregate = Assert.IsType<IngestionBatch>(
            aggregateMethod.Invoke(null, [selected]));
        var recentGroups = Assert.IsAssignableFrom<IEnumerable>(
            recentGroupMethod.Invoke(null, [new List<IngestionBatch> { noOutcomeRescan, activeNoOutcomeRescan, currentRun }]));
        var recentGroupBatches = recentGroups
            .Cast<object>()
            .Select(group => Assert.IsType<IngestionBatch>(group.GetType().GetProperty("Batch")!.GetValue(group)))
            .ToList();

        Assert.Single(selected);
        Assert.Equal(131, aggregate.FilesTotal);
        Assert.Equal(131, aggregate.FilesProcessed);
        Assert.Equal(52, aggregate.FilesIdentified);
        Assert.Equal(27, aggregate.FilesReview);
        Assert.Contains(recentGroupBatches, batch => batch.FilesTotal == 131);
        Assert.DoesNotContain(recentGroupBatches, batch => batch.FilesTotal == 283);
        Assert.DoesNotContain(recentGroupBatches, batch => batch.FilesTotal == 280);
    }

    [Fact]
    public void IngestionEndpoints_RecentBatchesHideCompletedNoOutcomeScans()
    {
        var completedNoOutcomeScan = new IngestionBatch
        {
            Id = Guid.NewGuid(),
            Status = "completed",
            FilesTotal = 149,
            FilesProcessed = 149,
        };
        var activeNoOutcomeScan = new IngestionBatch
        {
            Id = Guid.NewGuid(),
            Status = "running",
            FilesTotal = 149,
            FilesProcessed = 40,
        };
        var completedOutcomeRun = new IngestionBatch
        {
            Id = Guid.NewGuid(),
            Status = "completed",
            FilesTotal = 131,
            FilesProcessed = 131,
            FilesIdentified = 89,
        };

        Assert.False(IngestionBatchEndpointMapper.ShouldShowInRecentBatches(completedNoOutcomeScan));
        Assert.True(IngestionBatchEndpointMapper.ShouldShowInRecentBatches(activeNoOutcomeScan));
        Assert.True(IngestionBatchEndpointMapper.ShouldShowInRecentBatches(completedOutcomeRun));
    }

    [Fact]
    public void IngestionEndpoints_RecentBatchesProjectTerminalLifecycleCounters()
    {
        var endpointSource = File.ReadAllText(Path.Combine(
            FindRepoRoot(),
            "src",
            "MediaEngine.Api",
            "Endpoints",
            "IngestionEndpoints.cs"));
        var serviceSource = File.ReadAllText(Path.Combine(
            FindRepoRoot(),
            "src",
            "MediaEngine.Api",
            "Services",
            "IngestionBatchResponseService.cs"));
        var operationsSource = File.ReadAllText(Path.Combine(
            FindRepoRoot(),
            "src",
            "MediaEngine.Api",
            "Services",
            "IngestionOperationsStatusService.cs"));

        Assert.Contains("IIngestionBatchResponseService batchResponses", endpointSource, StringComparison.Ordinal);
        Assert.Contains("ToResponseAsync(batch, ct)", serviceSource, StringComparison.Ordinal);
        Assert.Contains("ReadTerminalSnapshotAsync", serviceSource, StringComparison.Ordinal);
        Assert.Contains("review_ready_at IS NOT NULL", serviceSource, StringComparison.Ordinal);
        Assert.Matches(@"FilesProcessed\s*=\s*terminal", serviceSource);
        Assert.Contains("GuidSql.ToBlob(batchId)", serviceSource, StringComparison.Ordinal);
        Assert.Contains("ProjectRecentBatchesForDisplayAsync", operationsSource, StringComparison.Ordinal);
        Assert.Contains("ProjectBatchForDisplay(batch, snapshot)", operationsSource, StringComparison.Ordinal);
        Assert.Contains("OperationSkipped", operationsSource, StringComparison.Ordinal);
        Assert.Contains("OperationOnlyTerminal", serviceSource, StringComparison.Ordinal);
        Assert.Contains("OperationOnlyTerminal", operationsSource, StringComparison.Ordinal);
        Assert.Contains("FileOperationsTerminal - TotalJobs", serviceSource, StringComparison.Ordinal);
        Assert.Contains("OperationTerminal", operationsSource, StringComparison.Ordinal);
        Assert.Contains("batchId = GuidSql.ToBlob(batchId)", operationsSource, StringComparison.Ordinal);
        Assert.Matches(@"FilesProcessed\s*=\s*terminal", operationsSource);
        Assert.DoesNotContain(".Select(IngestionBatchEndpointMapper.ToResponse)", endpointSource, StringComparison.Ordinal);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MediaEngine.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Repo root not found.");
    }

    private static IngestionPipelineStageDto Stage(string key, int count, int total) => new()
    {
        Key = key,
        Count = count,
        TotalCount = total,
    };

    private static IngestionBatch Batch(
        DateTimeOffset startedAt,
        int total,
        int processed,
        int identified,
        int review) => new()
        {
            Id = Guid.NewGuid(),
            StartedAt = startedAt,
            CreatedAt = startedAt,
            UpdatedAt = startedAt.AddMinutes(1),
            CompletedAt = startedAt.AddMinutes(2),
            Status = "completed",
            FilesTotal = total,
            FilesProcessed = processed,
            FilesIdentified = identified,
            FilesReview = review,
        };

    private static (int Count, int Total) ResolveActivityProgress(
        string stageKey,
        IReadOnlyList<IngestionPipelineStageDto> stages)
    {
        var method = typeof(IngestionOperationsStatusService).GetMethod(
            "ResolveActivityProgress",
            BindingFlags.Static | BindingFlags.NonPublic);

        Assert.NotNull(method);
        var actual = Assert.IsType<ValueTuple<int, int>>(method.Invoke(null, [stageKey, stages]));
        return actual;
    }

    private static int GetIntProperty(object source, string propertyName)
    {
        var value = source.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?.GetValue(source);
        return Assert.IsType<int>(value);
    }
}
