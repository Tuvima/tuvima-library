using System.Globalization;
using MediaEngine.Contracts.Realtime;
using MediaEngine.Domain.Services;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Formatting;
using MediaEngine.Web.Services.Ui;

namespace MediaEngine.Web.Services.Integration;

/// <summary>
/// Pure presentation projections for ingestion snapshots, stages, metrics, and status copy.
/// </summary>
public sealed partial class IngestionLiveDashboardState
{
    public static IReadOnlyList<IngestionOperationsJobDto> BuildActiveJobs(
        IngestionOperationsSnapshotDto? snapshot,
        UniverseStateContainer stateContainer)
    {
        var jobs = snapshot?.ActiveJobs.ToList() ?? [];
        var batch = stateContainer.BatchProgress;
        if (batch is { IsComplete: false })
        {
            var existing = jobs.FirstOrDefault(job => job.JobId == batch.BatchId);
            var liveJob = new IngestionOperationsJobDto
            {
                JobId = batch.BatchId,
                JobType = "Ingestion batch",
                MediaType = "Mixed",
                SourceFolder = "Watch folders",
                CurrentStage = StringHelpers.FirstNonBlankOr("", batch.CurrentStage, batch.LifecycleStage, "Processing"),
                CurrentItem = StringHelpers.FirstNonBlankOr("", batch.CurrentFileTitle, batch.RecentTitles?.FirstOrDefault()),
                ProcessedCount = batch.FilesProcessed,
                TotalCount = batch.FilesTotal,
                PercentComplete = batch.ProgressPercent,
                Status = "running",
                Elapsed = existing?.Elapsed,
                LastUpdatedTime = DateTimeOffset.UtcNow,
                WarningSummary = batch.FilesFailed > 0
                    ? $"{batch.FilesFailed.ToString("N0", CultureInfo.CurrentCulture)} failed"
                    : null,
            };

            UpsertLiveJob(jobs, liveJob);
        }

        var liveItemJob = BuildLiveItemJob(snapshot, stateContainer);
        if (liveItemJob is not null)
        {
            UpsertLiveJob(jobs, liveItemJob);
        }

        var progress = stateContainer.IngestionProgress;
        if (progress is not null && !progress.Stage.Equals("Complete", StringComparison.OrdinalIgnoreCase))
        {
            jobs.Insert(0, new IngestionOperationsJobDto
            {
                JobId = Guid.Empty,
                JobType = "Folder scan",
                MediaType = "Watched folders",
                SourceFolder = "Watch folders",
                CurrentStage = progress.Stage,
                CurrentItem = progress.CurrentFile,
                ProcessedCount = progress.ProcessedCount,
                TotalCount = progress.TotalCount,
                PercentComplete = progress.TotalCount > 0 ? progress.ProcessedCount * 100d / progress.TotalCount : 0,
                Status = "running",
                LastUpdatedTime = DateTimeOffset.UtcNow,
            });
        }

        return jobs
            .GroupBy(job => job.JobId)
            .Select(group => group.OrderByDescending(job => job.LastUpdatedTime ?? DateTimeOffset.MinValue).First())
            .OrderBy(JobSortKey)
            .ThenBy(job => job.JobType, StringComparer.OrdinalIgnoreCase)
            .ThenBy(job => job.MediaType, StringComparer.OrdinalIgnoreCase)
            .ThenBy(job => job.JobId)
            .ToList();
    }

    private static void UpsertLiveJob(
        List<IngestionOperationsJobDto> jobs,
        IngestionOperationsJobDto liveJob)
    {
        var index = jobs.FindIndex(job => job.JobId == liveJob.JobId);
        if (index >= 0)
        {
            jobs[index] = liveJob;
        }
        else
        {
            jobs.Insert(0, liveJob);
        }
    }

    private static IngestionOperationsJobDto? BuildLiveItemJob(
        IngestionOperationsSnapshotDto? snapshot,
        UniverseStateContainer stateContainer)
    {
        var items = FreshIngestionItemProgress(stateContainer).ToList();
        if (items.Count == 0)
        {
            return null;
        }

        var activeItems = items
            .Where(item => !IsFileIngestionTerminal(item.Event))
            .ToList();
        if (activeItems.Count == 0)
        {
            return null;
        }

        var latest = activeItems
            .OrderByDescending(item => item.ReceivedAt)
            .First();
        var batch = stateContainer.BatchProgress is { IsComplete: false } liveBatch
            && liveBatch.BatchId == latest.Event.BatchId
                ? liveBatch
                : null;
        var batchId = latest.Event.BatchId;
        var batchItems = items
            .Where(item => item.Event.BatchId == batchId)
            .ToList();
        var total = ResolveLiveItemTotal(snapshot, batch, batchItems);
        var completed = batchItems.Count(item => IsFileIngestionTerminal(item.Event));
        var percent = ResolveLiveItemPercent(batchItems, total);
        var stage = FriendlyStageName(latest.Event.Stage);

        return new IngestionOperationsJobDto
        {
            JobId = batchId,
            JobType = "Reading files",
            MediaType = StringHelpers.FirstNonBlankOr("", latest.Event.MediaType, "Mixed"),
            SourceFolder = "Watch folders",
            CurrentStage = stage,
            CurrentItem = CleanDisplayTitle(ResolveItemTitle(latest.Event)),
            ProcessedCount = completed,
            TotalCount = total,
            PercentComplete = percent,
            Status = "running",
            LastUpdatedTime = latest.ReceivedAt,
        };
    }

    public static IngestionDashboardMetrics BuildMetrics(
        IngestionOperationsSnapshotDto? snapshot,
        IReadOnlyList<IngestionOperationsJobDto> activeJobs)
    {
        var activeTotal = activeJobs.Sum(job => Math.Max(0, job.TotalCount));
        var totalFiles = Total(snapshot, "detected", snapshot?.Summary.TotalItems ?? activeTotal);
        var processedFiles = ResolveTerminalPipelineCount(snapshot, totalFiles);

        return new IngestionDashboardMetrics(
            totalFiles,
            processedFiles,
            activeJobs.Count > 0 ? activeJobs.Count : snapshot?.CurrentActivities.Count ?? 0,
            snapshot?.Summary.ItemsNeedingReview ?? 0);
    }

    public static IReadOnlyList<IngestionCurrentActivityDto> BuildCurrentActivities(
        IngestionOperationsSnapshotDto? snapshot,
        IReadOnlyList<IngestionOperationsJobDto> activeJobs,
        IReadOnlyList<IngestionDashboardStage> stages,
        UniverseStateContainer? stateContainer = null)
    {
        var activities = snapshot?.CurrentActivities
            .Where(activity => !string.IsNullOrWhiteSpace(activity.Message))
            .ToList() ?? [];
        var liveItemActivity = BuildLiveItemActivity(stateContainer);
        var liveBatchActivity = BuildLiveBatchActivity(stateContainer, activeJobs, stages);
        var liveUniverseActivity = BuildLiveUniverseActivity(stateContainer);

        if (liveBatchActivity is not null)
        {
            UpsertCurrentActivity(activities, liveBatchActivity);
        }

        if (liveItemActivity is not null)
        {
            UpsertCurrentActivity(activities, liveItemActivity);
        }

        if (liveUniverseActivity is not null)
        {
            UpsertCurrentActivity(activities, liveUniverseActivity);
        }

        if (activities.Count > 0)
        {
            return activities;
        }

        return activeJobs
            .Where(job => job.Status.Equals("running", StringComparison.OrdinalIgnoreCase)
                || job.Status.Equals("processing", StringComparison.OrdinalIgnoreCase)
                || job.Status.Equals("active", StringComparison.OrdinalIgnoreCase))
            .Select(job => ToCurrentActivity(job, stages))
            .Where(activity => !string.IsNullOrWhiteSpace(activity.Message))
            .ToList();
    }

    private static IngestionCurrentActivityDto? BuildLiveItemActivity(UniverseStateContainer? stateContainer)
    {
        if (stateContainer is null)
        {
            return null;
        }

        var items = FreshIngestionItemProgress(stateContainer).ToList();
        if (items.Count == 0)
        {
            return null;
        }

        var activeItems = items
            .Where(item => !IsFileIngestionTerminal(item.Event))
            .OrderByDescending(item => item.ReceivedAt)
            .ToList();
        if (activeItems.Count == 0)
        {
            return null;
        }

        var latest = activeItems[0];
        var batch = stateContainer.BatchProgress is { IsComplete: false } liveBatch
            && liveBatch.BatchId == latest.Event.BatchId
                ? liveBatch
                : null;
        var batchId = latest.Event.BatchId;
        var batchItems = items
            .Where(item => item.Event.BatchId == batchId)
            .ToList();
        var completedItems = batchItems
            .Where(item => IsFileIngestionTerminal(item.Event))
            .OrderByDescending(item => item.ReceivedAt)
            .ToList();
        var total = ResolveLiveItemTotal(null, batch, batchItems);
        var completed = completedItems.Count;
        var active = activeItems.Count(item => item.Event.BatchId == batchId);
        var queued = total > 0 ? Math.Max(0, total - completed - active) : 0;
        var percent = ResolveLiveItemPercent(batchItems, total);
        var stageKey = ResolveItemStageKey(latest.Event.Stage);
        var stageLabel = FriendlyStageName(latest.Event.Stage);
        var activeLabels = activeItems
            .Where(item => item.Event.BatchId == batchId)
            .Select(item => CleanDisplayTitle(ResolveItemTitle(item.Event)))
            .Where(title => !string.IsNullOrWhiteSpace(title))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(10)
            .ToList();
        var completedLabels = completedItems
            .Select(item => CleanDisplayTitle(ResolveItemTitle(item.Event)))
            .Where(title => !string.IsNullOrWhiteSpace(title))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(10)
            .ToList();

        return new IngestionCurrentActivityDto
        {
            StageKey = stageKey,
            Message = ResolveItemActivityMessage(stageKey),
            Detail = stageLabel,
            CurrentItem = CleanDisplayTitle(ResolveItemTitle(latest.Event)),
            Source = "Watch folders",
            ProcessedCount = completed,
            TotalCount = total,
            CountUnit = "files",
            PercentComplete = percent,
            LastUpdatedTime = latest.ReceivedAt,
            QueuedCount = queued,
            ActiveCount = Math.Max(1, active),
            SampleItems = activeLabels.Count > 0 ? activeLabels : [CleanDisplayTitle(ResolveItemTitle(latest.Event))],
            MetricLabel = "Current stage",
            MetricValue = stageLabel,
            MetricTone = "info",
            CurrentBatch = new IngestionActivityBatchDto
            {
                BatchNumber = 1,
                BatchSize = total,
                TotalBatches = 1,
                CompletedCount = completed,
                ActiveCount = Math.Max(1, active),
                PendingCount = queued,
                ReviewCount = completedItems.Count(item => IsReviewTerminalStage(item.Event.Stage)),
                ActiveItems = activeLabels,
                CompletedPreview = completedLabels,
                PendingPreview = [],
                ReviewPreview = completedItems
                    .Where(item => IsReviewTerminalStage(item.Event.Stage))
                    .Select(item => CleanDisplayTitle(ResolveItemTitle(item.Event)))
                    .Where(title => !string.IsNullOrWhiteSpace(title))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(10)
                    .ToList(),
            },
        };
    }

    private static IngestionCurrentActivityDto? BuildLiveBatchActivity(
        UniverseStateContainer? stateContainer,
        IReadOnlyList<IngestionOperationsJobDto> activeJobs,
        IReadOnlyList<IngestionDashboardStage> stages)
    {
        var batch = stateContainer?.BatchProgress;
        if (batch is not { IsComplete: false })
        {
            return null;
        }

        var job = activeJobs.FirstOrDefault(candidate => candidate.JobId == batch.BatchId)
            ?? new IngestionOperationsJobDto
            {
                JobId = batch.BatchId,
                JobType = "Ingestion batch",
                MediaType = "Mixed",
                SourceFolder = "Watch folders",
                CurrentStage = StringHelpers.FirstNonBlankOr("", batch.CurrentStage, batch.LifecycleStage, "Processing"),
                CurrentItem = StringHelpers.FirstNonBlankOr("", batch.CurrentFileTitle, batch.RecentTitles?.FirstOrDefault()),
                ProcessedCount = batch.FilesProcessed,
                TotalCount = batch.FilesTotal,
                PercentComplete = batch.ProgressPercent,
                Status = "running",
                LastUpdatedTime = DateTimeOffset.UtcNow,
            };

        var activity = ToCurrentActivity(job, stages);
        if (activity.StageKey.Equals("retail", StringComparison.OrdinalIgnoreCase)
            && IsQuickMetadataStage(StringHelpers.FirstNonBlankOr("", batch.CurrentStage, batch.LifecycleStage)))
        {
            activity.Message = "Retail Match";
            activity.Detail = "Adding retail metadata and primary artwork";
        }
        else if (activity.StageKey.Equals("enrichment", StringComparison.OrdinalIgnoreCase))
        {
            var batchStage = StringHelpers.FirstNonBlankOr("", batch.CurrentStage, batch.LifecycleStage);
            var friendlyStage = ToFriendlyStepLabel(batchStage);
            if (IsQuickMetadataStage(batchStage))
            {
                activity.StageKey = "retail";
                activity.Message = "Retail Match";
                activity.Detail = StringHelpers.FirstNonBlankOr("", friendlyStage, activity.Detail, "Adding retail metadata and primary artwork");
            }
            else
            {
                activity.StageKey = "relationships";
                activity.Message = "Relationships";
                activity.Detail = StringHelpers.FirstNonBlankOr("", friendlyStage, activity.Detail, "Stage 3 enrichment");
            }

            activity.CountUnit = "items";
        }

        var batchActive = Math.Max(0, batch.FilesActive);
        var batchQueued = Math.Max(0, batch.FilesQueued);
        activity.ActiveCount = Math.Max(activity.ActiveCount, batchActive);
        if (batchQueued > 0 || batchActive > 0)
        {
            activity.QueuedCount = batchQueued;
        }

        activity.LastUpdatedTime = DateTimeOffset.UtcNow;
        return activity;
    }

