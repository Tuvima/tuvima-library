using System.Text.Json.Serialization;

namespace MediaEngine.Contracts.Ingestion;

public sealed class IngestionOperationsSnapshotDto
{
    public List<IngestionRetryWaitDto> RetryWaits { get; set; } = [];
    [JsonPropertyName("summary")]
    public IngestionOperationsSummaryDto Summary { get; set; } = new();

    [JsonPropertyName("active_jobs")]
    public List<IngestionOperationsJobDto> ActiveJobs { get; set; } = [];

    [JsonPropertyName("recent_identity_jobs")]
    public List<IngestionOperationsJobDto> RecentIdentityJobs { get; set; } = [];

    [JsonPropertyName("current_activities")]
    public List<IngestionCurrentActivityDto> CurrentActivities { get; set; } = [];

    [JsonPropertyName("pipeline_stages")]
    public List<IngestionPipelineStageDto> PipelineStages { get; set; } = [];

    [JsonPropertyName("stage_progress")]
    public List<IngestionStageProgressDto> StageProgress { get; set; } = [];

    [JsonPropertyName("review_reasons")]
    public List<IngestionReviewReasonDto> ReviewReasons { get; set; } = [];

    [JsonPropertyName("source_groups")]
    public List<IngestionSourceGroupDto> SourceGroups { get; set; } = [];

    [JsonPropertyName("provider_health")]
    public List<IngestionProviderHealthDto> ProviderHealth { get; set; } = [];

    [JsonPropertyName("provider_activity")]
    public List<IngestionProviderActivityDto> ProviderActivity { get; set; } = [];

    [JsonPropertyName("recent_batches")]
    public List<IngestionOperationsBatchDto> RecentBatches { get; set; } = [];

    [JsonPropertyName("organization")]
    public IngestionOrganizationRulesDto Organization { get; set; } = new();

    [JsonPropertyName("generated_at")]
    public DateTimeOffset GeneratedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class IngestionRetryWaitDto
{
    public string MediaType { get; set; } = "";
    public int Count { get; set; }
    public int Attempts { get; set; }
    public string Reason { get; set; } = "";
    public DateTimeOffset? NextAttemptAt { get; set; }
}

public sealed class IngestionProviderActivityDto
{
    [JsonPropertyName("provider_name")]
    public string ProviderName { get; set; } = "";

    [JsonPropertyName("active_requests")]
    public int ActiveRequests { get; set; }

    [JsonPropertyName("waiting_requests")]
    public int WaitingRequests { get; set; }

    [JsonPropertyName("requests_total")]
    public long RequestsTotal { get; set; }

    [JsonPropertyName("requests_last_minute")]
    public int RequestsLastMinute { get; set; }

    [JsonPropertyName("max_active_last_minute")]
    public int MaxActiveLastMinute { get; set; }

    [JsonPropertyName("errors_total")]
    public long ErrorsTotal { get; set; }

    [JsonPropertyName("errors_last_minute")]
    public int ErrorsLastMinute { get; set; }

    [JsonPropertyName("throttle_wait_ms_total")]
    public long ThrottleWaitMsTotal { get; set; }

    [JsonPropertyName("wait_ms_last_minute")]
    public long WaitMsLastMinute { get; set; }

    [JsonPropertyName("average_wait_ms")]
    public double AverageWaitMs { get; set; }

    [JsonPropertyName("average_latency_ms")]
    public double AverageLatencyMs { get; set; }

    [JsonPropertyName("last_success_at")]
    public DateTimeOffset? LastSuccessAt { get; set; }

    [JsonPropertyName("last_request_at")]
    public DateTimeOffset? LastRequestAt { get; set; }

    [JsonPropertyName("last_error")]
    public string? LastError { get; set; }
}

public sealed class IngestionOperationsSummaryDto
{
    [JsonPropertyName("total_items")]
    public int TotalItems { get; set; }

    [JsonPropertyName("registered_items")]
    public int RegisteredItems { get; set; }

    [JsonPropertyName("provisional_items")]
    public int ProvisionalItems { get; set; }

    [JsonPropertyName("items_needing_review")]
    public int ItemsNeedingReview { get; set; }

    [JsonPropertyName("active_jobs")]
    public int ActiveJobs { get; set; }

    [JsonPropertyName("failed_jobs")]
    public int FailedJobs { get; set; }

    [JsonPropertyName("provider_warnings")]
    public int ProviderWarnings { get; set; }

    [JsonPropertyName("last_successful_scan_time")]
    public DateTimeOffset? LastSuccessfulScanTime { get; set; }

