using System.Text.Json.Serialization;

namespace MediaEngine.Contracts.Realtime;

public sealed record IngestionStartedEvent(
    string FilePath,
    DateTimeOffset StartedAt);

public sealed record IngestionHashedEvent(
    string FilePath,
    string ContentHash,
    long FileSizeBytes,
    TimeSpan Elapsed);

public sealed record IngestionCompletedEvent(
    string FilePath,
    string MediaType,
    DateTimeOffset CompletedAt);

public sealed record AutoOrganizedIngestionCompletedEvent(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("media_type")] string MediaType,
    [property: JsonPropertyName("timestamp")] DateTimeOffset Timestamp);

public sealed record IngestionFailedEvent(
    string FilePath,
    string Reason,
    DateTimeOffset FailedAt);

public sealed record MediaAddedEvent(
    Guid WorkId,
    Guid? CollectionId,
    string MediaType,
    string Title,
    Guid? AssetId = null);

public sealed record WatchFolderActiveEvent(
    string WatchDirectory,
    DateTimeOffset ActivatedAt);

public sealed record IngestionProgressEvent(
    string CurrentFile,
    int ProcessedCount,
    int TotalCount,
    string Stage);

public sealed record IngestionItemProgressEvent(
    Guid BatchId,
    Guid LogEntryId,
    Guid? MediaAssetId,
    string FilePath,
    string FileName,
    string Stage,
    int StageOrder,
    int ProgressPercent,
    bool IsTerminal,
    string? Title = null,
    string? MediaType = null);

public sealed record BatchProgressEvent(
    Guid BatchId,
    int FilesTotal,
    int FilesProcessed,
    int FilesIdentified,
    int FilesReview,
    int FilesNoMatch,
    int FilesFailed,
    int ProgressPercent,
    int? EstimatedSecondsRemaining,
    bool IsComplete,
    IReadOnlyList<string>? RecentTitles = null,
    string? CurrentStage = null,
    int FilesQueued = 0,
    int FilesActive = 0,
    int FilesReady = 0,
    int FilesReadyWithoutUniverse = 0,
    string? CurrentFileTitle = null,
    string? LifecycleStage = null,
    int WorkUnitsTotal = 0,
    int WorkUnitsCompleted = 0,
    bool ProgressIsIndeterminate = false);

public sealed record ProviderActivityEvent(
    IReadOnlyList<ProviderActivityItemEvent> Providers,
    DateTimeOffset CapturedAt);

public sealed record ProviderActivityItemEvent(
    string ProviderName,
    int ActiveRequests,
    int WaitingRequests,
    long RequestsTotal,
    int RequestsLastMinute,
    int MaxActiveLastMinute,
    long ErrorsTotal,
    int ErrorsLastMinute,
    long ThrottleWaitMsTotal,
    long WaitMsLastMinute,
    double AverageWaitMs,
    double AverageLatencyMs,
    DateTimeOffset? LastSuccessAt,
    DateTimeOffset? LastRequestAt,
    string? LastError);

public sealed record MediaRemovedEvent(
    [property: JsonPropertyName("asset_id")] Guid AssetId,
    [property: JsonPropertyName("file_path")] string FilePath,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("library_id")] Guid? LibraryId = null,
    [property: JsonPropertyName("feature_id")] string? FeatureId = null);

public sealed record ReconciliationLibraryChangedEvent(
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("removed_count")] int RemovedCount,
    [property: JsonPropertyName("merged_count")] int MergedCount,
    [property: JsonPropertyName("collection_assignments_repaired")] int CollectionAssignmentsRepaired);

/// <summary>
/// Payload-agnostic Dashboard subscription for MediaRemoved notifications.
/// Producers retain their exact typed payload shapes.
/// </summary>
public sealed record LibraryChangedEvent;