    private static IngestionCurrentActivityDto? BuildLiveUniverseActivity(UniverseStateContainer? stateContainer)
    {
        var progress = stateContainer?.UniverseEnrichmentProgress;
        var receivedAt = stateContainer?.UniverseEnrichmentProgressReceivedAt;
        if (progress is null || receivedAt is null)
        {
            return null;
        }

        var now = DateTimeOffset.UtcNow;
        if (now - receivedAt.Value.ToUniversalTime() > LiveUniverseProgressFreshness)
        {
            return null;
        }

        var total = Math.Max(0, progress.TotalCount);
        var currentOrdinal = total > 0
            ? Math.Clamp(progress.ProcessedCount, 1, total)
            : Math.Max(1, progress.ProcessedCount);
        var completed = total > 0
            ? Math.Max(0, currentOrdinal - 1)
            : Math.Max(0, progress.ProcessedCount);
        var active = 1;
        var queued = total > 0 ? Math.Max(0, total - completed - active) : 0;
        var percent = total > 0 ? Math.Clamp(completed * 100d / total, 0, 99) : 0;

        var stepLabel = ToFriendlyStepLabel(progress.CurrentStep);
        return new IngestionCurrentActivityDto
        {
            StageKey = "relationships",
            Message = progress.CurrentStep.Contains("enhancer", StringComparison.OrdinalIgnoreCase)
                ? "Artwork, people, and relationship enhancers"
                : "Relationships",
            Detail = StringHelpers.FirstNonBlankOr("", stepLabel, "Stage 3 enrichment"),
            CurrentItem = progress.WorkTitle,
            Source = "Wikidata",
            ProcessedCount = completed,
            TotalCount = total,
            CountUnit = "items",
            PercentComplete = percent,
            LastUpdatedTime = receivedAt,
            QueuedCount = queued,
            ActiveCount = active,
            SampleItems = [progress.WorkTitle],
            MetricLabel = "Current step",
            MetricValue = StringHelpers.FirstNonBlankOr("", stepLabel, "Stage 3"),
            MetricTone = "success",
            CurrentBatch = total > 0
                ? new IngestionActivityBatchDto
                {
                    BatchNumber = Math.Max(1, (int)Math.Ceiling(currentOrdinal / 50d)),
                    BatchSize = Math.Min(50, Math.Max(1, total)),
                    TotalBatches = Math.Max(1, (int)Math.Ceiling(total / 50d)),
                    CompletedCount = completed,
                    ActiveCount = active,
                    PendingCount = queued,
                    ReviewCount = 0,
                    ActiveItems = [progress.WorkTitle],
                    PendingPreview = [],
                    CompletedPreview = [],
                    ReviewPreview = [],
                }
                : null,
        };
    }

    private static void UpsertCurrentActivity(
        List<IngestionCurrentActivityDto> activities,
        IngestionCurrentActivityDto activity)
    {
        var existingIndex = activities.FindIndex(existing =>
            existing.StageKey.Equals(activity.StageKey, StringComparison.OrdinalIgnoreCase));

        if (existingIndex >= 0)
        {
            activities[existingIndex] = activity;
            return;
        }

        activities.Insert(0, activity);
    }

    public static IReadOnlyList<IngestionQueueHealthItem> BuildQueueHealth(
        IReadOnlyDictionary<string, int> summary,
        IReadOnlyList<OperationDto> operations)
    {
        var waitingForLock = operations.Count(operation =>
            operation.Stage?.Equals("waiting_for_lock", StringComparison.OrdinalIgnoreCase) == true);

        return
        [
            new("queued", "Queued", CountStatuses(summary, "pending", "queued"), "neutral"),
            new("running", "Running", CountStatuses(summary, "leased", "running"), "info"),
            new("waiting_for_lock", "Waiting for lock", waitingForLock, "warning"),
            new("retrying", "Retrying", CountStatuses(summary, "retry_waiting", "failed_retryable"), "warning"),
            new("blocked", "Blocked", CountStatuses(summary, "blocked"), "danger"),
            new("interrupted", "Interrupted", CountStatuses(summary, "interrupted"), "warning"),
            new("failed", "Failed", CountStatuses(summary, "failed_terminal", "dead_lettered"), "danger"),
            new("completed", "Completed", CountStatuses(summary, "succeeded", "no_result", "not_applicable", "skipped"), "success"),
        ];
    }

    public static bool ShouldDefaultShowDetails(IReadOnlyDictionary<string, int> summary) =>
        CountStatuses(summary, "blocked", "failed_retryable", "failed_terminal", "dead_lettered", "interrupted", "retry_waiting") > 0;

    public static bool ShouldLoadOperationRows(IReadOnlyDictionary<string, int> summary) =>
        CountStatuses(summary,
            "pending",
            "queued",
            "leased",
            "running",
            "retry_waiting",
            "blocked",
            "failed_retryable",
            "failed_terminal",
            "dead_lettered",
            "interrupted") > 0;

    public static IReadOnlyList<OperationDto> FilterOperations(
        IReadOnlyList<OperationDto> operations,
        string filter,
        string sort)
    {
        var normalizedFilter = NormalizeQueueFilter(filter);
        var query = operations.Where(operation => OperationMatchesFilter(operation, normalizedFilter));

        return NormalizeQueueSort(sort) switch
        {
            "updated" => query
                .OrderByDescending(operation => operation.UpdatedAt)
                .ThenBy(operation => operation.QueuePosition ?? int.MaxValue)
                .ToList(),
            "position" => query
                .OrderBy(operation => operation.QueuePosition ?? int.MaxValue)
                .ThenBy(operation => operation.Priority)
                .ThenBy(operation => operation.CreatedAt)
                .ToList(),
            _ => query
                .OrderBy(operation => operation.Priority)
                .ThenBy(operation => operation.QueuePosition ?? int.MaxValue)
                .ThenBy(operation => operation.CreatedAt)
                .ToList(),
        };
    }

    public static OperationDto? SelectPrimaryOperation(IReadOnlyList<OperationDto> operations) =>
        operations
            .Where(operation => IsStatus(operation, "running", "leased"))
            .OrderByDescending(operation => operation.UpdatedAt)
            .FirstOrDefault()
        ?? operations
            .Where(operation => IsStatus(operation, "blocked", "failed_retryable", "retry_waiting", "interrupted"))
            .OrderByDescending(operation => operation.UpdatedAt)
            .FirstOrDefault()
        ?? operations
            .Where(operation => IsStatus(operation, "pending", "queued"))
            .OrderBy(operation => operation.QueuePosition ?? int.MaxValue)
            .ThenBy(operation => operation.Priority)
            .FirstOrDefault();

    public static IReadOnlyList<OperationDto> BuildQueuePreview(IReadOnlyList<OperationDto> operations) =>
        operations
            .Where(operation => IsStatus(operation, "pending", "queued", "retry_waiting", "blocked", "failed_retryable", "interrupted"))
            .OrderBy(operation => operation.QueuePosition ?? int.MaxValue)
            .ThenBy(operation => operation.Priority)
            .ThenByDescending(operation => operation.UpdatedAt)
            .Take(3)
            .ToList();

    public static bool CanRetry(OperationDto operation) =>
        IsStatus(operation, "blocked", "interrupted", "failed_retryable", "failed_terminal", "dead_lettered");

    public static bool CanCancel(OperationDto operation) =>
        IsStatus(operation, "pending", "queued", "retry_waiting", "leased", "running");

    public static string FriendlyOperationName(OperationDto operation) =>
        FriendlyOperationName(operation.OperationType);

    public static string FriendlyOperationName(string? operationType)
    {
        var value = operationType ?? string.Empty;
        return value switch
        {
            "ingestion.file" => "Reading file",
            "identity.retail_match" => "Matching identity",
            "identity.wikidata_bridge" => "Linking Wikidata",
            "identity.quick_hydration" => "Quick hydration",
            "enrichment.cover_art" => "Fetching artwork",
            "enrichment.people" => "Finding people",
            "enrichment.description" => "Fetching description",
            "enrichment.relationships" => "Linking relationships",
            "text_track.lyrics" => "Finding lyrics",
            "text_track.subtitles" => "Finding subtitles",
            "plugin.commercial_skip" => "Commercial skip analysis",
            "plugin.playback_segment_detection" => "Playback segment detection",
            "writeback.metadata" => "Writing metadata",
            "ai.tldr" => "AI summary",
            "ai.vibe_tags" => "AI vibe tags",
            "ai.smart_labels" => "AI smart labels",
            _ when value.StartsWith("plugin.", StringComparison.OrdinalIgnoreCase) => "Plugin work",
            _ when value.StartsWith("ai.", StringComparison.OrdinalIgnoreCase) => "AI enrichment",
            _ when value.Length > 0 => value.Replace('.', ' '),
            _ => "Library work",
        };
    }

    public static string FriendlyStageName(string? stage)
    {
        if (string.IsNullOrWhiteSpace(stage))
        {
            return "Queued";
        }

        return stage.Trim().ToLowerInvariant() switch
        {
            "detected" => "Detected",
            "discovered" => "Discovered",
            "settling" => "Settling",
            "waiting_for_lock" => "Waiting for lock",
            "queued" => "Queued",
            "hashing" => "Hashing",
            "processed" => "Read details",
            "scored" => "Scored metadata",
            "parsing" => "Reading details",
            "scoring" => "Scoring identity",
            "registered" => "Registered",
            "queued_identity" => "Identity queued",
            "needs_review" => "Needs review",
            "duplicate" => "Duplicate",
            "same_path_redetected" => "Already tracked",
            "skipped_non_media" => "Skipped",
            "provider_lookup" => "Provider lookup",
            "downloading" => "Downloading",
            "analyzing" => "Analyzing",
            "writing_artifact" => "Writing artifact",
            "completed" => "Completed",
            "no_result" => "No result",
            "blocked" => "Blocked",
            "failed" => "Failed",
            var value => CultureInfo.CurrentCulture.TextInfo.ToTitleCase(value.Replace('_', ' ')),
        };
    }

    public static string FriendlyStatusName(string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
        {
            return "Unknown";
        }

        return status.Trim().ToLowerInvariant() switch
        {
            "retry_waiting" => "Retrying",
            "failed_retryable" => "Retrying",
            "failed_terminal" => "Failed",
            "dead_lettered" => "Failed",
            "no_result" => "No result",
            "missing_confirmed" => "Missing",
            "not_applicable" => "Not applicable",
            var value => CultureInfo.CurrentCulture.TextInfo.ToTitleCase(value.Replace('_', ' ')),
        };
    }

    public static string StatusTone(string? status)
    {
        var value = status ?? string.Empty;
        if (IsStatus(value, "running", "leased"))
        {
            return "info";
        }

        if (IsStatus(value, "succeeded"))
        {
            return "success";
        }

        if (IsStatus(value, "retry_waiting", "failed_retryable", "interrupted"))
        {
            return "warning";
        }

        if (IsStatus(value, "blocked", "failed_terminal", "dead_lettered"))
        {
            return "danger";
        }

        if (IsStatus(value, "stale"))
        {
            return "stale";
        }

        if (IsStatus(value, "no_result", "not_applicable", "skipped", "cancelled"))
        {
            return "muted";
        }

        return "neutral";
    }