    [JsonPropertyName("engine_status")]
    public string EngineStatus { get; set; } = "Online";

    [JsonPropertyName("health_label")]
    public string HealthLabel { get; set; } = "Healthy";

    [JsonPropertyName("expected_outcomes")]
    public IngestionExpectedOutcomesDto? ExpectedOutcomes { get; set; }
}

public sealed class IngestionExpectedOutcomesDto
{
    [JsonPropertyName("total_files")]
    public int TotalFiles { get; set; }

    [JsonPropertyName("expected_resolved")]
    public int ExpectedResolved { get; set; }

    [JsonPropertyName("expected_exact_qid")]
    public int ExpectedExactQid { get; set; }

    [JsonPropertyName("expected_any_qid")]
    public int ExpectedAnyQid { get; set; }

    [JsonPropertyName("expected_review")]
    public int ExpectedReview { get; set; }

    [JsonPropertyName("expected_known_no_qid")]
    public int ExpectedKnownNoQid { get; set; }

    [JsonPropertyName("expected_duplicate")]
    public int ExpectedDuplicate { get; set; }

    [JsonPropertyName("expected_skipped")]
    public int ExpectedSkipped { get; set; }

    [JsonPropertyName("expected_corrupt")]
    public int ExpectedCorrupt { get; set; }
}

public sealed class IngestionOperationsJobDto
{
    [JsonPropertyName("job_id")]
    public Guid JobId { get; set; }

    [JsonPropertyName("job_type")]
    public string JobType { get; set; } = "Ingestion batch";

    [JsonPropertyName("media_type")]
    public string? MediaType { get; set; }

    [JsonPropertyName("source_folder")]
    public string? SourceFolder { get; set; }

    [JsonPropertyName("current_stage")]
    public string CurrentStage { get; set; } = "Processing";

    [JsonPropertyName("current_item")]
    public string? CurrentItem { get; set; }

    [JsonPropertyName("processed_count")]
    public int ProcessedCount { get; set; }

    [JsonPropertyName("total_count")]
    public int TotalCount { get; set; }

    [JsonPropertyName("count_unit")]
    public string CountUnit { get; set; } = "files";

    [JsonPropertyName("percent_complete")]
    public double PercentComplete { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = "running";

    [JsonPropertyName("elapsed")]
    public TimeSpan? Elapsed { get; set; }

    [JsonPropertyName("last_updated_time")]
    public DateTimeOffset? LastUpdatedTime { get; set; }

    [JsonPropertyName("warning_summary")]
    public string? WarningSummary { get; set; }
}

public sealed class IngestionCurrentActivityDto
{
    [JsonPropertyName("stage_key")]
    public string StageKey { get; set; } = "";

    [JsonPropertyName("message")]
    public string Message { get; set; } = "";

    [JsonPropertyName("detail")]
    public string Detail { get; set; } = "";

    [JsonPropertyName("current_item")]
    public string? CurrentItem { get; set; }

    [JsonPropertyName("source")]
    public string? Source { get; set; }

    [JsonPropertyName("processed_count")]
    public int ProcessedCount { get; set; }

    [JsonPropertyName("total_count")]
    public int TotalCount { get; set; }

    [JsonPropertyName("count_unit")]
    public string CountUnit { get; set; } = "files";

    [JsonPropertyName("percent_complete")]
    public double PercentComplete { get; set; }

    [JsonPropertyName("last_updated_time")]
    public DateTimeOffset? LastUpdatedTime { get; set; }

    [JsonPropertyName("queued_count")]
    public int QueuedCount { get; set; }

    [JsonPropertyName("active_count")]
    public int ActiveCount { get; set; }

    [JsonPropertyName("sample_items")]
    public List<string> SampleItems { get; set; } = [];

    [JsonPropertyName("metric_label")]
    public string? MetricLabel { get; set; }

    [JsonPropertyName("metric_value")]
    public string? MetricValue { get; set; }

    [JsonPropertyName("metric_tone")]
    public string? MetricTone { get; set; }

    [JsonPropertyName("current_batch")]
    public IngestionActivityBatchDto? CurrentBatch { get; set; }
}

public sealed class IngestionActivityBatchDto
{
    [JsonPropertyName("batch_number")]
    public int BatchNumber { get; set; }

    [JsonPropertyName("batch_size")]
    public int BatchSize { get; set; }

    [JsonPropertyName("total_batches")]
    public int TotalBatches { get; set; }

