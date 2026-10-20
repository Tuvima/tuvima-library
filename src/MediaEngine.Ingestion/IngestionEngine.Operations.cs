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

    /// <summary>
    /// Links the file's durable operation to its media asset the moment the asset is known (newly registered, or the
    /// existing asset a duplicate or same-path re-detection resolves to), so the live ingestion numbers can follow the
    /// file from intake into identity.
    /// </summary>
    private async Task AttachOperationAssetAsync(MediaOperation? operation, Guid assetId, CancellationToken ct)
    {
        if (_operationRepository is null || operation is null)
        {
            return;
        }

        try
        {
            await _operationRepository
                .AttachEntityAsync(operation.Id, assetId, EntityType.MediaAsset.ToString(), ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Durable operation asset link failed for {OperationId}", operation.Id);
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

    /// <summary>The reason shown when a file the Engine had noticed was gone before its intake could read it.</summary>
    private const string VanishedFileReason =
        "The file was detected but is no longer available. It may have been moved, deleted, or locked before ingestion could read it.";

    /// <summary>
    /// The debounce queue dropped a noticed file because it is no longer there. Nothing else will ever finish the job
    /// that was opened when the file was first noticed, so close it here.
    /// </summary>
    private void OnFileVanished(FileEvent evt)
    {
        if (LifetimeToken.IsCancellationRequested)
        {
            return;
        }

        TrackBackgroundTask(
            CloseVanishedFileOperationAsync(IngestionCandidate.FromEvent(evt), LifetimeToken),
            "vanished-file intake close-out");
    }

    /// <summary>
    /// A file that is gone before the pipeline could read it: closes its waiting intake job (see
    /// <see cref="CloseWaitingFileOperationAsync"/>) and, when there was one, settles the file in the ingestion log
    /// the same way the pipeline does for a file it finds missing.
    /// </summary>
    private async Task CloseVanishedFileOperationAsync(IngestionCandidate candidate, CancellationToken ct)
    {
        ReleaseActivePath(candidate.Path);

        if (!await CloseWaitingFileOperationAsync(candidate.Path, ct).ConfigureAwait(false))
        {
            return;
        }

        _logger.LogInformation(
            "Ingestion closed - file vanished before it could be read: \"{FileName}\"",
            Path.GetFileName(candidate.Path));
        await _ingestionLogScribe.RecordTerminalAsync(
            candidate,
            "missing",
            VanishedFileReason,
            ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Closes the file's open intake job as <c>missing_confirmed</c> with a plain reason, through the tracker (so a history
    /// entry is written and the change is published). Only a job still waiting for the pipeline (just noticed, or waiting
    /// for a lock retry) is closed; a job the pipeline is already running is left to it. A job is found by the file's path,
    /// because its key includes the file's size and time, which change once the file is gone.
    /// Returns whether a job was closed.
    /// </summary>
    private async Task<bool> CloseWaitingFileOperationAsync(string path, CancellationToken ct)
    {
        if (_operationTracker is null || _operationRepository is null)
        {
            return false;
        }

        try
        {
            var open = await _operationRepository
                .GetActiveBySourcePathAsync(Path.GetFullPath(path), ct)
                .ConfigureAwait(false);
            if (open is null || !IsWaitingForPipeline(open))
            {
                return false;
            }

            await _operationTracker.MarkMissingConfirmedAsync(open.Id, VanishedFileReason, null, ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Durable operation close-out failed for vanished file {Path}", path);
            return false;
        }
    }

    private static bool IsWaitingForPipeline(MediaOperation operation) =>
        string.Equals(operation.Stage, MediaOperationStage.Discovered, StringComparison.Ordinal)
        || string.Equals(operation.Status, MediaOperationStatus.FailedRetryable, StringComparison.Ordinal);

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
                await CloseVanishedFileOperationAsync(candidate, ct).ConfigureAwait(false);
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
        UnsubscribeFromFolderChanges();
        _watcher.FileDetected -= OnFileDetected;
        _watcher.WatcherError -= OnWatcherError;
        _debounce.FileVanished -= OnFileVanished;
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
            "failed",
            reason,
            ct).ConfigureAwait(false);

        await CloseFailedOperationAsync(candidate, reason, ct).ConfigureAwait(false);

        await SafeActivityLogAsync(new Domain.Entities.SystemActivityEntry
        {
            ActionType = Domain.Constants.SystemActionType.MediaFailed,
            EntityType = "MediaAsset",
            CollectionName = Path.GetFileName(candidate.Path),
            // The Activity log shows this line as it is, so it stays plain: the error itself goes to the technical log.
            Detail = "Something went wrong while reading this file. The technical log has the details.",
            ChangesJson = JsonSerializer.Serialize(new
            {
                source_path = candidate.Path,
                source_file = Path.GetFileName(candidate.Path),
                error_type = exception.GetType().Name,
                message = exception.Message,
            }),
        }, ct).ConfigureAwait(false);

        await SafePublishAsync(SignalREvents.IngestionFailed, new IngestionFailedEvent(
            candidate.Path,
            reason,
            DateTimeOffset.UtcNow), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Closes the file's durable operation as a terminal failure, so a file that failed is never left "running" for ever.
    /// </summary>
    private async Task CloseFailedOperationAsync(IngestionCandidate candidate, string reason, CancellationToken ct)
    {
        if (_operationRepository is null)
        {
            return;
        }

        try
        {
            var operation = await _operationRepository
                .GetByIdempotencyKeyAsync(BuildIngestionOperationKey(candidate.Path), ct)
                .ConfigureAwait(false);
            if (operation is { CompletedAt: null })
            {
                await _operationRepository.MarkFailedTerminalAsync(operation.Id, reason, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Durable operation failure close failed for {Path}", candidate.Path);
        }
    }

    private async Task RecordTerminalLogAsync(
        IngestionCandidate candidate,
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
        return SafePublishAsync(
            SignalREvents.IngestionItemProgress,
            new IngestionItemProgressEvent(
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

    private async Task<Guid> ResolveArtworkOwnerEntityIdAsync(Guid assetId, CancellationToken ct)
    {
        if (_writeBackStageDependencies.WorkRepository is null)
        {
            return assetId;
        }

        var lineage = await _writeBackStageDependencies.WorkRepository.GetLineageByAssetAsync(assetId, ct).ConfigureAwait(false);
        return lineage?.TargetForParentScope ?? assetId;
    }

    private async Task<(Guid OwnerEntityId, bool IsSharedByManyFiles)> ResolveEmbeddedCoverOwnerAsync(Guid assetId, CancellationToken ct)
    {
        if (_writeBackStageDependencies.WorkRepository is null)
        {
            return (assetId, false);
        }

        var lineage = await _writeBackStageDependencies.WorkRepository.GetLineageByAssetAsync(assetId, ct).ConfigureAwait(false);
        if (lineage is null)
        {
            return (assetId, false);
        }

        var owner = lineage.MediaType switch
        {
            MediaType.Books or MediaType.Audiobooks or MediaType.Comics => lineage.TargetForSelfScope,
            _ => lineage.TargetForParentScope,
        };

        // Album tracks and audiobook parts share one cover owner; the first local cover found
        // keeps the cover stable instead of flipping to whichever file was written last.
        return (owner, lineage.MediaType is MediaType.Music or MediaType.Audiobooks);
    }

    /// <summary>
    /// True when a shared owner (album or multi-part audiobook) already has a preferred, on-disk
    /// local cover that a sibling file must not replace. An embedded cover still replaces a folder image.
    /// </summary>
    private static bool KeepsFirstSharedLocalCover(EntityAsset? existing, string newSource, bool isSharedByManyFiles)
    {
        if (!isSharedByManyFiles
            || existing is null
            || !existing.IsPreferred
            || string.IsNullOrWhiteSpace(existing.LocalImagePath)
            || !File.Exists(existing.LocalImagePath))
        {
            return false;
        }

        var existingIsFolder = string.Equals(existing.SourceProvider, CoverSourceFolder, StringComparison.OrdinalIgnoreCase);
        var newIsEmbedded = string.Equals(newSource, CoverSourceEmbedded, StringComparison.OrdinalIgnoreCase);
        return !(existingIsFolder && newIsEmbedded);
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