    public static IReadOnlyList<IngestionDashboardStage> BuildStages(
        IngestionOperationsSnapshotDto? snapshot,
        IReadOnlyList<IngestionOperationsJobDto> activeJobs,
        int totalFiles)
    {
        if (snapshot?.StageProgress.Count > 0)
        {
            return snapshot.StageProgress
                .OrderBy(stage => stage.StageNumber)
                .Select(stage => new IngestionDashboardStage(
                    stage.StageKey,
                    stage.Label,
                    stage.StatusLabel ?? stage.Label,
                    ResolveNumberedStageIcon(stage.StageKey),
                    stage.CompletedFiles,
                    stage.TotalFiles,
                    Math.Clamp(stage.PercentComplete, 0, 100),
                    ResolveNumberedStageStatusKey(stage),
                    Math.Clamp(stage.PercentComplete, 0, 100),
                    false,
                    0,
                    string.Equals(stage.StageKey, "review", StringComparison.OrdinalIgnoreCase)
                        ? Math.Max(0, stage.ArtifactCount ?? 0)
                        : 0,
                    0,
                    false,
                    stage.StageNumber,
                    stage.ArtifactLabel,
                    stage.ArtifactCount,
                    stage.ActiveItemLabel,
                    stage.ActiveGroupLabel,
                    stage.ActiveGroupCount,
                    stage.LabelAccuracy,
                    stage.LastUpdatedTime,
                    stage.IsStale,
                    stage.StatusLabel,
                    stage.ActiveCount,
                    stage.QueuedCount,
                    stage.DetailItems))
                .ToList();
        }

        var activeKey = ResolveActiveStage(activeJobs);
        var duplicateCount = Count(snapshot, "duplicate");
        var skippedCount = Count(snapshot, "skipped");
        var failedCount = Count(snapshot, "failed");
        var skippedOrDuplicate = duplicateCount + skippedCount;
        var terminalOther = skippedOrDuplicate + failedCount;
        var retailMatched = Count(snapshot, "matched");
        var retailReview = Count(snapshot, "retail_review");
        var retailTotal = Math.Max(totalFiles, Total(snapshot, "matched", Math.Max(0, retailMatched + retailReview + terminalOther)));
        var retailDone = retailMatched + retailReview + terminalOther;
        var canonicalized = Count(snapshot, "canonicalized");
        var wikidataReview = Count(snapshot, "wikidata_review");
        var wikidataTotal = Total(snapshot, "canonicalized", Math.Max(0, canonicalized + wikidataReview));
        var wikidataDone = canonicalized + wikidataReview;
        var enriched = Count(snapshot, "enriched");
        var enrichmentDone = enriched + wikidataReview;
        var enrichmentTotal = Total(snapshot, "enriched", Math.Max(0, enrichmentDone));
        var scanningJobs = activeJobs
            .Where(job => ResolveActiveStage([job]) == "scanning")
            .ToList();
        var scanningTotal = scanningJobs.Sum(job => Math.Max(0, job.TotalCount));
        var scanningDone = scanningJobs.Sum(job => Math.Max(0, job.ProcessedCount));
        if (scanningTotal <= 0)
        {
            scanningTotal = Total(snapshot, "detected", totalFiles);
            scanningDone = Count(snapshot, "detected", 0);
        }

        var stages = new List<IngestionDashboardStage>
        {
            CreateStage(
                "scanning",
                "Ingestion_StageScanning",
                "Ingestion_StageScanningDetail",
                AppMaterialIcons.Outlined.Radar,
                scanningDone,
                scanningTotal,
                totalFiles,
                activeKey,
                hideCountWhenIdle: true),
            CreateStage(
                "retail",
                "Ingestion_StageRetailIdentification",
                "Ingestion_StageRetailIdentificationDetail",
                AppMaterialIcons.Outlined.Search,
                retailDone,
                retailTotal,
                totalFiles,
                activeKey,
                matchedCount: retailMatched,
                reviewCount: retailReview,
                otherCount: terminalOther),
            CreateStage(
                "wikidata",
                "Ingestion_StageWikidataMatch",
                "Ingestion_StageWikidataMatchDetail",
                AppMaterialIcons.Outlined.TravelExplore,
                wikidataDone,
                wikidataTotal,
                totalFiles,
                activeKey),
            CreateStage(
                "enrichment",
                "Ingestion_StageEnrichment",
                "Ingestion_StageEnrichmentDetail",
                AppMaterialIcons.Outlined.DataObject,
                enrichmentDone,
                enrichmentTotal,
                totalFiles,
                activeKey),
        };

        return stages;

        static IngestionDashboardStage CreateStage(
            string key,
            string labelKey,
            string detailKey,
            string icon,
            int count,
            int total,
            int globalTotal,
            string activeKey,
            bool hideCountWhenIdle = false,
            int matchedCount = 0,
            int reviewCount = 0,
            int otherCount = 0,
            bool isSummary = false)
        {
            var percent = total > 0
                ? Math.Clamp(count * 100d / total, 0, 100)
                : globalTotal <= 0 && isSummary
                    ? 100
                    : 0;
            var isActive = activeKey == key;
            var hideCount = hideCountWhenIdle && !isActive && total <= 0;
            var status = isActive && percent < 100
                ? "Ingestion_StatusActive"
                : hideCount
                    ? "Ingestion_StatusIdle"
                    : percent >= 100
                        ? "Ingestion_StatusComplete"
                        : "Ingestion_StatusPending";
            return new IngestionDashboardStage(
                key,
                labelKey,
                detailKey,
                icon,
                count,
                total,
                percent,
                status,
                percent,
                hideCount,
                matchedCount,
                reviewCount,
                otherCount,
                isSummary);
        }
    }

    private static string ResolveNumberedStageIcon(string stageKey)
    {
        var key = stageKey.ToLowerInvariant();
        return key switch
        {
            "scan" => AppMaterialIcons.Outlined.Folder,
            "read" => AppMaterialIcons.Outlined.Description,
            "retail" => AppMaterialIcons.Outlined.Search,
            "wikidata" => AppMaterialIcons.Outlined.TravelExplore,
            "ready" => AppMaterialIcons.Outlined.CheckCircle,
            "people" => AppMaterialIcons.Outlined.Groups,
            "relationships" or "universes" => AppMaterialIcons.Outlined.Public,
            "deep_artwork" or "artwork" => AppMaterialIcons.Outlined.Image,
            "review" => AppMaterialIcons.Outlined.WarningAmber,
            _ => AppMaterialIcons.Outlined.AutoAwesome,
        };
    }

    private static string ResolveNumberedStageStatusKey(IngestionStageProgressDto stage)
    {
        if (IsCompleteStageLabel(stage.StatusLabel))
        {
            return "Ingestion_StatusComplete";
        }

        if (stage.ActiveCount > 0)
        {
            return "Ingestion_StatusActive";
        }

        if (stage.IsStale)
        {
            return "Ingestion_StatusPending";
        }

        if (stage.PercentComplete >= 100)
        {
            return "Ingestion_StatusComplete";
        }

        if (stage.QueuedCount > 0 || stage.CompletedFiles > 0)
        {
            return "Ingestion_StatusPending";
        }

        return "Ingestion_StatusIdle";
    }

    private static bool IsCompleteStageLabel(string? statusLabel) =>
        string.Equals(statusLabel, "Complete", StringComparison.OrdinalIgnoreCase);

    public static IngestionOverallProgress BuildOverallProgress(
        IngestionDashboardMetrics metrics,
        IReadOnlyList<IngestionDashboardStage> stages,
        BatchProgressEvent? batch)
    {
        var pipelineStages = stages
            .Where(stage => !stage.HideCount && !IsReviewStage(stage))
            .OrderBy(stage => stage.StageNumber > 0 ? stage.StageNumber : int.MaxValue)
            .ToList();

        // Whole-pipeline progress: every stage is measured against the run's full item count
        // from the start, so fast early stages (detect, fingerprint, process) cannot push the bar
        // to ~done while slow later stages (retail match, Wikidata, hydration, enrichment) have
        // not seen most items yet. Each stage is weighted equally; overall = average completion.
        // A stage whose own (smaller) total is finished only counts as fully done once every
        // upstream stage is done too, otherwise it would claim 100% while items are still queued.
        var runTotal = Math.Max(Math.Max(0, metrics.TotalFiles), batch?.FilesTotal ?? 0);
        var measurableStages = pipelineStages.Where(stage => runTotal > 0 || stage.Total > 0).ToList();
        var upstreamDone = true;
        var fractionSum = 0d;
        foreach (var stage in measurableStages)
        {
            var denominator = Math.Max(Math.Max(0, stage.Total), runTotal);
            var done = denominator > 0 && stage.Count >= denominator;
            if (!done && upstreamDone && IsStageSettled(stage))
            {
                done = true;
            }

            fractionSum += done
                ? 1d
                : denominator > 0
                    ? Math.Clamp(stage.Count, 0, denominator) / (double)denominator
                    : 0d;
            upstreamDone &= done;
        }

        var percent = measurableStages.Count > 0
            ? Math.Clamp(fractionSum * 100d / measurableStages.Count, 0, 100)
            : metrics.TotalFiles > 0
                ? Math.Clamp(metrics.ProcessedFiles * 100d / metrics.TotalFiles, 0, 100)
                : 0;

        // Items fully settled: the batch's ready count when known, otherwise the last
        // measurable stage's count.
        var itemsReady = batch is not null
            ? batch.FilesIdentified
            : measurableStages.LastOrDefault()?.Count ?? 0;
        itemsReady = runTotal > 0 ? Math.Clamp(itemsReady, 0, runTotal) : Math.Max(0, itemsReady);

        var hasActiveStage = pipelineStages.Any(stage => stage.StatusKey == "Ingestion_StatusActive");
        if (!hasActiveStage && metrics.TotalFiles > 0 && metrics.ProcessedFiles >= metrics.TotalFiles)
        {
            percent = 100;
        }
        else if (hasActiveStage && percent >= 100)
        {
            percent = 99;
        }

        var activeStage = stages.FirstOrDefault(stage => stage.StatusKey == "Ingestion_StatusActive")
            ?? stages.FirstOrDefault(stage => !stage.HideCount && stage.Percent < 100)
            ?? stages.LastOrDefault(stage => !stage.HideCount)
            ?? stages.LastOrDefault();

        return new IngestionOverallProgress(
            metrics.ProcessedFiles,
            metrics.TotalFiles,
            percent,
            activeStage?.LabelKey ?? "Ingestion_StageScanning",
            activeStage?.DetailKey ?? "Ingestion_StageScanningDetail",
            activeStage?.Count ?? 0,
            activeStage?.Total ?? 0,
            activeStage?.Percent ?? 0,
            batch?.EstimatedSecondsRemaining,
            itemsReady);
    }

    private static bool IsStageSettled(IngestionDashboardStage stage) =>
        stage.Percent >= 99.5
        || stage.StatusKey == "Ingestion_StatusComplete"
        || string.Equals(stage.StatusLabel, "Complete", StringComparison.OrdinalIgnoreCase);

    private static bool IsReviewStage(IngestionDashboardStage stage) =>
        stage.StageNumber == 9 || stage.Key.Equals("review", StringComparison.OrdinalIgnoreCase);