    [JsonPropertyName("completed_count")]
    public int CompletedCount { get; set; }

    [JsonPropertyName("active_count")]
    public int ActiveCount { get; set; }

    [JsonPropertyName("pending_count")]
    public int PendingCount { get; set; }

    [JsonPropertyName("review_count")]
    public int ReviewCount { get; set; }

    [JsonPropertyName("active_items")]
    public List<string> ActiveItems { get; set; } = [];

    [JsonPropertyName("completed_preview")]
    public List<string> CompletedPreview { get; set; } = [];

    [JsonPropertyName("pending_preview")]
    public List<string> PendingPreview { get; set; } = [];

    [JsonPropertyName("review_preview")]
    public List<string> ReviewPreview { get; set; } = [];
}

public sealed class IngestionPipelineStageDto
{
    [JsonPropertyName("key")]
    public string Key { get; set; } = "";

    [JsonPropertyName("label")]
    public string Label { get; set; } = "";

    [JsonPropertyName("count")]
    public int Count { get; set; }

    [JsonPropertyName("total_count")]
    public int TotalCount { get; set; }

    [JsonPropertyName("helper")]
    public string Helper { get; set; } = "";
}

public sealed class IngestionStageProgressDto
{
    [JsonPropertyName("stage_number")]
    public int StageNumber { get; set; }

    [JsonPropertyName("stage_key")]
    public string StageKey { get; set; } = "";

    [JsonPropertyName("label")]
    public string Label { get; set; } = "";

    [JsonPropertyName("completed_files")]
    public int CompletedFiles { get; set; }

    [JsonPropertyName("total_files")]
    public int TotalFiles { get; set; }

    [JsonPropertyName("percent_complete")]
    public double PercentComplete { get; set; }

    [JsonPropertyName("active_count")]
    public int ActiveCount { get; set; }

    [JsonPropertyName("queued_count")]
    public int QueuedCount { get; set; }

    [JsonPropertyName("status_label")]
    public string? StatusLabel { get; set; }

    [JsonPropertyName("active_item_label")]
    public string? ActiveItemLabel { get; set; }

    [JsonPropertyName("active_group_label")]
    public string? ActiveGroupLabel { get; set; }

    [JsonPropertyName("active_group_count")]
    public int ActiveGroupCount { get; set; }

    [JsonPropertyName("label_accuracy")]
    public string LabelAccuracy { get; set; } = "None";

    [JsonPropertyName("artifact_label")]
    public string? ArtifactLabel { get; set; }

    [JsonPropertyName("artifact_count")]
    public int? ArtifactCount { get; set; }

    [JsonPropertyName("last_updated_time")]
    public DateTimeOffset? LastUpdatedTime { get; set; }

    [JsonPropertyName("is_stale")]
    public bool IsStale { get; set; }

    [JsonPropertyName("detail_items")]
    public List<IngestionStageDetailItemDto> DetailItems { get; set; } = [];
}

public sealed class IngestionStageDetailItemDto
{
    [JsonPropertyName("label")]
    public string Label { get; set; } = "";

    [JsonPropertyName("value")]
    public string Value { get; set; } = "";

    [JsonPropertyName("tone")]
    public string? Tone { get; set; }

    [JsonPropertyName("icon")]
    public string? Icon { get; set; }
}

public sealed class IngestionReviewReasonDto
{
    [JsonPropertyName("key")]
    public string Key { get; set; } = "";

    [JsonPropertyName("label")]
    public string Label { get; set; } = "";

    [JsonPropertyName("count")]
    public int Count { get; set; }

    [JsonPropertyName("explanation")]
    public string Explanation { get; set; } = "";
}

public sealed class IngestionSourceGroupDto
{
    [JsonPropertyName("intent")]
    public string Intent { get; set; } = "";

    [JsonPropertyName("libraries")]
    public List<IngestionSourceLibraryDto> Libraries { get; set; } = [];
}

public sealed class IngestionSourceLibraryDto
{
    [JsonPropertyName("label")]
    public string Label { get; set; } = "";

    [JsonPropertyName("folders")]
    public List<IngestionSourceFolderDto> Folders { get; set; } = [];
}

public sealed class IngestionSourceFolderDto
{
    [JsonPropertyName("path")]
    public string Path { get; set; } = "";

    [JsonPropertyName("media_type")]
    public string MediaType { get; set; } = "";

    [JsonPropertyName("purpose")]
    public string Purpose { get; set; } = "primary";

    [JsonPropertyName("scan_mode")]
    public string ScanMode { get; set; } = "automatic";

