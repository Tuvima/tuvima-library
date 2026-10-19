using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Jobs;

namespace MediaEngine.Domain.Contracts;

public interface IMediaOperationRepository
{
    Task<MediaOperation> EnsureAsync(MediaOperation operation, CancellationToken ct = default);
    Task<MediaOperation?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<MediaOperation?> GetByIdempotencyKeyAsync(string idempotencyKey, CancellationToken ct = default);
    Task<MediaOperation?> GetActiveBySourcePathAsync(string sourcePath, CancellationToken ct = default);
    Task<MediaOperation?> GetLatestBySourcePathAsync(string sourcePath, CancellationToken ct = default);
    Task<IReadOnlyList<MediaOperation>> GetByEntityAsync(Guid entityId, CancellationToken ct = default);
    Task<IReadOnlyList<MediaOperation>> GetByBatchAsync(Guid batchId, CancellationToken ct = default);
    Task<IReadOnlyList<MediaOperation>> GetByPluginAsync(string pluginId, int limit, CancellationToken ct = default);
    Task<IReadOnlyList<MediaOperation>> GetQueueAsync(string? queueName, int limit, CancellationToken ct = default);
    Task<IReadOnlyList<MediaOperation>> LeaseNextAsync(string workerName, IReadOnlyList<string> operationTypes, int batchSize, TimeSpan leaseDuration, CancellationToken ct = default);
    Task UpdateStageAsync(Guid id, string stage, int? progressPercent = null, CancellationToken ct = default);

    /// <summary>Records which entity (for an ingestion file, its media asset) the operation is about, once that is known. The status and stage are not touched.</summary>
    Task AttachEntityAsync(Guid id, Guid entityId, string entityKind, CancellationToken ct = default);

    Task HeartbeatAsync(Guid id, int? progressPercent = null, CancellationToken ct = default);
    Task MarkSucceededAsync(Guid id, string? resultSummary = null, CancellationToken ct = default);
    Task MarkNoResultAsync(Guid id, string? missingReason = null, string? resultSummary = null, CancellationToken ct = default);
    Task MarkMissingConfirmedAsync(Guid id, string? missingReason = null, CancellationToken ct = default);
    Task MarkBlockedAsync(Guid id, string reason, CancellationToken ct = default);
    Task MarkFailedRetryableAsync(Guid id, string error, DateTimeOffset nextRetryAt, CancellationToken ct = default);
    Task MarkFailedRetryableForOutcomeAsync(
        Guid id,
        string error,
        DateTimeOffset nextRetryAt,
        BackgroundJobOutcomeCategory category,
        CancellationToken ct = default)
        => MarkFailedRetryableAsync(id, error, nextRetryAt, ct);
    Task MarkFailedTerminalAsync(Guid id, string error, CancellationToken ct = default);
    Task MarkFailedTerminalForOutcomeAsync(
        Guid id,
        string error,
        BackgroundJobOutcomeCategory category,
        CancellationToken ct = default)
        => MarkFailedTerminalAsync(id, error, ct);
    Task MarkDeadLetteredAsync(Guid id, string error, CancellationToken ct = default);
    Task MarkCancelledAsync(Guid id, string? reason = null, CancellationToken ct = default);
    Task MarkInterruptedAsync(Guid id, string? reason = null, CancellationToken ct = default);
    Task RequeueAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Records how far a long job has got (items done, items that failed, a percentage) and keeps its lease and heartbeat
    /// fresh. It never changes the status, so a pause or cancel a person asked for in the meantime is not overwritten.
    /// </summary>
    Task RecordProgressAsync(
        Guid id,
        int itemsTotal,
        int itemsCompleted,
        int itemsFailed,
        int progressPercent,
        TimeSpan? extendLease = null,
        CancellationToken ct = default);

    /// <summary>Pauses a job that has not finished. Returns <see langword="false"/> when it already finished or is already paused.</summary>
    Task<bool> PauseAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Releases a job that is still running because its worker is stopping, so it is picked up again on the next start. A
    /// job that was paused, cancelled or finished in the meantime is left exactly as it is. Returns whether it was released.
    /// </summary>
    Task<bool> InterruptIfRunningAsync(Guid id, string? reason = null, CancellationToken ct = default);

    /// <summary>Puts a paused job back in the queue. Returns <see langword="false"/> when it was not paused.</summary>
    Task<bool> ResumePausedAsync(Guid id, CancellationToken ct = default);
    Task<int> ReclaimStuckAsync(TimeSpan stuckThreshold, CancellationToken ct = default);

    /// <summary>File intake jobs that are waiting to start (pending or queued), with the file each one is for.</summary>
    Task<IReadOnlyList<(Guid Id, string SourcePath)>> ListWaitingIngestionFilesAsync(CancellationToken ct = default);
    Task<IReadOnlyDictionary<string, int>> GetSummaryAsync(CancellationToken ct = default);
}
