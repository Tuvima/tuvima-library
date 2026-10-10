using System.Text.Json;
using MediaEngine.Contracts.Realtime;
using MediaEngine.Domain;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Capabilities;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Models;
using MediaEngine.Domain.Services;
using MediaEngine.Ingestion.Contracts;
using MediaEngine.Ingestion.Detection;
using MediaEngine.Ingestion.Models;
using MediaEngine.Ingestion.Services;
using MediaEngine.Intelligence.Contracts;
using MediaEngine.Intelligence.Models;
using MediaEngine.Processors.Contracts;
using MediaEngine.Providers.Contracts;
using MediaEngine.Providers.Helpers;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MediaEngine.Ingestion;

public sealed partial class IngestionEngine
{
    private async Task<MediaOperation?> GetTrackedIngestionOperationAsync(string path, CancellationToken ct)
    {
        if (_operationRepository is null)
        {
            return null;
        }

        try
        {
            var keyMatch = await _operationRepository
                .GetByIdempotencyKeyAsync(BuildIngestionOperationKey(path), ct)
                .ConfigureAwait(false);

            if (keyMatch is not null && !IsTerminalMediaOperation(keyMatch))
            {
                return keyMatch;
            }

            var activePathMatch = await _operationRepository
                .GetActiveBySourcePathAsync(Path.GetFullPath(path), ct)
                .ConfigureAwait(false);

            if (activePathMatch is not null)
            {
                return activePathMatch;
            }

            var latestPathMatch = await _operationRepository
                .GetLatestBySourcePathAsync(Path.GetFullPath(path), ct)
                .ConfigureAwait(false);

            return latestPathMatch ?? keyMatch;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Durable ingestion operation lookup failed for {Path}", path);
            return null;
        }
    }