    [JsonPropertyName("last_scan")]
    public DateTimeOffset? LastScan { get; set; }

    [JsonPropertyName("item_count")]
    public int ItemCount { get; set; }

    [JsonPropertyName("unresolved_count")]
    public int UnresolvedCount { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = "Unknown";

    [JsonPropertyName("is_reachable")]
    public bool? IsReachable { get; set; }

    [JsonPropertyName("permissions_valid")]
    public bool? PermissionsValid { get; set; }

    [JsonPropertyName("music_note")]
    public string? MusicNote { get; set; }
}

public sealed class IngestionProviderHealthDto
{
    [JsonPropertyName("provider_id")]
    public string ProviderId { get; set; } = "";

    [JsonPropertyName("display_name")]
    public string DisplayName { get; set; } = "";

    [JsonPropertyName("status")]
    public string Status { get; set; } = "Unknown";

    [JsonPropertyName("last_successful_call")]
    public DateTimeOffset? LastSuccessfulCall { get; set; }

    [JsonPropertyName("last_error")]
    public string? LastError { get; set; }

    [JsonPropertyName("warning")]
    public string? Warning { get; set; }
}

public sealed class IngestionOperationsBatchDto
{
    [JsonPropertyName("batch_id")]
    public Guid BatchId { get; set; }

    [JsonPropertyName("started_at")]
    public DateTimeOffset StartedAt { get; set; }

    [JsonPropertyName("completed_at")]
    public DateTimeOffset? CompletedAt { get; set; }

    [JsonPropertyName("source")]
    public string? Source { get; set; }

    [JsonPropertyName("media_type")]
    public string? MediaType { get; set; }

    [JsonPropertyName("total_files")]
    public int TotalFiles { get; set; }

    [JsonPropertyName("processed_files")]
    public int ProcessedFiles { get; set; }

    [JsonPropertyName("movies_count")]
    public int MoviesCount { get; set; }

    [JsonPropertyName("tv_shows_count")]
    public int TvShowsCount { get; set; }

    [JsonPropertyName("books_count")]
    public int BooksCount { get; set; }

    [JsonPropertyName("audiobooks_count")]
    public int AudiobooksCount { get; set; }

    [JsonPropertyName("music_count")]
    public int MusicCount { get; set; }

    [JsonPropertyName("comics_count")]
    public int ComicsCount { get; set; }

    [JsonPropertyName("registered_count")]
    public int RegisteredCount { get; set; }

    [JsonPropertyName("review_count")]
    public int ReviewCount { get; set; }

    [JsonPropertyName("failed_count")]
    public int FailedCount { get; set; }

    [JsonPropertyName("outstanding_operations")]
    public int OutstandingOperations { get; set; }

    [JsonPropertyName("active_operations")]
    public int ActiveOperations { get; set; }

    [JsonPropertyName("queued_operations")]
    public int QueuedOperations { get; set; }

    [JsonPropertyName("retry_waiting_operations")]
    public int RetryWaitingOperations { get; set; }

    [JsonPropertyName("people_generated_count")]
    public int PeopleGeneratedCount { get; set; }

    [JsonPropertyName("artwork_downloaded_count")]
    public int ArtworkDownloadedCount { get; set; }

    [JsonPropertyName("metadata_updated_count")]
    public int MetadataUpdatedCount { get; set; }

    [JsonPropertyName("duration_seconds")]
    public int? DurationSeconds { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = "";

    [JsonPropertyName("summary")]
    public string Summary { get; set; } = "";

    [JsonPropertyName("stage_progress")]
    public List<IngestionStageProgressDto> StageProgress { get; set; } = [];
}

public sealed class IngestionOrganizationRulesDto
{
    [JsonPropertyName("rename_enabled")]
    public bool RenameEnabled { get; set; }

    [JsonPropertyName("move_enabled")]
    public bool MoveEnabled { get; set; }

    [JsonPropertyName("preview_required")]
    public bool PreviewRequired { get; set; } = true;

    [JsonPropertyName("folder_template_summary")]
    public string FolderTemplateSummary { get; set; } = "Not configured";

    [JsonPropertyName("filename_template_summary")]
    public string FilenameTemplateSummary { get; set; } = "Not configured";

    [JsonPropertyName("last_organization_run")]
    public DateTimeOffset? LastOrganizationRun { get; set; }

    [JsonPropertyName("music_behavior")]
    public string MusicBehavior { get; set; } = "Music keeps album folder structure conservative.";
}