    public static LibraryUpdateStatusViewModel BuildLibraryUpdateStatus(
        IngestionOperationsSnapshotDto? snapshot,
        IReadOnlyList<IngestionOperationsJobDto> activeJobs,
        IReadOnlyList<IngestionCurrentActivityDto> currentActivities,
        IReadOnlyList<ActivityEntryResponse> recentActivity,
        IReadOnlyList<ReviewItemViewModel> pendingReviews,
        IngestionDashboardMetrics metrics,
        string? error,
        DateTimeOffset? lastUpdated,
        DateTimeOffset now)
    {
        var latestBatch = snapshot?.RecentBatches
            .OrderByDescending(batch => batch.StartedAt)
            .FirstOrDefault();
        var lastCompletedAt = ResolveLastCompletedAt(snapshot);
        var hasReviewWork = (snapshot?.Summary.ItemsNeedingReview ?? 0) > 0 || pendingReviews.Count > 0;
        var hasPriorRun = snapshot is not null
            && (snapshot.RecentBatches.Count > 0
                || lastCompletedAt.HasValue
                || snapshot.Summary.TotalItems > 0
                || hasReviewWork
                || metrics.TotalFiles > 0);
        var hasDetailedWork = activeJobs.Count > 0 || currentActivities.Count > 0;
        var hasActiveDetailedWork = activeJobs.Any(IsActiveJob) || currentActivities.Any(IsActiveActivity);
        var isRunning = hasActiveDetailedWork
            || (snapshot?.Summary.ActiveJobs > 0 && !hasDetailedWork);
        var pageState = ResolveLibraryUpdatePageState(
            snapshot,
            latestBatch,
            hasPriorRun,
            isRunning,
            lastCompletedAt,
            error,
            now);

        var totalFiles = Math.Max(0, metrics.TotalFiles);
        if (totalFiles == 0)
        {
            totalFiles = Math.Max(0, latestBatch?.TotalFiles ?? snapshot?.Summary.TotalItems ?? 0);
        }

        var processedFiles = ResolveFileProcessingCount(snapshot, activeJobs, latestBatch, metrics, totalFiles);

        if (totalFiles > 0)
        {
            processedFiles = Math.Clamp(processedFiles, 0, totalFiles);
        }

        var matchedItems = Count(snapshot, "matched", snapshot?.Summary.RegisteredItems ?? latestBatch?.RegisteredCount ?? 0);
        if (matchedItems == 0)
        {
            matchedItems = Math.Max(0, latestBatch?.RegisteredCount ?? 0);
        }

        var reviewItems = pendingReviews.Count > 0
            ? pendingReviews.Count
            : Math.Max(0, snapshot?.Summary.ItemsNeedingReview ?? 0);
        var expectedOutcomes = snapshot?.Summary.ExpectedOutcomes;
        var expectedReviewItems = Math.Max(0, expectedOutcomes?.ExpectedReview ?? 0);
        var unexpectedReviewItems = expectedOutcomes is null
            ? 0
            : Math.Max(0, reviewItems - expectedReviewItems);
        var activeItems = currentActivities.Sum(activity => Math.Max(0, activity.ActiveCount));
        if (activeItems == 0)
        {
            activeItems = activeJobs.Count(job => IsActiveJob(job));
        }

        if (totalFiles > 0)
        {
            activeItems = Math.Clamp(activeItems, 0, totalFiles);
        }

        var queuedItems = ResolveQueuedPipelineCount(currentActivities, activeJobs, metrics, totalFiles, activeItems);

        var addedOrUpdatedCount = latestBatch is not null
            ? Math.Max(0, latestBatch.RegisteredCount + latestBatch.ReviewCount)
            : Math.Max(0, processedFiles);

        var activeStep = ResolveActiveLibraryUpdateStep(activeJobs, currentActivities);
        var progressPercent = ResolveLibraryUpdateProgress(pageState, activeJobs, processedFiles, totalFiles);
        var showProgress = pageState is LibraryUpdatePageState.Running or LibraryUpdatePageState.Complete or LibraryUpdatePageState.Failed or LibraryUpdatePageState.Interrupted;
        var isIndeterminate = pageState == LibraryUpdatePageState.Running
            && (totalFiles == 0 || processedFiles >= totalFiles);
        var primaryActivity = SelectPrimaryActivity(activeJobs, currentActivities, activeStep);
        var currentItem = CleanDisplayTitle(StringHelpers.FirstNonBlankOr("",
            primaryActivity?.CurrentItem,
            primaryActivity?.CurrentBatch?.ActiveItems.FirstOrDefault(),
            activeJobs.Select(job => job.CurrentItem).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))));
        var currentStep = ResolveCurrentStepLabel(activeJobs, primaryActivity, activeStep);
        var currentSource = ResolveCurrentSource(primaryActivity);
        var activityLine = ResolveCurrentActivityLine(pageState, activeStep, totalFiles);
        var secondaryLine = ResolveSecondaryLine(pageState, activeItems, queuedItems, processedFiles, matchedItems, reviewItems, totalFiles, lastCompletedAt, now);
        var status = ResolveStatusPill(pageState, reviewItems);
        var recentItems = BuildRecentItems(recentActivity, currentActivities);
        var recentRuns = BuildRecentRuns(snapshot?.RecentBatches ?? [], now);
        var attentionReasons = BuildAttentionReasons(snapshot?.ReviewReasons ?? [], reviewItems);
        var steps = BuildLibraryUpdateSteps(pageState, activeStep, reviewItems);
        var enrichmentStats = BuildEnrichmentStats(currentActivities, snapshot);
        var enrichmentTotal = totalFiles;
        var enrichmentCompleted = enrichmentTotal > 0
            ? ResolveEnrichmentCompletedCount(snapshot, enrichmentTotal)
            : ResolveEnrichmentCompletedCount(snapshot, 0);
        var enrichmentPercent = enrichmentTotal > 0
            ? Math.Clamp(enrichmentCompleted * 100d / enrichmentTotal, 0, 100)
            : 0;

        return new LibraryUpdateStatusViewModel(
            pageState,
            totalFiles,
            processedFiles,
            matchedItems,
            enrichmentCompleted,
            enrichmentTotal,
            enrichmentPercent,
            enrichmentStats,
            reviewItems,
            expectedReviewItems,
            unexpectedReviewItems,
            activeItems,
            queuedItems,
            addedOrUpdatedCount,
            progressPercent,
            showProgress,
            isIndeterminate,
            ResolveHeading(pageState),
            ResolveMainLine(pageState, processedFiles, totalFiles, reviewItems, lastCompletedAt, now),
            activityLine,
            secondaryLine,
            ResolveTimestampLine(pageState, lastCompletedAt, lastUpdated, now),
            status.Label,
            status.Tone,
            ResolvePanelTitle(pageState, hasPriorRun),
            ResolvePanelText(pageState, lastCompletedAt, now),
            currentItem,
            currentStep,
            currentSource,
            steps,
            attentionReasons,
            recentItems,
            recentRuns,
            hasPriorRun);
    }

    public static string ResolveActiveStage(IReadOnlyList<IngestionOperationsJobDto> activeJobs)
    {
        if (activeJobs.Count == 0)
        {
            return string.Empty;
        }

        var stage = activeJobs
            .Select(job => StringHelpers.FirstNonBlankOr("", job.CurrentStage, job.JobType))
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))
            ?.ToLowerInvariant() ?? string.Empty;

        if (stage.Contains("scan") || stage.Contains("queue") || stage.Contains("detect") || stage.Contains("parse"))
        {
            return "scanning";
        }

        if (stage.Contains("bridge") || stage.Contains("qid") || stage.Contains("wikidata") || stage.Contains("canonical"))
        {
            return "wikidata";
        }

        if (stage.Contains("identify") || stage.Contains("hash") || stage.Contains("fingerprint") || stage.Contains("match") || stage.Contains("retail"))
        {
            return "retail";
        }

        if (stage.Contains("enrich") || stage.Contains("hydrate") || stage.Contains("metadata") || stage.Contains("universe"))
        {
            return "enrichment";
        }

        if (stage.Contains("register") || stage.Contains("organize") || stage.Contains("review") || stage.Contains("complete"))
        {
            return "enrichment";
        }

        return "retail";
    }

    private static IngestionCurrentActivityDto ToCurrentActivity(
        IngestionOperationsJobDto job,
        IReadOnlyList<IngestionDashboardStage> stages)
    {
        var stageKey = ResolveActiveStage([job]);
        var stage = stages.FirstOrDefault(value => value.Key.Equals(stageKey, StringComparison.OrdinalIgnoreCase));
        var total = Math.Max(0, stage?.Total ?? job.TotalCount);
        var processed = Math.Clamp(Math.Max(0, stage?.Count ?? job.ProcessedCount), 0, Math.Max(0, total));
        var message = stageKey switch
        {
            "retail" => "Matching metadata",
            "wikidata" => "Checking Wikidata identity",
            "enrichment" => "Enriching relationships",
            _ => "Scanning files",
        };
        var item = StringHelpers.FirstNonBlankOr("", job.CurrentItem, job.CurrentStage, "Ingestion is running");

        return new IngestionCurrentActivityDto
        {
            StageKey = stageKey,
            Message = message,
            Detail = item,
            CurrentItem = item,
            Source = job.SourceFolder,
            ProcessedCount = processed,
            TotalCount = total,
            CountUnit = CountUnitForStage(stageKey),
            PercentComplete = total > 0 ? Math.Clamp(processed * 100d / total, 0, 100) : Math.Clamp(job.PercentComplete, 0, 100),
            LastUpdatedTime = job.LastUpdatedTime,
            QueuedCount = Math.Max(0, total - processed),
            ActiveCount = 1,
            SampleItems = [item],
        };
    }

    public static IngestionLiveMode ResolveLiveMode(EngineConnectionState state) => state switch
    {
        EngineConnectionState.Online => IngestionLiveMode.Live,
        EngineConnectionState.Checking or EngineConnectionState.Reconnecting or EngineConnectionState.LiveUpdatesDisconnected => IngestionLiveMode.Reconnecting,
        _ => IngestionLiveMode.Polling,
    };

    private static string CountUnitForStage(string? stageKey) => stageKey?.ToLowerInvariant() switch
    {
        "artwork" => "artwork assets",
        "relationships" => "items",
        "people" => "people",
        "wikidata" => "items",
        _ => "files",
    };

    private static int ResolveEnrichmentCompletedCount(
        IngestionOperationsSnapshotDto? snapshot,
        int totalFiles)
    {
        var terminal = ResolveTerminalPipelineCount(snapshot, totalFiles);
        if (terminal > 0)
        {
            return terminal;
        }

        var enriched = Count(snapshot, "enriched");
        if (enriched > 0)
        {
            return ClampFileCount(enriched, totalFiles);
        }

        var hasCurrentPipelineStages = snapshot?.PipelineStages.Any(stage =>
            stage.Key.Equals("matched", StringComparison.OrdinalIgnoreCase)
            || stage.Key.Equals("retail_review", StringComparison.OrdinalIgnoreCase)
            || stage.Key.Equals("canonicalized", StringComparison.OrdinalIgnoreCase)
            || stage.Key.Equals("wikidata_review", StringComparison.OrdinalIgnoreCase)
            || stage.Key.Equals("enriched", StringComparison.OrdinalIgnoreCase)) == true;

        if (hasCurrentPipelineStages)
        {
            return 0;
        }

        return ClampFileCount(snapshot?.Summary.RegisteredItems ?? 0, totalFiles);
    }

    private static IEnumerable<LiveIngestionItemProgress> FreshIngestionItemProgress(UniverseStateContainer stateContainer)
    {
        var now = DateTimeOffset.UtcNow;
        return stateContainer.IngestionItemProgress
            .Where(item => IsFileIngestionTerminal(item.Event)
                || now - item.ReceivedAt.ToUniversalTime() <= LiveItemProgressFreshness);
    }

    private static int ResolveLiveItemTotal(
        IngestionOperationsSnapshotDto? snapshot,
        BatchProgressEvent? batch,
        IReadOnlyList<LiveIngestionItemProgress> items)
    {
        if (batch?.FilesTotal > 0)
        {
            return batch.FilesTotal;
        }

        var batchId = items.FirstOrDefault()?.Event.BatchId;
        var snapshotTotal = batchId is { } id
            ? snapshot?.ActiveJobs.FirstOrDefault(job => job.JobId == id)?.TotalCount ?? 0
            : 0;
        var trackedTotal = items.Select(item => item.Event.LogEntryId).Distinct().Count();

        return Math.Max(1, Math.Max(snapshotTotal, trackedTotal));
    }

    private static double ResolveLiveItemPercent(
        IReadOnlyList<LiveIngestionItemProgress> items,
        int total)
    {
        if (items.Count == 0)
        {
            return 0;
        }

        var normalizedTotal = Math.Max(1, total);
        var progressUnits = items
            .GroupBy(item => item.Event.LogEntryId)
            .Select(group => group.OrderByDescending(item => item.ReceivedAt).First().Event)
            .Sum(ev => IsFileIngestionTerminal(ev) ? 100 : Math.Clamp(ev.ProgressPercent, 0, 99));

        return Math.Clamp(progressUnits / (normalizedTotal * 100d) * 100d, 0, 100);
    }

    private static string ResolveItemTitle(IngestionItemProgressEvent ev) =>
        StringHelpers.FirstNonBlankOr("", ev.Title, ev.FileName, Path.GetFileName(ev.FilePath), "Library item");

    private static string ResolveItemStageKey(string? stage) =>
        (stage ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "registered" => "saving",
            "scored" or "queued_identity" => "retail",
            "needs_review" => "retail",
            var value when value.Contains("hash") || value.Contains("process") || value.Contains("detect") => "scanning",
            _ => "scanning",
        };

    private static string ResolveItemActivityMessage(string stageKey) => stageKey switch
    {
        "saving" => "Saving library records",
        "retail" => "Preparing identity match",
        _ => "Reading media files",
    };

    private static bool IsFileIngestionTerminal(IngestionItemProgressEvent ev) =>
        ev.IsTerminal
        || ev.Stage.Equals("queued_identity", StringComparison.OrdinalIgnoreCase)
        || ev.Stage.Equals("complete", StringComparison.OrdinalIgnoreCase)
        || ev.Stage.Equals("needs_review", StringComparison.OrdinalIgnoreCase)
        || ev.Stage.Equals("duplicate", StringComparison.OrdinalIgnoreCase)
        || ev.Stage.Equals("same_path_redetected", StringComparison.OrdinalIgnoreCase)
        || ev.Stage.Equals("missing", StringComparison.OrdinalIgnoreCase)
        || ev.Stage.Equals("skipped_non_media", StringComparison.OrdinalIgnoreCase)
        || ev.Stage.Equals("failed", StringComparison.OrdinalIgnoreCase);

    private static bool IsReviewTerminalStage(string? stage) =>
        stage?.Equals("needs_review", StringComparison.OrdinalIgnoreCase) == true;

    private void PruneOperationCaches()
    {
        var operationIds = Operations.Select(operation => operation.Id).ToHashSet();
        foreach (var id in _operationDetails.Keys.Where(id => !operationIds.Contains(id)).ToList())
        {
            _operationDetails.Remove(id);
        }

        if (ExpandedOperationId is { } expandedId && !operationIds.Contains(expandedId))
        {
            ExpandedOperationId = null;
        }

        var entityIds = Operations
            .Select(operation => operation.EntityId)
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .ToHashSet();
        foreach (var id in _capabilitiesByEntity.Keys.Where(id => !entityIds.Contains(id)).ToList())
        {
            _capabilitiesByEntity.Remove(id);
        }
    }

    private static int CountStatuses(IReadOnlyDictionary<string, int> summary, params string[] statuses) =>
        statuses.Sum(status => summary.TryGetValue(status, out var count) ? Math.Max(0, count) : 0);

    private static bool OperationMatchesFilter(OperationDto operation, string filter) =>
        filter switch
        {
            "queued" => IsStatus(operation, "pending", "queued"),
            "running" => IsStatus(operation, "leased", "running"),
            "blocked" => IsStatus(operation, "blocked"),
            "retrying" => IsStatus(operation, "retry_waiting", "failed_retryable", "interrupted"),
            "failed" => IsStatus(operation, "failed_terminal", "dead_lettered"),
            "completed" => IsStatus(operation, "succeeded", "no_result", "missing_confirmed", "not_applicable", "skipped", "cancelled"),
            _ => true,
        };

    private static string NormalizeQueueFilter(string? filter) =>
        (filter ?? "all").Trim().ToLowerInvariant() switch
        {
            "queued" => "queued",
            "running" => "running",
            "blocked" => "blocked",
            "retrying" => "retrying",
            "failed" => "failed",
            "completed" => "completed",
            _ => "all",
        };

    private static string NormalizeQueueSort(string? sort) =>
        (sort ?? "priority").Trim().ToLowerInvariant() switch
        {
            "updated" => "updated",
            "position" => "position",
            _ => "priority",
        };

    private static bool IsStatus(OperationDto operation, params string[] statuses) =>
        IsStatus(operation.Status, statuses);

    private static bool IsStatus(string value, params string[] statuses) =>
        statuses.Any(status => value.Equals(status, StringComparison.OrdinalIgnoreCase));

    private static int Count(IngestionOperationsSnapshotDto? snapshot, string key) =>
        snapshot?.PipelineStages.FirstOrDefault(stage => stage.Key.Equals(key, StringComparison.OrdinalIgnoreCase))?.Count ?? 0;

    private static int Count(IngestionOperationsSnapshotDto? snapshot, string key, int fallback) =>
        snapshot?.PipelineStages.FirstOrDefault(stage => stage.Key.Equals(key, StringComparison.OrdinalIgnoreCase))?.Count ?? fallback;

    private static int Total(IngestionOperationsSnapshotDto? snapshot, string key, int fallback)
    {
        var total = snapshot?.PipelineStages
            .FirstOrDefault(stage => stage.Key.Equals(key, StringComparison.OrdinalIgnoreCase))
            ?.TotalCount ?? 0;
        return total > 0 ? total : fallback;
    }

    private static int ResolveTerminalPipelineCount(IngestionOperationsSnapshotDto? snapshot, int totalFiles)
    {
        if (snapshot is null)
        {
            return 0;
        }

        var hasIdentityPipelineStages = snapshot.PipelineStages.Any(stage =>
            stage.Key.Equals("matched", StringComparison.OrdinalIgnoreCase)
            || stage.Key.Equals("retail_review", StringComparison.OrdinalIgnoreCase)
            || stage.Key.Equals("canonicalized", StringComparison.OrdinalIgnoreCase)
            || stage.Key.Equals("wikidata_review", StringComparison.OrdinalIgnoreCase)
            || stage.Key.Equals("enriched", StringComparison.OrdinalIgnoreCase));
        var enrichedTotal = Total(snapshot, "enriched", 0);
        var readyForLibrary = Count(snapshot, "enriched");
        if (readyForLibrary == 0 && enrichedTotal == 0 && !hasIdentityPipelineStages)
        {
            readyForLibrary = Count(snapshot, "registered", snapshot.Summary.RegisteredItems);
        }

        var wikidataTerminal = Count(snapshot, "wikidata_review");
        var needsReview = Math.Max(Count(snapshot, "needs_review"), snapshot.Summary.ItemsNeedingReview);
        var duplicate = Count(snapshot, "duplicate");
        var skipped = Count(snapshot, "skipped");
        var failed = Count(snapshot, "failed");
        var terminalCount = readyForLibrary + wikidataTerminal + needsReview + duplicate + skipped + failed;
        var retailTerminal = Count(snapshot, "matched") + Count(snapshot, "retail_review") + duplicate + skipped + failed;
        if (HasTerminalRecentBatch(snapshot) && retailTerminal > terminalCount)
        {
            terminalCount = retailTerminal;
        }

        return totalFiles > 0
            ? Math.Clamp(terminalCount, 0, totalFiles)
            : Math.Max(0, terminalCount);
    }

    private static bool HasTerminalRecentBatch(IngestionOperationsSnapshotDto snapshot)
    {
        var latestBatch = snapshot.RecentBatches
            .OrderByDescending(batch => batch.StartedAt)
            .FirstOrDefault();
        return latestBatch is not null
            && (latestBatch.CompletedAt.HasValue
                || IsFailedBatchStatus(latestBatch.Status)
                || IsInterruptedBatchStatus(latestBatch.Status)
                || !IsActiveBatchStatus(latestBatch.Status));
    }

    private static int ResolveFileProcessingCount(
        IngestionOperationsSnapshotDto? snapshot,
        IReadOnlyList<IngestionOperationsJobDto> activeJobs,
        IngestionOperationsBatchDto? latestBatch,
        IngestionDashboardMetrics metrics,
        int totalFiles)
    {
        var parsed = Count(snapshot, "parsed");
        if (parsed > 0)
        {
            return ClampFileCount(parsed, totalFiles);
        }

        if (latestBatch?.ProcessedFiles > 0)
        {
            return ClampFileCount(latestBatch.ProcessedFiles, totalFiles);
        }

        var detected = Count(snapshot, "detected");
        if (detected > 0)
        {
            return ClampFileCount(detected, totalFiles);
        }

        var terminalFileOutcomes =
            Count(snapshot, "registered", snapshot?.Summary.RegisteredItems ?? 0)
            + Math.Max(Count(snapshot, "needs_review"), snapshot?.Summary.ItemsNeedingReview ?? 0)
            + Count(snapshot, "duplicate")
            + Count(snapshot, "skipped")
            + Count(snapshot, "failed");
        if (terminalFileOutcomes > 0)
        {
            return ClampFileCount(terminalFileOutcomes, totalFiles);
        }

        var activeJobProcessed = activeJobs.Sum(job => Math.Max(0, job.ProcessedCount));
        if (activeJobProcessed > 0)
        {
            return ClampFileCount(activeJobProcessed, totalFiles);
        }

        return ClampFileCount(metrics.ProcessedFiles, totalFiles);
    }

    private static int ResolveQueuedPipelineCount(
        IReadOnlyList<IngestionCurrentActivityDto> currentActivities,
        IReadOnlyList<IngestionOperationsJobDto> activeJobs,
        IngestionDashboardMetrics metrics,
        int totalFiles,
        int activeItems)
    {
        var queuedFromActivities = currentActivities
            .Where(activity => activity.TotalCount <= 0 || activity.ProcessedCount < activity.TotalCount)
            .Sum(activity => Math.Max(0, activity.QueuedCount));
        if (queuedFromActivities > 0)
        {
            return ClampQueuedCount(queuedFromActivities, totalFiles, activeItems);
        }

        if (currentActivities.Any(IsActiveActivity))
        {
            return 0;
        }

        var queuedFromJobs = activeJobs
            .Where(IsActiveJob)
            .Sum(job => Math.Max(0, job.TotalCount - job.ProcessedCount));
        if (queuedFromJobs > 0)
        {
            return ClampQueuedCount(queuedFromJobs, totalFiles, activeItems);
        }

        return ClampQueuedCount(
            Math.Max(0, totalFiles - Math.Max(0, metrics.ProcessedFiles) - activeItems),
            totalFiles,
            activeItems);
    }

    private static int ClampFileCount(int count, int totalFiles) =>
        totalFiles > 0
            ? Math.Clamp(Math.Max(0, count), 0, totalFiles)
            : Math.Max(0, count);

    private static int ClampQueuedCount(
        int count,
        int totalFiles,
        int activeItems)
    {
        var normalized = Math.Max(0, count);
        if (totalFiles <= 0)
        {
            return normalized;
        }

        var remainingCapacity = Math.Max(0, totalFiles - Math.Max(0, activeItems));
        return Math.Min(normalized, remainingCapacity);
    }

    private static bool IsUsefulActivity(ActivityEntryResponse activity) =>
        activity.ActionType.Contains("Ingest", StringComparison.OrdinalIgnoreCase)
        || activity.ActionType.Contains("MediaAdded", StringComparison.OrdinalIgnoreCase)
        || activity.ActionType.Contains("Batch", StringComparison.OrdinalIgnoreCase)
        || activity.ActionType.Contains("Review", StringComparison.OrdinalIgnoreCase)
        || activity.ActionType.Contains("Metadata", StringComparison.OrdinalIgnoreCase)
        || activity.ActionType.Contains("Artwork", StringComparison.OrdinalIgnoreCase)
        || activity.ActionType.Contains("CoverArt", StringComparison.OrdinalIgnoreCase)
        || activity.ActionType.Contains("Person", StringComparison.OrdinalIgnoreCase)
        || activity.ActionType.Contains("Character", StringComparison.OrdinalIgnoreCase)
        || activity.ActionType.Contains("Location", StringComparison.OrdinalIgnoreCase)
        || activity.ActionType.Contains("Organization", StringComparison.OrdinalIgnoreCase)
        || activity.ActionType.Contains("Relationship", StringComparison.OrdinalIgnoreCase)
        || activity.ActionType.Contains("NarrativeRoot", StringComparison.OrdinalIgnoreCase)
        || activity.ActionType.Contains("Universe", StringComparison.OrdinalIgnoreCase);

    private static LibraryUpdatePageState ResolveLibraryUpdatePageState(
        IngestionOperationsSnapshotDto? snapshot,
        IngestionOperationsBatchDto? latestBatch,
        bool hasPriorRun,
        bool isRunning,
        DateTimeOffset? lastCompletedAt,
        string? error,
        DateTimeOffset now)
    {
        if (!string.IsNullOrWhiteSpace(error))
        {
            return LibraryUpdatePageState.StatusUnavailable;
        }

        if (isRunning)
        {
            return LibraryUpdatePageState.Running;
        }

        if (latestBatch is not null && IsFailedBatchStatus(latestBatch.Status))
        {
            return LibraryUpdatePageState.Failed;
        }

        if (latestBatch is not null && IsInterruptedBatchStatus(latestBatch.Status))
        {
            return LibraryUpdatePageState.Interrupted;
        }

        if (lastCompletedAt is not null && now - lastCompletedAt.Value.ToUniversalTime() <= TimeSpan.FromSeconds(60))
        {
            return LibraryUpdatePageState.Complete;
        }

        return snapshot is null || !hasPriorRun
            ? LibraryUpdatePageState.NoPriorRun
            : LibraryUpdatePageState.Idle;
    }

    private static DateTimeOffset? ResolveLastCompletedAt(IngestionOperationsSnapshotDto? snapshot)
    {
        var lastBatchCompletion = snapshot?.RecentBatches
            .Where(batch => batch.CompletedAt.HasValue
                && !IsFailedBatchStatus(batch.Status)
                && !IsInterruptedBatchStatus(batch.Status))
            .OrderByDescending(batch => batch.CompletedAt)
            .Select(batch => batch.CompletedAt)
            .FirstOrDefault();

        return lastBatchCompletion ?? snapshot?.Summary.LastSuccessfulScanTime;
    }

    private static double ResolveLibraryUpdateProgress(
        LibraryUpdatePageState pageState,
        IReadOnlyList<IngestionOperationsJobDto> activeJobs,
        int processedFiles,
        int totalFiles)
    {
        if (pageState == LibraryUpdatePageState.Complete)
        {
            return 100;
        }

        var calculatedPercent = totalFiles > 0
            ? Math.Clamp(processedFiles * 100d / totalFiles, 0, 100)
            : 0;
        var livePercent = activeJobs
            .Where(IsActiveJob)
            .Where(job => job.TotalCount > 0 || job.PercentComplete > 0)
            .Select(job => Math.Clamp(job.PercentComplete, 0, 100))
            .DefaultIfEmpty(calculatedPercent)
            .Max();
        var percent = pageState == LibraryUpdatePageState.Running
            ? Math.Max(calculatedPercent, livePercent)
            : calculatedPercent;

        // A settled file count is only one phase of an ingestion run. Avoid a
        // full run percentage while identity or enrichment work is still live;
        // those stages can discover additional work as they proceed.
        return pageState == LibraryUpdatePageState.Running && percent >= 100
            ? 0
            : Math.Clamp(percent, 0, 100);
    }

    private static string BuildSnapshotSignature(
        IngestionOperationsSnapshotDto? snapshot,
        IReadOnlyList<ActivityEntryResponse> recentActivity,
        IReadOnlyList<ReviewItemViewModel> pendingReviews,
        IReadOnlyDictionary<string, int> operationsSummary,
        IReadOnlyList<OperationDto> operations,
        bool showDetails,
        string queueFilter,
        string queueSort,
        Guid? expandedOperationId)
    {
        if (snapshot is null)
        {
            var nullOperationSummary = string.Join(';', operationsSummary
                .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(pair => $"{pair.Key}:{pair.Value}"));
            return $"null|activity:{recentActivity.Count}|reviews:{pendingReviews.Count}|ops:{nullOperationSummary}|details:{showDetails}:{queueFilter}:{queueSort}:{expandedOperationId}";
        }

        var summary = snapshot.Summary;
        var active = string.Join(';', snapshot.ActiveJobs
            .OrderBy(job => job.JobId)
            .Select(job => string.Join(':',
                job.JobId,
                job.CurrentStage,
                job.ProcessedCount,
                job.TotalCount,
                Math.Round(job.PercentComplete, 1),
                job.Status)));
        var activities = string.Join(';', snapshot.CurrentActivities
            .OrderBy(activity => activity.StageKey)
            .Select(activity => string.Join(':',
                activity.StageKey,
                activity.CurrentItem,
                activity.ProcessedCount,
                activity.TotalCount,
                activity.ActiveCount,
                activity.QueuedCount)));
        var stageProgress = string.Join(';', snapshot.StageProgress
            .OrderBy(stage => stage.StageNumber)
            .Select(stage => string.Join(':',
                stage.StageNumber,
                stage.StageKey,
                stage.CompletedFiles,
                stage.TotalFiles,
                Math.Round(stage.PercentComplete, 1),
                stage.ActiveCount,
                stage.QueuedCount,
                stage.StatusLabel,
                stage.ArtifactCount,
                string.Join(',', stage.DetailItems.Select(item => $"{item.Label}={item.Value}")))));
        var operationSummary = string.Join(';', operationsSummary
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => $"{pair.Key}:{pair.Value}"));
        var operationRows = string.Join(';', operations
            .OrderBy(operation => operation.Id)
            .Select(operation => string.Join(':',
                operation.Id,
                operation.Status,
                operation.Stage,
                operation.ProgressPercent,
                operation.ItemsCompleted,
                operation.ItemsTotal,
                operation.QueuePosition,
                operation.UpdatedAt.ToUnixTimeSeconds())));

        return string.Join('|',
            summary.TotalItems,
            summary.RegisteredItems,
            summary.ItemsNeedingReview,
            summary.ActiveJobs,
            summary.FailedJobs,
            summary.LastSuccessfulScanTime?.ToUnixTimeSeconds(),
            active,
            activities,
            stageProgress,
            operationSummary,
            operationRows,
            showDetails,
            queueFilter,
            queueSort,
            expandedOperationId,
            recentActivity.FirstOrDefault()?.Id,
            pendingReviews.Count);
    }

    private static int ResolveActiveLibraryUpdateStep(
        IReadOnlyList<IngestionOperationsJobDto> activeJobs,
        IReadOnlyList<IngestionCurrentActivityDto> currentActivities)
    {
        var value = StringHelpers.FirstNonBlankOr("",
            activeJobs.Select(job => StringHelpers.FirstNonBlankOr("", job.CurrentStage, job.JobType)).FirstOrDefault(candidate => !string.IsNullOrWhiteSpace(candidate)),
            currentActivities.FirstOrDefault(activity => activity.ActiveCount > 0)?.StageKey,
            currentActivities.FirstOrDefault(activity => activity.ActiveCount > 0)?.Message,
            currentActivities.FirstOrDefault(activity => activity.PercentComplete < 100)?.StageKey,
            currentActivities.FirstOrDefault(activity => activity.PercentComplete < 100)?.Message);

        if (string.IsNullOrWhiteSpace(value))
        {
            return activeJobs.Count > 0 ? 1 : 4;
        }

        var normalized = value.ToLowerInvariant();
        if (normalized.Contains("save") || normalized.Contains("register") || normalized.Contains("organize") || normalized.Contains("write"))
        {
            return 4;
        }

        if (normalized.Contains("artwork") || normalized.Contains("cover") || normalized.Contains("metadata")
            || normalized.Contains("hydrate") || normalized.Contains("enrich") || normalized.Contains("universe")
            || normalized.Contains("series") || normalized.Contains("relationship") || normalized.Contains("people")
            || normalized.Contains("cast"))
        {
            return 3;
        }

        if (normalized.Contains("match") || normalized.Contains("identity") || normalized.Contains("identify")
            || normalized.Contains("retail") || normalized.Contains("wikidata") || normalized.Contains("qid") || normalized.Contains("bridge"))
        {
            return 2;
        }

        if (normalized.Contains("read") || normalized.Contains("parse") || normalized.Contains("hash") || normalized.Contains("process"))
        {
            return 1;
        }

        return normalized.Contains("scan") || normalized.Contains("detect") || normalized.Contains("queue")
            ? 0
            : 1;
    }

    private static IngestionCurrentActivityDto? SelectPrimaryActivity(
        IReadOnlyList<IngestionOperationsJobDto> activeJobs,
        IReadOnlyList<IngestionCurrentActivityDto> currentActivities,
        int activeStep)
    {
        if (currentActivities.Count == 0)
        {
            return null;
        }

        var activeStageKey = ResolveActiveStage(activeJobs);
        var expectedStageKey = activeStep switch
        {
            0 or 1 => "scanning",
            2 => "wikidata",
            3 => "artwork",
            4 => "saving",
            _ => activeStageKey,
        };

        return currentActivities
            .Where(activity => !IsCompletedActivity(activity))
            .FirstOrDefault(activity => activity.StageKey.Equals(activeStageKey, StringComparison.OrdinalIgnoreCase))
            ?? currentActivities
                .Where(activity => !IsCompletedActivity(activity))
                .FirstOrDefault(activity => activity.StageKey.Equals(expectedStageKey, StringComparison.OrdinalIgnoreCase))
            ?? currentActivities.FirstOrDefault(activity => !IsCompletedActivity(activity))
            ?? currentActivities.FirstOrDefault();
    }

    private static bool IsCompletedActivity(IngestionCurrentActivityDto activity) =>
        activity.TotalCount > 0
        && activity.ProcessedCount >= activity.TotalCount
        && activity.ActiveCount <= 0
        && activity.QueuedCount <= 0;

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

        return DateTimeOffset.UtcNow - updatedAt.Value.ToUniversalTime() <= LiveUniverseProgressFreshness;
    }

    private static string ResolveCurrentStepLabel(
        IReadOnlyList<IngestionOperationsJobDto> activeJobs,
        IngestionCurrentActivityDto? primaryActivity,
        int activeStep)
    {
        var explicitStep = StringHelpers.FirstNonBlankOr("",
            ResolveSpecificActivityStep(primaryActivity),
            activeJobs.Select(job => job.CurrentStage).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)));
        if (!string.IsNullOrWhiteSpace(explicitStep))
        {
            return ToFriendlyStepLabel(explicitStep);
        }

        return activeStep switch
        {
            0 => "Scanning library folders",
            1 => "Reading media details",
            2 => "Matching titles and identity",
            3 => "Enriching metadata and relationships",
            4 => "Saving library records",
            _ => "Checking library update",
        };
    }

    private static string ResolveSpecificActivityStep(IngestionCurrentActivityDto? activity)
    {
        if (activity is null)
        {
            return string.Empty;
        }

        var detail = activity.Detail ?? string.Empty;
        if (detail.Contains("enhancer", StringComparison.OrdinalIgnoreCase)
            || detail.Contains("core universe", StringComparison.OrdinalIgnoreCase)
            || detail.Contains("artwork", StringComparison.OrdinalIgnoreCase)
            || detail.Contains("people", StringComparison.OrdinalIgnoreCase)
            || detail.Contains("relationship", StringComparison.OrdinalIgnoreCase))
        {
            return detail;
        }

        return StringHelpers.FirstNonBlankOr("", activity.Message, detail);
    }

    private static string ResolveCurrentSource(IngestionCurrentActivityDto? primaryActivity)
    {
        return LooksLikeProvider(primaryActivity?.Source)
            ? primaryActivity!.Source!
            : string.Empty;
    }

    private static bool LooksLikeProvider(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim();
        return !trimmed.Equals("Watch folders", StringComparison.OrdinalIgnoreCase)
            && !trimmed.Equals("Current file", StringComparison.OrdinalIgnoreCase)
            && !KnownMediaExtensions.Any(extension => trimmed.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            && !trimmed.Contains('\\')
            && !trimmed.Contains('/')
            && !trimmed.Contains(':');
    }

    private static string ResolveCurrentActivityLine(LibraryUpdatePageState pageState, int activeStep, int totalFiles) =>
        pageState switch
        {
            LibraryUpdatePageState.Running when totalFiles == 0 => "Looking for new media",
            LibraryUpdatePageState.Running => activeStep switch
            {
                0 => "Currently scanning library folders",
                1 => "Currently reading media details",
                2 => "Currently matching titles and identity",
                3 => "Currently enriching artwork, people, and relationships",
                4 => "Currently saving library records",
                _ => "Currently updating your library",
            },
            LibraryUpdatePageState.Complete => string.Empty,
            LibraryUpdatePageState.Idle => string.Empty,
            LibraryUpdatePageState.Interrupted => "Tuvima Library paused before the update finished.",
            LibraryUpdatePageState.Failed => "Tuvima Library stopped before the update was complete.",
            LibraryUpdatePageState.StatusUnavailable => "Status temporarily unavailable",
            _ => "Start a scan to find new or changed media.",
        };

    private static string? ResolveSecondaryLine(
        LibraryUpdatePageState pageState,
        int activeItems,
        int queuedItems,
        int processedFiles,
        int matchedItems,
        int reviewItems,
        int totalFiles,
        DateTimeOffset? lastCompletedAt,
        DateTimeOffset now)
    {
        if (pageState == LibraryUpdatePageState.Running)
        {
            return BuildRunningPipelineSummary(activeItems, queuedItems, reviewItems);
        }

        if (pageState == LibraryUpdatePageState.Idle)
        {
            return $"{processedFiles.ToString("N0", CultureInfo.CurrentCulture)} files finished - {matchedItems.ToString("N0", CultureInfo.CurrentCulture)} matched - {reviewItems.ToString("N0", CultureInfo.CurrentCulture)} need review";
        }

        return pageState switch
        {
            LibraryUpdatePageState.Running => BuildRunningPipelineSummary(activeItems, queuedItems, reviewItems),
            LibraryUpdatePageState.Complete => $"{matchedItems.ToString("N0", CultureInfo.CurrentCulture)} matched  -  {reviewItems.ToString("N0", CultureInfo.CurrentCulture)} need review",
            LibraryUpdatePageState.Idle => $"{totalFiles.ToString("N0", CultureInfo.CurrentCulture)} files processed  -  {matchedItems.ToString("N0", CultureInfo.CurrentCulture)} matched  -  {reviewItems.ToString("N0", CultureInfo.CurrentCulture)} need review",
            LibraryUpdatePageState.Interrupted => $"{totalFiles.ToString("N0", CultureInfo.CurrentCulture)} files found  -  {matchedItems.ToString("N0", CultureInfo.CurrentCulture)} matched  -  {reviewItems.ToString("N0", CultureInfo.CurrentCulture)} need review",
            LibraryUpdatePageState.Failed => $"{totalFiles.ToString("N0", CultureInfo.CurrentCulture)} files found  -  {matchedItems.ToString("N0", CultureInfo.CurrentCulture)} matched  -  {reviewItems.ToString("N0", CultureInfo.CurrentCulture)} need review",
            LibraryUpdatePageState.StatusUnavailable when lastCompletedAt.HasValue => $"Last successful scan completed {FormatRelativeLong(lastCompletedAt.Value, now)}",
            _ => null,
        };
    }

    private static string BuildRunningPipelineSummary(int activeItems, int queuedItems, int reviewItems)
    {
        var parts = new List<string>(3);
        if (activeItems > 0)
        {
            parts.Add($"{activeItems.ToString("N0", CultureInfo.CurrentCulture)} active");
        }

        if (queuedItems > 0)
        {
            parts.Add($"{queuedItems.ToString("N0", CultureInfo.CurrentCulture)} still in pipeline");
        }

        if (reviewItems > 0)
        {
            parts.Add($"{reviewItems.ToString("N0", CultureInfo.CurrentCulture)} need review");
        }

        return parts.Count > 0
            ? string.Join(" - ", parts)
            : "Waiting for the next pipeline update";
    }

    private static (string Label, string Tone) ResolveStatusPill(LibraryUpdatePageState pageState, int reviewItems) =>
        pageState switch
        {
            LibraryUpdatePageState.Running => ("In progress", "info"),
            LibraryUpdatePageState.Complete => ("Complete", reviewItems > 0 ? "warning" : "success"),
            LibraryUpdatePageState.Idle => ("Ready", reviewItems > 0 ? "warning" : "success"),
            LibraryUpdatePageState.Interrupted => ("Interrupted", "warning"),
            LibraryUpdatePageState.Failed => ("Could not finish", "danger"),
            LibraryUpdatePageState.StatusUnavailable => ("Unavailable", "warning"),
            _ => ("Ready", "neutral"),
        };

    private static string ResolveHeading(LibraryUpdatePageState pageState) =>
        pageState switch
        {
            LibraryUpdatePageState.Running => "Updating your library",
            LibraryUpdatePageState.Complete => "Library update complete",
            LibraryUpdatePageState.Idle => "Library is up to date",
            LibraryUpdatePageState.Interrupted => "Library update interrupted",
            LibraryUpdatePageState.Failed => "Library update could not finish",
            LibraryUpdatePageState.StatusUnavailable => "Status temporarily unavailable",
            _ => "Ready to scan your library",
        };

    private static string ResolveMainLine(
        LibraryUpdatePageState pageState,
        int processedFiles,
        int totalFiles,
        int reviewItems,
        DateTimeOffset? lastCompletedAt,
        DateTimeOffset now)
    {
        if (pageState == LibraryUpdatePageState.Running && totalFiles > 0 && processedFiles >= totalFiles)
        {
            return "Metadata, artwork, people, and relationships are still being processed.";
        }

        if ((pageState is LibraryUpdatePageState.Running or LibraryUpdatePageState.Complete or LibraryUpdatePageState.Interrupted) && totalFiles > 0)
        {
            return $"{processedFiles.ToString("N0", CultureInfo.CurrentCulture)} of {totalFiles.ToString("N0", CultureInfo.CurrentCulture)} files checked";
        }

        return pageState switch
        {
            LibraryUpdatePageState.Running when totalFiles == 0 => "Looking for new media",
            LibraryUpdatePageState.Running or LibraryUpdatePageState.Complete => $"{processedFiles.ToString("N0", CultureInfo.CurrentCulture)} of {totalFiles.ToString("N0", CultureInfo.CurrentCulture)} files processed",
            LibraryUpdatePageState.Idle when lastCompletedAt.HasValue => $"Last scan completed {FormatRelativeLong(lastCompletedAt.Value, now)}",
            LibraryUpdatePageState.Idle when reviewItems > 0 => $"{reviewItems.ToString("N0", CultureInfo.CurrentCulture)} items need review before they can be fully added.",
            LibraryUpdatePageState.Interrupted => "The last update was interrupted before all work finished.",
            LibraryUpdatePageState.Failed => "Tuvima Library stopped before the update was complete.",
            LibraryUpdatePageState.StatusUnavailable => "Tuvima Library could not refresh the latest status.",
            _ => "Start a scan to find new or changed media.",
        };
    }

    private static string ResolveTimestampLine(
        LibraryUpdatePageState pageState,
        DateTimeOffset? lastCompletedAt,
        DateTimeOffset? lastUpdated,
        DateTimeOffset now)
    {
        return pageState switch
        {
            LibraryUpdatePageState.Running => "Updated just now",
            LibraryUpdatePageState.Complete or LibraryUpdatePageState.Idle when lastCompletedAt.HasValue => $"Last scan completed {FormatRelativeLong(lastCompletedAt.Value, now)}",
            LibraryUpdatePageState.Interrupted when lastUpdated.HasValue => $"Last update changed {FormatRelativeShort(lastUpdated.Value, now)}",
            LibraryUpdatePageState.StatusUnavailable when lastUpdated.HasValue => $"Last known update: {FormatRelativeShort(lastUpdated.Value, now)}",
            _ => "No recent library update",
        };
    }

    private static string ResolvePanelTitle(LibraryUpdatePageState pageState, bool hasPriorRun) =>
        pageState switch
        {
            LibraryUpdatePageState.Running => "What's happening now",
            LibraryUpdatePageState.NoPriorRun when !hasPriorRun => "No library updates have run yet",
            _ => "Last update summary",
        };

    private static string ResolvePanelText(
        LibraryUpdatePageState pageState,
        DateTimeOffset? lastCompletedAt,
        DateTimeOffset now)
    {
        return pageState switch
        {
            LibraryUpdatePageState.Running => "Tuvima Library is matching new media against retail providers, then confirming identity with Wikidata.",
            LibraryUpdatePageState.Complete => "Tuvima Library finished scanning and matching your library.",
            LibraryUpdatePageState.Idle when lastCompletedAt.HasValue => $"Your library is ready. The most recent scan finished {FormatRelativeLong(lastCompletedAt.Value, now)}.",
            LibraryUpdatePageState.Interrupted => "Tuvima Library stopped receiving fresh work updates before this update reached a clean finish.",
            LibraryUpdatePageState.Failed => "Tuvima Library stopped before the update was complete.",
            LibraryUpdatePageState.StatusUnavailable => "Tuvima Library could not refresh the latest status. Last known counts are still shown.",
            _ => "No library updates have run yet.",
        };
    }

    private static IReadOnlyList<LibraryUpdateStepViewModel> BuildLibraryUpdateSteps(
        LibraryUpdatePageState pageState,
        int activeStep,
        int reviewItems)
    {
        var labels = new[]
        {
            ("Scanned folders", AppMaterialIcons.Outlined.FolderOpen),
            ("Read details", AppMaterialIcons.Outlined.MenuBook),
            ("Matched identity", AppMaterialIcons.Outlined.Link),
            ("Enriched metadata", AppMaterialIcons.Outlined.AutoAwesome),
            ("Saved to library", AppMaterialIcons.Outlined.Storage),
        };

        return labels
            .Select((step, index) =>
            {
                var status = ResolveStepStatus(pageState, index, activeStep, reviewItems);
                return new LibraryUpdateStepViewModel(step.Item1, step.Item2, status, StepStatusLabel(status));
            })
            .ToList();
    }

    private static LibraryUpdateStepStatus ResolveStepStatus(
        LibraryUpdatePageState pageState,
        int stepIndex,
        int activeStep,
        int reviewItems)
    {
        return pageState switch
        {
            LibraryUpdatePageState.NoPriorRun => LibraryUpdateStepStatus.Pending,
            LibraryUpdatePageState.Complete or LibraryUpdatePageState.Idle => reviewItems > 0 && stepIndex == 2
                ? LibraryUpdateStepStatus.NeedsReview
                : LibraryUpdateStepStatus.Complete,
            LibraryUpdatePageState.Failed or LibraryUpdatePageState.Interrupted => stepIndex < activeStep
                ? LibraryUpdateStepStatus.Complete
                : stepIndex == activeStep ? LibraryUpdateStepStatus.NeedsReview : LibraryUpdateStepStatus.Pending,
            LibraryUpdatePageState.StatusUnavailable => stepIndex < activeStep
                ? LibraryUpdateStepStatus.Complete
                : stepIndex == activeStep ? LibraryUpdateStepStatus.NeedsReview : LibraryUpdateStepStatus.Pending,
            _ => stepIndex < activeStep
                ? LibraryUpdateStepStatus.Complete
                : stepIndex == activeStep ? LibraryUpdateStepStatus.InProgress : LibraryUpdateStepStatus.Pending,
        };
    }

    private static string StepStatusLabel(LibraryUpdateStepStatus status) =>
        status switch
        {
            LibraryUpdateStepStatus.Complete => "Complete",
            LibraryUpdateStepStatus.InProgress => "In progress",
            LibraryUpdateStepStatus.NeedsReview => "Needs review",
            _ => "Pending",
        };

    private static IReadOnlyList<LibraryUpdateAttentionReasonViewModel> BuildAttentionReasons(
        IReadOnlyList<IngestionReviewReasonDto> reasons,
        int reviewItems)
    {
        if (reviewItems <= 0)
        {
            return [];
        }

        var uncertain = SumReasonCounts(reasons, "low_confidence");
        var unmatched = SumReasonCounts(reasons, "unmatched");
        var missingIdentity = SumReasonCounts(reasons, "missing_wikidata");
        var duplicates = SumReasonCounts(reasons, "duplicates");
        var missingArtwork = SumReasonCounts(reasons, "missing_artwork");
        var providerFailures = SumReasonCounts(reasons, "provider_failures");
        var rows = new List<LibraryUpdateAttentionReasonViewModel>();

        AddReason(rows, uncertain, "uncertain matches");
        AddReason(rows, unmatched, "unmatched items");
        AddReason(rows, missingIdentity, "missing identity");
        AddReason(rows, duplicates, "possible duplicates");
        AddReason(rows, missingArtwork, "missing artwork");
        AddReason(rows, providerFailures, "provider failures");

        if (rows.Count == 0)
        {
            AddReason(rows, reviewItems, "items awaiting review");
        }

        return rows;
    }

    private static int SumReasonCounts(IReadOnlyList<IngestionReviewReasonDto> reasons, params string[] keys) =>
        reasons
            .Where(reason => keys.Any(key => reason.Key.Equals(key, StringComparison.OrdinalIgnoreCase)))
            .Sum(reason => Math.Max(0, reason.Count));

    private static void AddReason(List<LibraryUpdateAttentionReasonViewModel> rows, int count, string label)
    {
        if (count > 0)
        {
            rows.Add(new LibraryUpdateAttentionReasonViewModel(count, label));
        }
    }

    private static IReadOnlyList<LibraryUpdateEnrichmentStatViewModel> BuildEnrichmentStats(
        IReadOnlyList<IngestionCurrentActivityDto> currentActivities,
        IngestionOperationsSnapshotDto? snapshot)
    {
        var rows = currentActivities
            .Where(IsEnrichmentActivity)
            .GroupBy(activity => NormalizeEnrichmentKey(activity), StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var activities = group.ToList();
                var found = activities.Sum(activity => Math.Max(0, activity.ProcessedCount));
                var active = activities.Sum(activity => Math.Max(0, activity.ActiveCount));
                var queued = activities.Sum(activity => Math.Max(0, activity.QueuedCount));
                var key = group.Key;
                return new LibraryUpdateEnrichmentStatViewModel(
                    key,
                    EnrichmentLabel(key),
                    EnrichmentIcon(key),
                    EnrichmentTone(key),
                    found,
                    0,
                    active,
                    queued,
                    activities.Select(activity => CleanDisplayTitle(StringHelpers.FirstNonBlankOr("", activity.CurrentItem, activity.SampleItems.FirstOrDefault())))
                        .Where(value => !string.IsNullOrWhiteSpace(value))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Take(3)
                        .ToList());
            })
            .Where(stat => stat.CompletedCount > 0 || stat.ActiveCount > 0)
            .OrderBy(stat => EnrichmentSort(stat.Key))
            .ToList();

        if (rows.Count > 0)
        {
            return rows;
        }

        var enriched = Count(snapshot, "enriched");
        var enrichmentTotal = Total(snapshot, "enriched", 0);
        return enrichmentTotal > 0 || enriched > 0
            ?
            [
                new LibraryUpdateEnrichmentStatViewModel(
                    "metadata",
                    "Metadata",
                    AppMaterialIcons.Outlined.AutoAwesome,
                    "purple",
                    enriched,
                    0,
                    0,
                    0,
                    [])
            ]
            : [];
    }

    private static bool IsEnrichmentActivity(IngestionCurrentActivityDto activity)
    {
        var value = StringHelpers.FirstNonBlankOr("", activity.StageKey, activity.Message);
        return value.Contains("art", StringComparison.OrdinalIgnoreCase)
            || value.Contains("cover", StringComparison.OrdinalIgnoreCase)
            || value.Contains("people", StringComparison.OrdinalIgnoreCase)
            || value.Contains("cast", StringComparison.OrdinalIgnoreCase)
            || value.Contains("relationship", StringComparison.OrdinalIgnoreCase)
            || value.Contains("series", StringComparison.OrdinalIgnoreCase)
            || value.Contains("description", StringComparison.OrdinalIgnoreCase)
            || value.Contains("metadata", StringComparison.OrdinalIgnoreCase)
            || value.Contains("enrich", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeEnrichmentKey(IngestionCurrentActivityDto activity)
    {
        var value = StringHelpers.FirstNonBlankOr("", activity.StageKey, activity.Message).ToLowerInvariant();
        if (value.Contains("art") || value.Contains("cover"))
        {
            return "artwork";
        }

        if (value.Contains("people") || value.Contains("cast"))
        {
            return "people";
        }

        if (value.Contains("relationship") || value.Contains("series") || value.Contains("universe"))
        {
            return "relationships";
        }

        if (value.Contains("description"))
        {
            return "descriptions";
        }

        return "metadata";
    }

    private static int EnrichmentSort(string key) => key.ToLowerInvariant() switch
    {
        "artwork" => 10,
        "people" => 20,
        "relationships" => 30,
        "descriptions" => 40,
        _ => 90,
    };

    private static string EnrichmentLabel(string key) => key.ToLowerInvariant() switch
    {
        "artwork" => "Artwork",
        "people" => "People",
        "relationships" => "Relationships",
        "descriptions" => "Descriptions",
        _ => "Metadata",
    };

    private static string EnrichmentIcon(string key) => key.ToLowerInvariant() switch
    {
        "artwork" => AppMaterialIcons.Outlined.ImageSearch,
        "people" => AppMaterialIcons.Outlined.Groups,
        "relationships" => AppMaterialIcons.Outlined.Hub,
        "descriptions" => AppMaterialIcons.Outlined.Article,
        _ => AppMaterialIcons.Outlined.AutoAwesome,
    };

    private static string EnrichmentTone(string key) => key.ToLowerInvariant() switch
    {
        "artwork" => "pink",
        "people" => "amber",
        "relationships" => "green",
        "descriptions" => "blue",
        _ => "purple",
    };

    private static IReadOnlyList<LibraryUpdateRecentItemViewModel> BuildRecentItems(
        IReadOnlyList<ActivityEntryResponse> recentActivity,
        IReadOnlyList<IngestionCurrentActivityDto> currentActivities)
    {
        var activityItems = recentActivity
            .Where(IsRecentLibraryUpdateActivity)
            .Select(ToRecentItem)
            .Where(item => !string.IsNullOrWhiteSpace(item.Title))
            .Take(5)
            .ToList();

        if (activityItems.Count > 0)
        {
            return activityItems;
        }

        return currentActivities
            .SelectMany(activity => activity.CurrentBatch?.CompletedPreview ?? [])
            .Concat(currentActivities.SelectMany(activity => activity.SampleItems))
            .Select(CleanDisplayTitle)
            .Where(title => !string.IsNullOrWhiteSpace(title))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(5)
            .Select(title => new LibraryUpdateRecentItemViewModel(
                title,
                "Added or updated in this library update",
                "Updated",
                "success",
                "Just now",
                null,
                null))
            .ToList();
    }

    private static IReadOnlyList<LibraryUpdateRunSummaryViewModel> BuildRecentRuns(
        IReadOnlyList<IngestionOperationsBatchDto> batches,
        DateTimeOffset now)
    {
        return batches
            .OrderByDescending(batch => batch.StartedAt)
            .Take(5)
            .Select(batch =>
            {
                var reviewCount = Math.Max(0, batch.ReviewCount);
                var statusTone = IsActiveBatchStatus(batch.Status)
                    ? "info"
                    : IsFailedBatchStatus(batch.Status)
                        ? "danger"
                        : IsInterruptedBatchStatus(batch.Status)
                            ? "warning"
                            : reviewCount > 0 ? "warning" : "success";
                var statusLabel = IsActiveBatchStatus(batch.Status)
                    ? "Active"
                    : IsFailedBatchStatus(batch.Status)
                        ? "Failed"
                        : IsInterruptedBatchStatus(batch.Status)
                            ? "Interrupted"
                            : "Complete";
                var processed = batch.ProcessedFiles > 0
                    ? Math.Min(Math.Max(0, batch.ProcessedFiles), Math.Max(0, batch.TotalFiles))
                    : Math.Min(Math.Max(0, batch.RegisteredCount + batch.ReviewCount), Math.Max(0, batch.TotalFiles));
                var title = string.IsNullOrWhiteSpace(batch.Source)
                    ? "Library update"
                    : $"{batch.Source} update";
                var statusText = $"{DisplayFormat.FormatCountClamped(processed)} of {DisplayFormat.FormatCountClamped(batch.TotalFiles)} files finished";
                var durationText = batch.DurationSeconds is > 0
                    ? DisplayFormat.FormatDurationCompactFloor(TimeSpan.FromSeconds(batch.DurationSeconds.Value))
                    : IsActiveBatchStatus(batch.Status) ? "In progress" : "Duration unknown";

                return new LibraryUpdateRunSummaryViewModel(
                    title,
                    statusText,
                    statusLabel,
                    statusTone,
                    FormatRelativeShort(batch.StartedAt, now),
                    durationText,
                    [
                        new("Files", batch.TotalFiles, AppMaterialIcons.Outlined.Description, "purple"),
                        new("Matched", batch.RegisteredCount, AppMaterialIcons.Outlined.Link, "green"),
                        new("Review", reviewCount, AppMaterialIcons.Outlined.WarningAmber, "amber"),
                        new("People", batch.PeopleGeneratedCount, AppMaterialIcons.Outlined.Groups, "blue"),
                        new("Artwork", batch.ArtworkDownloadedCount, AppMaterialIcons.Outlined.Collections, "pink"),
                        new("Metadata", batch.MetadataUpdatedCount, AppMaterialIcons.Outlined.AutoAwesome, "cyan"),
                    ]);
            })
            .ToList();
    }

    private static bool IsActiveBatchStatus(string? status) =>
        string.Equals(status, "running", StringComparison.OrdinalIgnoreCase)
        || string.Equals(status, "processing", StringComparison.OrdinalIgnoreCase)
        || string.Equals(status, "active", StringComparison.OrdinalIgnoreCase)
        || string.Equals(status, "queued", StringComparison.OrdinalIgnoreCase);

    private static bool IsRecentLibraryUpdateActivity(ActivityEntryResponse activity) =>
        activity.ActionType.Equals("MediaAdded", StringComparison.OrdinalIgnoreCase)
        || activity.ActionType.Equals("FileIngested", StringComparison.OrdinalIgnoreCase)
        || activity.ActionType.Equals("ReviewItemResolved", StringComparison.OrdinalIgnoreCase);

    private static LibraryUpdateRecentItemViewModel ToRecentItem(ActivityEntryResponse activity)
    {
        var rich = activity.GetRichData();
        var review = activity.GetReviewData();
        var title = CleanDisplayTitle(StringHelpers.FirstNonBlankOr("",
            rich?.Title,
            review?.Title,
            activity.CollectionName,
            ExtractTitleFromDetail(activity.Detail),
            "Library item"));
        var needsReview = rich?.NeedsReview == true || activity.ActionType.Contains("Review", StringComparison.OrdinalIgnoreCase);
        var hasArtwork = !string.IsNullOrWhiteSpace(rich?.ResolvedCoverUrl) || !string.IsNullOrWhiteSpace(review?.CoverUrl);
        var hasWikidata = !string.IsNullOrWhiteSpace(rich?.WikidataQid) || !string.IsNullOrWhiteSpace(review?.Qid);
        var statusText = needsReview
            ? "Added as provisional  -  Needs identity review"
            : hasWikidata
                ? hasArtwork ? "Matched  -  Artwork found  -  Wikidata confirmed" : "Matched  -  Wikidata confirmed"
                : hasArtwork ? "Matched  -  Artwork found" : "Added or updated";
        var statusLabel = needsReview ? "Provisional" : "Matched";
        var statusTone = needsReview ? "warning" : "success";

        return new LibraryUpdateRecentItemViewModel(
            title,
            statusText,
            statusLabel,
            statusTone,
            activity.GetRelativeTime(),
            rich?.ResolvedCoverUrl ?? review?.CoverUrl,
            ResolveDetailHref(activity));
    }

    private static string? ExtractTitleFromDetail(string? detail)
    {
        if (string.IsNullOrWhiteSpace(detail))
        {
            return null;
        }

        var trimmed = detail.Trim();
        var quoted = trimmed.Split('"', StringSplitOptions.RemoveEmptyEntries);
        if (quoted.Length >= 2)
        {
            return quoted[1];
        }

        return trimmed.Contains(':', StringComparison.Ordinal)
            ? trimmed[(trimmed.LastIndexOf(':') + 1)..].Trim()
            : trimmed;
    }

    private static string? ResolveDetailHref(ActivityEntryResponse activity)
    {
        var id = StringHelpers.FirstNonBlankOr(
            "",
            activity.EntityId,
            activity.GetRichData()?.EntityId,
            activity.GetReviewData()?.EntityId);
        if (!Guid.TryParse(id, out var entityId))
        {
            return null;
        }

        var entityType = StringHelpers.FirstNonBlankOr("", activity.EntityType, "work").ToLowerInvariant() switch
        {
            "tvshow" or "tv_show" or "show" => "tvshow",
            _ => "work",
        };
        return $"/details/{entityType}/{entityId:D}";
    }

    private static bool IsActiveJob(IngestionOperationsJobDto job) =>
        job.Status.Equals("running", StringComparison.OrdinalIgnoreCase)
        || job.Status.Equals("processing", StringComparison.OrdinalIgnoreCase)
        || job.Status.Equals("active", StringComparison.OrdinalIgnoreCase)
        || job.Status.Equals("queued", StringComparison.OrdinalIgnoreCase);

    private static bool IsFailedBatchStatus(string? status) =>
        !string.IsNullOrWhiteSpace(status)
        && status.Contains("fail", StringComparison.OrdinalIgnoreCase);

    private static bool IsInterruptedBatchStatus(string? status) =>
        !string.IsNullOrWhiteSpace(status)
        && (status.Contains("abandon", StringComparison.OrdinalIgnoreCase)
            || status.Contains("interrupt", StringComparison.OrdinalIgnoreCase));

    private static string ToFriendlyStepLabel(string value)
    {
        var normalized = value.Replace('_', ' ').Trim();
        if (normalized.Contains("quick metadata", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("hydrat", StringComparison.OrdinalIgnoreCase))
        {
            return "Adding retail metadata and primary artwork";
        }

        if (normalized.Contains("enhancer", StringComparison.OrdinalIgnoreCase))
        {
            return "Enriching artwork, people, and relationships";
        }

        if (normalized.Contains("people", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("cast", StringComparison.OrdinalIgnoreCase))
        {
            return "Enriching people and cast";
        }

        if (normalized.Contains("artwork", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("cover", StringComparison.OrdinalIgnoreCase))
        {
            return "Fetching artwork";
        }

        if (normalized.Contains("core universe", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("series", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("relationship", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("universe", StringComparison.OrdinalIgnoreCase))
        {
            return "Building series and relationships";
        }

        if (normalized.Contains("metadata", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("enrich", StringComparison.OrdinalIgnoreCase))
        {
            return "Enriching metadata and relationships";
        }

        if (normalized.Contains("wikidata", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("qid", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("identity", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("match", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("retail", StringComparison.OrdinalIgnoreCase))
        {
            return "Matching titles and identity";
        }

        if (normalized.Contains("scan", StringComparison.OrdinalIgnoreCase))
        {
            return "Scanning library folders";
        }

        if (normalized.Contains("read", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("parse", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("hash", StringComparison.OrdinalIgnoreCase))
        {
            return "Reading media details";
        }

        if (normalized.Contains("save", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("register", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("organize", StringComparison.OrdinalIgnoreCase))
        {
            return "Saving library records";
        }

        return CultureInfo.CurrentCulture.TextInfo.ToTitleCase(normalized);
    }

    private static bool IsQuickMetadataStage(string value) =>
        value.Contains("quick metadata", StringComparison.OrdinalIgnoreCase)
        || value.Contains("retail metadata", StringComparison.OrdinalIgnoreCase)
        || value.Contains("hydrat", StringComparison.OrdinalIgnoreCase);

    private static string CleanDisplayTitle(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var trimmed = value.Trim();
        var fileName = Path.GetFileName(trimmed);
        if (!string.IsNullOrWhiteSpace(fileName))
        {
            trimmed = fileName;
        }

        foreach (var mediaExtension in KnownMediaExtensions)
        {
            if (trimmed.EndsWith(mediaExtension, StringComparison.OrdinalIgnoreCase))
            {
                trimmed = trimmed[..^mediaExtension.Length];
                break;
            }
        }

        var extension = Path.GetExtension(trimmed);
        if (!string.IsNullOrWhiteSpace(extension)
            && extension.Length <= 6
            && extension.Skip(1).All(char.IsLetterOrDigit))
        {
            trimmed = Path.GetFileNameWithoutExtension(trimmed);
        }

        return trimmed.Replace('_', ' ');
    }

    private static readonly string[] KnownMediaExtensions =
    [
        ".epub",
        ".pdf",
        ".cbz",
        ".cbr",
        ".m4b",
        ".mp3",
        ".flac",
        ".mkv",
        ".mp4",
        ".avi",
        ".mov",
    ];

    private static string FormatRelativeLong(DateTimeOffset value, DateTimeOffset now)
    {
        var elapsed = now.ToUniversalTime() - value.ToUniversalTime();
        return elapsed.TotalMinutes switch
        {
            < 1 => "just now",
            < 60 => $"{(int)elapsed.TotalMinutes} minutes ago",
            < 120 => "1 hour ago",
            < 1440 => $"{(int)elapsed.TotalHours} hours ago",
            < 2880 => "1 day ago",
            _ => $"{(int)elapsed.TotalDays} days ago",
        };
    }

    private static string FormatRelativeShort(DateTimeOffset value, DateTimeOffset now)
    {
        var elapsed = now.ToUniversalTime() - value.ToUniversalTime();
        return elapsed.TotalMinutes switch
        {
            < 1 => "just now",
            < 60 => $"{(int)elapsed.TotalMinutes}m ago",
            < 1440 => $"{(int)elapsed.TotalHours}h ago",
            _ => value.ToLocalTime().ToString("MMM d, h:mm tt", CultureInfo.CurrentCulture),
        };
    }


}

public enum IngestionLiveMode
{
    Live,
    Reconnecting,
    Polling,
}

public sealed record IngestionDashboardMetrics(
    int TotalFiles,
    int ProcessedFiles,
    int ActiveFiles,
    int UnresolvedItems);

public sealed record IngestionOverallProgress(
    int ProcessedFiles,
    int TotalFiles,
    double Percent,
    string ActiveStageLabelKey,
    string ActiveStageDetailKey,
    int ActiveStageCount,
    int ActiveStageTotal,
    double ActiveStagePercent,
    int? EstimatedSecondsRemaining,
    int ItemsReady = 0);

public sealed record IngestionDashboardStage(
    string Key,
    string LabelKey,
    string DetailKey,
    string Icon,
    int Count,
    int Total,
    double Percent,
    string StatusKey,
    double RingPercent,
    bool HideCount,
    int MatchedCount,
    int ReviewCount,
    int OtherCount,
    bool IsSummary,
    int StageNumber = 0,
    string? ArtifactLabel = null,
    int? ArtifactCount = null,
    string? ActiveItemLabel = null,
    string? ActiveGroupLabel = null,
    int ActiveGroupCount = 0,
    string LabelAccuracy = "None",
    DateTimeOffset? LastUpdatedTime = null,
    bool IsStale = false,
    string? StatusLabel = null,
    int ActiveCount = 0,
    int QueuedCount = 0,
    IReadOnlyList<IngestionStageDetailItemDto>? DetailItems = null);

public sealed record IngestionQueueHealthItem(
    string Key,
    string Label,
    int Count,
    string Tone);

public enum LibraryUpdatePageState
{
    NoPriorRun,
    Running,
    Complete,
    Idle,
    Interrupted,
    Failed,
    StatusUnavailable,
}

public enum LibraryUpdateStepStatus
{
    Complete,
    InProgress,
    Pending,
    NeedsReview,
}

public sealed record LibraryUpdateStatusViewModel(
    LibraryUpdatePageState PageState,
    int TotalFiles,
    int ProcessedFiles,
    int MatchedItems,
    int EnrichmentCompleted,
    int EnrichmentTotal,
    double EnrichmentPercent,
    IReadOnlyList<LibraryUpdateEnrichmentStatViewModel> EnrichmentStats,
    int ReviewItems,
    int ExpectedReviewItems,
    int UnexpectedReviewItems,
    int ActiveItems,
    int QueuedItems,
    int AddedOrUpdatedCount,
    double ProgressPercent,
    bool ShowProgress,
    bool IsIndeterminate,
    string Heading,
    string MainLine,
    string ActivityLine,
    string? SecondaryLine,
    string TimestampLine,
    string StatusLabel,
    string StatusTone,
    string PanelTitle,
    string PanelText,
    string? CurrentItemTitle,
    string CurrentStep,
    string? CurrentProvider,
    IReadOnlyList<LibraryUpdateStepViewModel> Steps,
    IReadOnlyList<LibraryUpdateAttentionReasonViewModel> AttentionReasons,
    IReadOnlyList<LibraryUpdateRecentItemViewModel> RecentItems,
    IReadOnlyList<LibraryUpdateRunSummaryViewModel> RecentRuns,
    bool HasPriorRun);

public sealed record LibraryUpdateEnrichmentStatViewModel(
    string Key,
    string Label,
    string Icon,
    string Tone,
    int CompletedCount,
    int TotalCount,
    int ActiveCount,
    int QueuedCount,
    IReadOnlyList<string> SampleItems);

public sealed record LibraryUpdateStepViewModel(
    string Label,
    string Icon,
    LibraryUpdateStepStatus Status,
    string StatusLabel);

public sealed record LibraryUpdateAttentionReasonViewModel(int Count, string Label);

public sealed record LibraryUpdateRecentItemViewModel(
    string Title,
    string StatusText,
    string StatusLabel,
    string StatusTone,
    string RelativeTime,
    string? ThumbnailUrl,
    string? Href);

public sealed record LibraryUpdateRunSummaryViewModel(
    string Title,
    string StatusText,
    string StatusLabel,
    string StatusTone,
    string RelativeTime,
    string DurationText,
    IReadOnlyList<LibraryUpdateRunStatViewModel> Stats);

public sealed record LibraryUpdateRunStatViewModel(
    string Label,
    int Count,
    string Icon,
    string Tone);