    private async Task RequeueTrackedIngestionOperationAsync(MediaOperation operation, CancellationToken ct)
    {
        if (_operationRepository is null || IsTerminalMediaOperation(operation))
        {
            return;
        }

        try
        {
            await _operationRepository.RequeueAsync(operation.Id, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Durable ingestion operation requeue failed for {OperationId}", operation.Id);
        }
    }

    private static bool IsTerminalMediaOperation(MediaOperation operation) =>
        operation.Status is MediaOperationStatus.Succeeded
            or MediaOperationStatus.NoResult
            or MediaOperationStatus.MissingConfirmed
            or MediaOperationStatus.NotApplicable
            or MediaOperationStatus.Blocked
            or MediaOperationStatus.FailedTerminal
            or MediaOperationStatus.DeadLettered
            or MediaOperationStatus.Cancelled
            or MediaOperationStatus.Skipped;

    private async Task<MediaOperation?> EnsureIngestionOperationAsync(
        FileEvent evt,
        string stage,
        CancellationToken ct)
    {
        if (_operationTracker is null)
        {
            return null;
        }

        try
        {
            return await _operationTracker.EnsureQueuedAsync(new MediaOperation
            {
                OperationType = MediaOperationType.IngestionFile,
                OperationKind = MediaOperationKind.Ingestion,
                BatchId = evt.BatchId,
                SourcePath = Path.GetFullPath(evt.Path),
                Status = stage == MediaOperationStage.Discovered ? MediaOperationStatus.Pending : MediaOperationStatus.Queued,
                Stage = stage,
                QueueName = "ingestion",
                IdempotencyKey = BuildIngestionOperationKey(evt.Path)
            }, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Durable ingestion operation ensure failed for {Path}", evt.Path);
            return null;
        }
    }

    private async Task<MediaOperation?> EnsureIngestionOperationAsync(
        IngestionCandidate candidate,
        Guid ingestionRunId,
        string stage,
        CancellationToken ct)
    {
        if (_operationTracker is null)
        {
            return null;
        }

        try
        {
            return await _operationTracker.EnsureQueuedAsync(new MediaOperation
            {
                OperationType = MediaOperationType.IngestionFile,
                OperationKind = MediaOperationKind.Ingestion,
                BatchId = candidate.BatchId ?? ingestionRunId,
                SourcePath = Path.GetFullPath(candidate.Path),
                Status = MediaOperationStatus.Queued,
                Stage = stage,
                QueueName = "ingestion",
                IdempotencyKey = BuildIngestionOperationKey(candidate.Path)
            }, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Durable ingestion operation ensure failed for {Path}", candidate.Path);
            return null;
        }
    }

    private async Task UpdateOperationStageAsync(
        MediaOperation? operation,
        string stage,
        int progressPercent,
        string message,
        CancellationToken ct,
        object? detail = null)
    {
        if (_operationTracker is null || operation is null)
        {
            return;
        }

        try
        {
            await _operationTracker.UpdateStageAsync(operation.Id, stage, progressPercent, message, detail, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Durable operation stage update failed for {OperationId}", operation.Id);
        }
    }

    private async Task CompleteOperationAsync(MediaOperation? operation, string? summary, CancellationToken ct)
    {
        if (_operationTracker is null || operation is null)
        {
            return;
        }

        try
        {
            await _operationTracker.MarkSucceededAsync(operation.Id, summary, null, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Durable operation completion failed for {OperationId}", operation.Id);
        }
    }

    private async Task NoResultOperationAsync(MediaOperation? operation, string reason, CancellationToken ct)
    {
        if (_operationTracker is null || operation is null)
        {
            return;
        }

        try
        {
            await _operationTracker.MarkNoResultAsync(operation.Id, reason, null, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Durable operation no-result update failed for {OperationId}", operation.Id);
        }
    }

    private async Task MarkBlockedOperationAsync(
        MediaOperation? operation,
        string reason,
        CancellationToken ct)
    {
        if (_operationTracker is null || operation is null)
        {
            return;
        }

        try
        {
            await _operationTracker.MarkBlockedAsync(operation.Id, reason, null, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Durable operation blocked update failed for {OperationId}", operation.Id);
        }
    }

    private async Task MarkRetryableOperationAsync(
        MediaOperation? operation,
        string reason,
        DateTimeOffset nextRetryAt,
        CancellationToken ct)
    {
        if (_operationRepository is null || operation is null)
        {
            return;
        }

        try
        {
            await _operationRepository.MarkFailedRetryableAsync(operation.Id, reason, nextRetryAt, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Durable operation retry update failed for {OperationId}", operation.Id);
        }
    }

    private async Task MarkInterruptedOperationAsync(
        MediaOperation? operation,
        string reason,
        CancellationToken ct)
    {
        if (_operationRepository is null || operation is null)
        {
            return;
        }

        try
        {
            await _operationRepository.MarkInterruptedAsync(operation.Id, reason, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Durable operation interrupted update failed for {OperationId}", operation.Id);
        }
    }

    private TimeSpan ComputeLockProbeRetryDelay(int attempt)
    {
        var baseSeconds = Math.Max(1, _options.LockProbeRetryBaseDelaySeconds);
        var multiplier = Math.Pow(2, Math.Max(0, attempt));
        var seconds = Math.Min(300, baseSeconds * multiplier);
        return TimeSpan.FromSeconds(seconds);
    }

    private void ScheduleLockProbeRetry(IngestionCandidate candidate, DateTimeOffset nextRetryAt)
    {
        var delay = nextRetryAt - DateTimeOffset.UtcNow;
        if (delay < TimeSpan.Zero)
        {
            delay = TimeSpan.Zero;
        }

        TrackBackgroundTask(RunLockProbeRetryAsync(candidate, delay, LifetimeToken), "delayed lock-probe retry");
    }

    private async Task RunLockProbeRetryAsync(
        IngestionCandidate candidate,
        TimeSpan delay,
        CancellationToken ct)
    {
        try
        {
            await Task.Delay(delay, ct).ConfigureAwait(false);
            if (!File.Exists(candidate.Path))
            {
                return;
            }

            _debounce.Enqueue(new FileEvent
            {
                Path = candidate.Path,
                OldPath = candidate.OldPath,
                EventType = candidate.EventType == FileEventType.Deleted
                    ? FileEventType.Created
                    : candidate.EventType,
                OccurredAt = DateTimeOffset.UtcNow,
                BatchId = candidate.BatchId,
                Intake = candidate.Intake,
            });
        }
        catch (ObjectDisposedException)
        {
            // The engine is stopping.
        }
        catch (InvalidOperationException)
        {
            // The debounce queue is stopping.
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The engine is stopping.
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Delayed lock-probe retry failed for {Path}", candidate.Path);
        }
    }

    private void TrackBackgroundTask(Task task, string operation)
    {
        lock (_ownedTasksLock)
        {
            _ownedTasks[task] = operation;
        }

        task.GetAwaiter().OnCompleted(() => CompleteBackgroundTask(task));
    }

    private void CompleteBackgroundTask(Task task)
    {
        string operation;
        lock (_ownedTasksLock)
        {
            operation = _ownedTasks.Remove(task, out var trackedOperation)
                ? trackedOperation
                : "background operation";
        }

        if (task.IsFaulted)
        {
            _logger.LogError(
                task.Exception?.GetBaseException(),
                "IngestionEngine owned {Operation} failed.",
                operation);
        }
    }

    private async Task DrainOwnedTasksAsync(CancellationToken ct)
    {
        Task[] tasks;
        lock (_ownedTasksLock)
        {
            tasks = _ownedTasks.Keys.ToArray();
        }

        if (tasks.Length == 0)
        {
            return;
        }

        try
        {
            await Task.WhenAll(tasks).WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (LifetimeToken.IsCancellationRequested)
        {
            // Expected: stopping cancels polling, recovery, and delayed retries.
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "One or more owned ingestion background operations failed during shutdown.");
        }
        finally
        {
            lock (_ownedTasksLock)
            {
                foreach (var completed in tasks.Where(task => task.IsCompleted))
                {
                    _ownedTasks.Remove(completed);
                }
            }
        }
    }

    public override void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
        {
            return;
        }

        _shutdownCts.Cancel();
        _watcher.FileDetected -= OnFileDetected;
        _watcher.WatcherError -= OnWatcherError;
        lock (_fswBufferLock)
        {
            _fswFlushTimer?.Dispose();
            _fswFlushTimer = null;
        }

        base.Dispose();
        _executeCts?.Dispose();
        _executeCts = null;
        _shutdownCts.Dispose();
    }

    private static string BuildIngestionOperationKey(string path)
    {
        var normalized = Path.GetFullPath(path).Replace('\\', '/').Trim().ToLowerInvariant();
        try
        {
            var info = new FileInfo(path);
            var length = info.Exists ? info.Length : 0;
            var lastWrite = info.Exists ? info.LastWriteTimeUtc.Ticks : 0;
            return $"ingestion:file:{normalized}:{length}:{lastWrite}";
        }
        catch
        {
            return $"ingestion:file:{normalized}:0:0";
        }
    }
    /// <summary>
    /// Records a terminal failure when an unexpected exception escapes the per-file pipeline.
    /// </summary>
    private async Task RecordUnhandledCandidateFailureAsync(
        IngestionCandidate candidate,
        Exception exception,
        CancellationToken ct)
    {
        var reason = $"Ingestion failed while processing {Path.GetFileName(candidate.Path)}: {exception.Message}";
        _logger.LogError(exception, "Ingestion failed for {Path}", candidate.Path);

        await _ingestionLogScribe.RecordTerminalAsync(
            candidate,
            candidate.BatchId,
            "failed",
            reason,
            ct).ConfigureAwait(false);

        await SafeActivityLogAsync(new Domain.Entities.SystemActivityEntry
        {
            ActionType = Domain.Constants.SystemActionType.MediaFailed,
            EntityType = "MediaAsset",
            Detail = reason,
            ChangesJson = JsonSerializer.Serialize(new
            {
                source_path = candidate.Path,
                source_file = Path.GetFileName(candidate.Path),
                error_type = exception.GetType().Name,
                message = exception.Message,
            }),
            IngestionRunId = candidate.BatchId,
        }, ct).ConfigureAwait(false);

        await SafePublishAsync(SignalREvents.IngestionFailed, new IngestionFailedEvent(
            candidate.Path,
            reason,
            DateTimeOffset.UtcNow), ct).ConfigureAwait(false);

        if (candidate.BatchId.HasValue)
        {
            await SafeIncrementBatchCounterAsync(candidate.BatchId.Value, BatchCounterColumn.FilesFailed, ct).ConfigureAwait(false);
            await SafeIncrementBatchCounterAsync(candidate.BatchId.Value, BatchCounterColumn.FilesProcessed, ct).ConfigureAwait(false);
            await PublishQueuedBatchSnapshotAsync(candidate.BatchId.Value, ct).ConfigureAwait(false);
        }
    }

    private async Task RecordTerminalLogAsync(
        IngestionCandidate candidate,
        Guid? ingestionRunId,
        string status,
        string detail,
        CancellationToken ct)
    {
        try
        {
            var logEntryId = Guid.NewGuid();
            await _ingestionLog.InsertAsync(new Domain.Entities.IngestionLogEntry
            {
                Id = logEntryId,
                FilePath = candidate.Path,
                Status = status,
                ErrorDetail = detail,
                IngestionRunId = ingestionRunId,
            }, ct).ConfigureAwait(false);

            await PublishItemProgressAsync(
                candidate,
                logEntryId,
                status,
                100,
                true,
                ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Terminal ingestion log write failed for {Path}", candidate.Path);
        }
    }

    private Task PublishItemProgressAsync(
        IngestionCandidate candidate,
        Guid logEntryId,
        string stage,
        int progressPercent,
        bool isTerminal,
        CancellationToken ct,
        Guid? mediaAssetId = null,
        string? title = null,
        string? mediaType = null)
    {
        if (!candidate.BatchId.HasValue)
        {
            return Task.CompletedTask;
        }

        return SafePublishAsync(
            SignalREvents.IngestionItemProgress,
            new IngestionItemProgressEvent(
                candidate.BatchId.Value,
                logEntryId,
                mediaAssetId,
                candidate.Path,
                Path.GetFileName(candidate.Path),
                stage,
                ResolveItemStageOrder(stage),
                Math.Clamp(progressPercent, 0, 100),
                isTerminal,
                title,
                mediaType),
            ct);
    }

    private static int ResolveItemStageOrder(string stage) => stage switch
    {
        "detected" => 0,
        "hashing" => 1,
        "processed" => 2,
        "scored" => 3,
        "registered" => 4,
        "queued_identity" => 5,
        "hydrating" => 6,
        "complete" => 7,
        "needs_review" => 7,
        "duplicate" => 7,
        "same_path_redetected" => 7,
        "missing" => 7,
        "skipped_non_media" => 7,
        "failed" => 7,
        _ => 0,
    };
    private async Task SafeActivityLogAsync(
        Domain.Entities.SystemActivityEntry entry,
        CancellationToken ct)
    {
        try
        {
            await _activityRepo.LogAsync(entry, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Activity log write failed for action '{Action}' — pipeline continues",
                entry.ActionType);
        }
    }

    /// <summary>
    /// Atomically increments one counter column on the given batch record.
    /// Best-effort — never throws; a counter miss must not abort the pipeline.
    /// </summary>
    private async Task SafeIncrementBatchCounterAsync(
        Guid batchId,
        BatchCounterColumn column,
        CancellationToken ct)
    {
        try
        {
            await _batchRepo.IncrementCounterAsync(batchId, column, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Batch counter increment failed for batch {BatchId} column {Column} — pipeline continues",
                batchId, column);
        }
    }

    private async Task MarkBatchFileProcessedAsync(Guid? batchId, CancellationToken ct)
    {
        if (!batchId.HasValue)
        {
            return;
        }

        await SafeIncrementBatchCounterAsync(batchId.Value, BatchCounterColumn.FilesProcessed, ct)
            .ConfigureAwait(false);
        await PublishQueuedBatchSnapshotAsync(batchId.Value, ct).ConfigureAwait(false);
    }
    private Task PublishInitialBatchProgressAsync(Guid batchId, int totalFiles)
        => SafePublishAsync(
            SignalREvents.BatchProgress,
            new BatchProgressEvent(
                batchId,
                totalFiles,
                0,
                0,
                0,
                0,
                0,
                0,
                null,
                false,
                CurrentStage: "Queued",
                FilesQueued: totalFiles,
                FilesActive: 0),
            CancellationToken.None);

    private async Task PublishQueuedBatchSnapshotAsync(Guid batchId, CancellationToken ct)
    {
        try
        {
            var batch = await _batchRepo.GetByIdAsync(batchId, ct).ConfigureAwait(false);
            if (batch is null)
            {
                return;
            }

            var terminal = Math.Max(
                batch.FilesProcessed,
                batch.FilesIdentified + batch.FilesReview + batch.FilesNoMatch + batch.FilesFailed);
            var progressed = batch.FilesTotal > 0
                ? Math.Clamp(terminal, 0, batch.FilesTotal)
                : terminal;
            var queue = Math.Max(0, batch.FilesTotal - progressed);
            var completed = batch.FilesTotal > 0 && queue == 0;

            await SafePublishAsync(
                SignalREvents.BatchProgress,
                new BatchProgressEvent(
                    batch.Id,
                    batch.FilesTotal,
                    progressed,
                    0,
                    0,
                    0,
                    batch.FilesFailed,
                    batch.FilesTotal > 0 ? (int)Math.Round(progressed * 100.0 / batch.FilesTotal) : 0,
                    null,
                    completed,
                    CurrentStage: completed ? "Complete" : "Queued",
                    FilesQueued: queue,
                    FilesActive: 0),
                ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Initial batch snapshot publish failed for {BatchId}", batchId);
        }
    }

    private async Task<Guid> ResolveArtworkOwnerEntityIdAsync(Guid assetId, CancellationToken ct)
    {
        if (_writeBackStageDependencies.WorkRepository is null)
        {
            return assetId;
        }

        var lineage = await _writeBackStageDependencies.WorkRepository.GetLineageByAssetAsync(assetId, ct).ConfigureAwait(false);
        return lineage?.TargetForParentScope ?? assetId;
    }

    private async Task<Guid> ResolveEmbeddedCoverOwnerEntityIdAsync(Guid assetId, CancellationToken ct)
    {
        if (_writeBackStageDependencies.WorkRepository is null)
        {
            return assetId;
        }

        var lineage = await _writeBackStageDependencies.WorkRepository.GetLineageByAssetAsync(assetId, ct).ConfigureAwait(false);
        if (lineage is null)
        {
            return assetId;
        }

        return lineage.MediaType switch
        {
            MediaType.Books or MediaType.Audiobooks or MediaType.Comics => lineage.TargetForSelfScope,
            _ => lineage.TargetForParentScope,
        };
    }

    /// <summary>
    /// True when the owner already has a preferred, on-disk local cover that makes rewriting unnecessary:
    /// either the same bytes from the same kind of source, or an embedded cover when only a folder image is on offer
    /// (embedded art always wins over a folder image).
    /// </summary>
    private static async Task<bool> IsLocalCoverAlreadyPersistedAsync(
        EntityAsset? existing,
        string newSource,
        byte[] newBytes,
        CancellationToken ct)
    {
        if (existing is null || !existing.IsPreferred || string.IsNullOrWhiteSpace(existing.LocalImagePath))
        {
            return false;
        }

        var existingFile = new FileInfo(existing.LocalImagePath);
        if (!existingFile.Exists)
        {
            return false;
        }

        var existingIsFolder = string.Equals(existing.SourceProvider, CoverSourceFolder, StringComparison.OrdinalIgnoreCase);
        var newIsFolder = string.Equals(newSource, CoverSourceFolder, StringComparison.OrdinalIgnoreCase);
        if (newIsFolder && !existingIsFolder)
        {
            return true;
        }

        if (existingIsFolder != newIsFolder || existingFile.Length != newBytes.Length)
        {
            return false;
        }

        var existingBytes = await File.ReadAllBytesAsync(existing.LocalImagePath, ct).ConfigureAwait(false);
        return existingBytes.AsSpan().SequenceEqual(newBytes);
    }

    private static string InferArtworkExtension(string? contentType) =>
        string.Equals(contentType, "image/png", StringComparison.OrdinalIgnoreCase)
            ? ".png"
            : ".jpg";
}
