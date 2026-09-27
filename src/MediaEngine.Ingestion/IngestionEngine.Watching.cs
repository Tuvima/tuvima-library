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
    private async Task ScanExistingFilesAsync(IReadOnlyList<IngestionScanTarget> targets, CancellationToken ct = default)
    {
        var scanTargets = targets
            .Where(target => !string.IsNullOrWhiteSpace(target.Path))
            .Select(target => new IngestionScanTarget(NormalizeDirectoryPath(target.Path), target.IncludeSubdirectories))
            .Where(target => Directory.Exists(target.Path))
            .GroupBy(target => target.Path, StringComparer.OrdinalIgnoreCase)
            .Select(group => new IngestionScanTarget(
                group.Key,
                group.Any(target => target.IncludeSubdirectories)))
            .ToList();

        if (scanTargets.Count == 0)
        {
            return;
        }

        // Fetch all known file paths from the database in a single query.
        // Files whose path is already tracked are skipped without being
        // enqueued, preventing a spurious batch from being created on restart
        // for files that were processed in a previous session.
        // Note: files that moved after processing (e.g. watch folder -> staging)
        // will not be caught by this path check, but the hash-based duplicate
        // check inside ProcessCandidateAsync (step 5) serves as the safety net.
        // Normalize all stored paths to full, canonical forms so the comparison
        // below is robust against relative paths or casing differences (Windows).
        HashSet<string> knownPaths;
        try
        {
            var rawPaths = await _assetRepo.GetAllFilePathsAsync(ct).ConfigureAwait(false);
            knownPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in rawPaths)
            {
                try
                {
                    knownPaths.Add(Path.GetFullPath(p));
                }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or System.Security.SecurityException)
                {
                    // Best effort: a malformed stored path should not block startup scanning.
                    _logger.LogDebug(ex, "Skipping malformed stored file path during initial scan");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not load known file paths from database - skipping pre-filter");
            knownPaths = [];
        }

        var newEvents = new List<FileEvent>();
        var resumedEvents = new List<FileEvent>();
        var acceptedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var skipped = 0;
        var resumed = 0;

        foreach (var target in scanTargets)
        {
            var searchOption = target.IncludeSubdirectories
                ? SearchOption.AllDirectories
                : SearchOption.TopDirectoryOnly;

            try
            {
                foreach (var filePath in Directory.EnumerateFiles(target.Path, "*.*", searchOption))
                {
                    ct.ThrowIfCancellationRequested();

                    // Skip files inside the .data directory - staging, images, and database
                    // live here and must not be re-ingested automatically.
                    if (IsIgnoredScanFile(filePath))
                    {
                        continue;
                    }

                    // Seed the polling fingerprint cache during the initial scan so the
                    // fallback sweep does not immediately reconsider settled files.
                    var normalizedPath = Path.GetFullPath(filePath);
                    TrackPollFingerprint(normalizedPath, GetPollFingerprint(filePath));

                    if (!acceptedPaths.Add(normalizedPath))
                    {
                        skipped++;
                        continue;
                    }

                    // Skip a tracked path only when its persistent size/mtime fingerprint
                    // still maps to a cached content hash. A changed file may have been
                    // edited while the Engine was stopped and must reach the same-path
                    // refresh path so embedded/local metadata is read again.
                    var trackedOperation = await GetTrackedIngestionOperationAsync(normalizedPath, ct).ConfigureAwait(false);
                    if (knownPaths.Contains(normalizedPath)
                        && (trackedOperation is null || IsTerminalMediaOperation(trackedOperation)))
                    {
                        var fingerprintIsCurrent = false;
                        if (_hashStageDependencies.FileHashCache is not null)
                        {
                            try
                            {
                                var info = new FileInfo(normalizedPath);
                                var cachedHash = await _hashStageDependencies.FileHashCache.TryGetAsync(
                                    normalizedPath,
                                    info.Length,
                                    new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero),
                                    ct).ConfigureAwait(false);
                                fingerprintIsCurrent = !string.IsNullOrWhiteSpace(cachedHash);
                            }
                            catch (Exception ex) when (ex is not OperationCanceledException)
                            {
                                _logger.LogDebug(ex,
                                    "Could not verify the persistent fingerprint for tracked path {Path}; queuing a safe recheck",
                                    normalizedPath);
                            }
                        }

                        if (fingerprintIsCurrent)
                        {
                            skipped++;
                            continue;
                        }
                    }

                    if (trackedOperation is not null)
                    {
                        if (IsTerminalMediaOperation(trackedOperation))
                        {
                            skipped++;
                            continue;
                        }

                        var trackedEvent = new FileEvent
                        {
                            Path = normalizedPath,
                            EventType = FileEventType.Created,
                            OccurredAt = DateTimeOffset.UtcNow,
                            BatchId = trackedOperation.BatchId,
                        };

                        if (trackedOperation.BatchId.HasValue)
                        {
                            await RequeueTrackedIngestionOperationAsync(trackedOperation, ct).ConfigureAwait(false);
                            resumedEvents.Add(trackedEvent);
                            resumed++;
                            continue;
                        }

                        newEvents.Add(trackedEvent);
                        resumed++;
                        continue;
                    }

                    newEvents.Add(new FileEvent
                    {
                        Path = normalizedPath,
                        EventType = FileEventType.Created,
                        OccurredAt = DateTimeOffset.UtcNow,
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Initial scan of watch directory failed after {Count} accepted file(s): {Dir}",
                    newEvents.Count + resumedEvents.Count, target.Path);
            }
        }

        if (skipped > 0)
        {
            _logger.LogInformation(
                "Initial scan: skipped {Skipped} already-known or duplicate file(s) across {TargetCount} scan target(s)",
                skipped, scanTargets.Count);
        }

        if (newEvents.Count == 0 && resumedEvents.Count == 0)
        {
            _concurrencyGuard.Cleanup();
            return;
        }

        if (newEvents.Count > 0)
        {
            var batchId = Guid.NewGuid();
            foreach (var evt in newEvents)
            {
                evt.BatchId = batchId;
            }

            try
            {
                await _batchRepo.CreateAsync(new IngestionBatch
                {
                    Id = batchId,
                    Status = "running",
                    SourcePath = ResolveScanBatchSourcePath(scanTargets),
                    FilesTotal = newEvents.Count,
                    StartedAt = DateTimeOffset.UtcNow,
                }, ct).ConfigureAwait(false);
                await PublishInitialBatchProgressAsync(batchId, newEvents.Count).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Batch record creation failed for scan batchId {BatchId} - pipeline continues", batchId);
            }
        }

        _logger.LogInformation(
            "Initial scan: enqueued {NewCount} new file(s) and resumed {ResumedCount} tracked file(s) across {TargetCount} scan target(s)",
            newEvents.Count,
            resumed,
            scanTargets.Count);

        foreach (var resumedBatchId in resumedEvents
            .Select(evt => evt.BatchId)
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .Distinct())
        {
            await PublishQueuedBatchSnapshotAsync(resumedBatchId, ct).ConfigureAwait(false);
        }

        foreach (var evt in newEvents.Concat(resumedEvents))
        {
            await EnsureIngestionOperationAsync(evt, MediaOperationStage.Discovered, ct).ConfigureAwait(false);
            _debounce.Enqueue(evt);
        }
        _concurrencyGuard.Cleanup();
    }

    private static bool IsIgnoredScanFile(string filePath)
    {
        if (filePath.Contains(Path.DirectorySeparatorChar + ".data" + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase)
            || filePath.Contains('/' + ".data" + '/', StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return IsTransientInputPath(filePath)
            || NonMediaExtensions.Contains(Path.GetExtension(filePath));
    }

    private static bool IsTransientInputPath(string filePath)
    {
        var name = Path.GetFileName(filePath);
        return name.StartsWith(".fuse_hidden", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith(".nfs", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("~$", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".part", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".partial", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".crdownload", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeDirectoryPath(string path)
        => Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static string ResolveScanBatchSourcePath(IReadOnlyList<IngestionScanTarget> targets)
        => targets.Count == 1
            ? targets[0].Path
            : "Multiple source folders";

    // =========================================================================
    // Polling fallback — safety net when FSW misses events
    // =========================================================================

    /// <summary>
    /// Periodically sweeps the Watch Folder for files that the
    /// <see cref="System.IO.FileSystemWatcher"/> may have missed.
    /// Synthesises <c>Created</c> events into the debounce queue;
    /// the normal hash-based duplicate check prevents double-processing.
    /// </summary>
    private async Task PollWatchDirectoryAsync(CancellationToken ct)
    {
        if (_options.PollIntervalSeconds <= 0)
        {
            return;
        }

        var interval = TimeSpan.FromSeconds(_options.PollIntervalSeconds);
        _logger.LogInformation(
            "Polling fallback active: sweeping {Count} watch folder(s) every {Seconds}s",
            GetConfiguredScanTargets().Count,
            _options.PollIntervalSeconds);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }

            var scanTargets = GetConfiguredScanTargets()
                .Where(target => Directory.Exists(target.Path))
                .ToList();

            if (scanTargets.Count == 0)
            {
                continue;
            }

            var seenPollPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var rawKnownPaths = await _assetRepo.GetAllFilePathsAsync(ct).ConfigureAwait(false);
            var knownPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var trackedPath in rawKnownPaths)
            {
                try
                {
                    knownPaths.Add(Path.GetFullPath(trackedPath));
                }
                catch
                {
                    // Ignore malformed stored paths and continue the sweep.
                }
            }

            foreach (var target in scanTargets)
            {
                try
                {
                    var watchDirectory = target.Path;
                    var searchOption = target.IncludeSubdirectories
                        ? SearchOption.AllDirectories
                        : SearchOption.TopDirectoryOnly;
                    int inspected = 0;
                    int changed = 0;
                    int queued = 0;
                    int unchanged = 0;
                    int ignored = 0;
                    int missing = 0;

                    foreach (var filePath in Directory.EnumerateFiles(watchDirectory, "*.*", searchOption))
                    {
                        if (ct.IsCancellationRequested)
                        {
                            break;
                        }

                        if (IsIgnoredScanFile(filePath))
                        {
                            ignored++;
                            continue;
                        }

                        inspected++;

                        var normalizedPath = Path.GetFullPath(filePath);
                        seenPollPaths.Add(normalizedPath);
                        var fingerprint = GetPollFingerprint(filePath);
                        var trackedInDb = knownPaths.Contains(normalizedPath);
                        var hasPreviousFingerprint = TryGetPollFingerprint(normalizedPath, out var previousFingerprint);
                        var fingerprintChanged = hasPreviousFingerprint && previousFingerprint != fingerprint;

                        if (fingerprintChanged)
                        {
                            changed++;
                        }

                        if (!trackedInDb)
                        {
                            missing++;
                        }

                        if (hasPreviousFingerprint && !fingerprintChanged && trackedInDb)
                        {
                            unchanged++;
                            continue;
                        }

                        var pollEvt = new FileEvent
                        {
                            Path = normalizedPath,
                            EventType = FileEventType.Created,
                            OccurredAt = DateTimeOffset.UtcNow,
                        };

                        if (BufferFswEvent(pollEvt))
                        {
                            TrackPollFingerprint(normalizedPath, fingerprint);
                            queued++;
                        }
                        else
                        {
                            ignored++;
                        }
                    }

                    _logger.LogInformation(
                        "Poll sweep: inspected {Inspected}, changed {Changed}, queued {Queued}, unchanged {Unchanged}, ignored {Ignored}, missing {Missing} in {Dir}",
                        inspected,
                        changed,
                        queued,
                        unchanged,
                        ignored,
                        missing,
                        watchDirectory);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Poll sweep failed for {Dir}", target.Path);
                }
            }

            PrunePollFingerprints(seenPollPaths);
            _concurrencyGuard.Cleanup();
        }
    }

    // =========================================================================
    // Re-organize already-ingested files still sitting in the Watch Folder
    // =========================================================================

    /// <summary>
    /// When a file's content hash is already in the database but the file is
    /// still sitting in the Watch Folder (not auto-organized on first ingest),
    /// attempt to organize it using the canonical values that may have been
    /// enriched by external providers since the initial scan.
    /// </summary>
    private void OnFileDetected(object? sender, FileEvent evt)
    {
        if (!LifetimeToken.IsCancellationRequested)
        {
            BufferFswEvent(evt);
        }
    }

    private void OnWatcherError(object? sender, FileWatcherErrorEvent evt)
    {
        if (!LifetimeToken.IsCancellationRequested)
        {
            TrackBackgroundTask(
                RecoverWatcherAfterErrorAsync(evt, LifetimeToken),
                $"watcher recovery ({evt.Kind})");
        }
    }

    private async Task RecoverWatcherAfterErrorAsync(FileWatcherErrorEvent evt, CancellationToken ct)
    {
        if (!await _watcherRecoveryLock.WaitAsync(0, ct).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            _logger.LogWarning(
                "File watcher reported {Kind}: {Message}. Restarting watcher and running a targeted rescan.",
                evt.Kind,
                evt.Message);

            var scanTargets = _watcher.WatchedPaths
                .Where(Directory.Exists)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(path => GetConfiguredScanTargets().FirstOrDefault(target =>
                    string.Equals(target.Path, path, StringComparison.OrdinalIgnoreCase))
                    ?? new IngestionScanTarget(path, IncludeSubdirectories: false))
                .ToList();

            try
            {
                _watcher.Stop();
                _watcher.Start();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "File watcher restart failed after {Kind}", evt.Kind);
            }

            if (scanTargets.Count > 0)
            {
                await ScanExistingFilesAsync(scanTargets, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "File watcher recovery failed after {Kind}", evt.Kind);
        }
        finally
        {
            _watcherRecoveryLock.Release();
        }
    }

    /// <summary>
    /// Adds an FSW/poll-sourced file event to the collection buffer.
    /// Resets the quiet-period timer. Events that already carry a BatchId
    /// (e.g. from <see cref="ScanExistingFilesAsync"/>) are passed directly to
    /// the debounce queue.
    /// </summary>
    private bool BufferFswEvent(FileEvent evt)
    {
        var extension = Path.GetExtension(evt.Path);
        if (string.IsNullOrWhiteSpace(extension) || IsIgnoredScanFile(evt.Path))
        {
            return false;
        }

        var normalizedPath = Path.GetFullPath(evt.Path);
        if (Directory.Exists(normalizedPath))
        {
            return false;
        }

        if (evt.EventType != FileEventType.Deleted
            && !File.Exists(normalizedPath))
        {
            return false;
        }

        var normalizedEvent = new FileEvent
        {
            Path = normalizedPath,
            OldPath = evt.OldPath,
            EventType = evt.EventType,
            OccurredAt = evt.OccurredAt,
            BatchId = evt.BatchId,
            Intake = evt.Intake,
        };

        if (!TryTrackQueuedPath(normalizedPath))
        {
            return false;
        }

        // Events from ScanExistingFiles already have a batch - pass through.
        if (normalizedEvent.BatchId is not null)
        {
            TrackBackgroundTask(
                EnsureIngestionOperationAsync(normalizedEvent, MediaOperationStage.Discovered, LifetimeToken),
                "durable ingestion-operation discovery");
            _debounce.Enqueue(normalizedEvent);
            return true;
        }

        lock (_fswBufferLock)
        {
            _fswBuffer.Add(normalizedEvent);

            // Reset (or start) the quiet-period timer.
            _fswFlushTimer?.Dispose();
            _fswFlushTimer = new Timer(
                static state => ((IngestionEngine)state!).QueueFswBufferFlush(),
                this,
                _options.FswQuietPeriod,
                Timeout.InfiniteTimeSpan);

            return true;
        }
    }

    private bool TryTrackQueuedPath(string path)
    {
        var normalizedPath = Path.GetFullPath(path);
        var fingerprint = GetPollFingerprint(normalizedPath);

        lock (_fswBufferLock)
        {
            if (_activePaths.Contains(normalizedPath))
            {
                return false;
            }

            if (_queuedFingerprints.TryGetValue(normalizedPath, out var priorFingerprint)
                && priorFingerprint == fingerprint)
            {
                return false;
            }

            _activePaths.Add(normalizedPath);
            _queuedFingerprints[normalizedPath] = fingerprint;
            return true;
        }
    }

    private void ReleaseActivePath(string path)
    {
        try
        {
            var normalizedPath = Path.GetFullPath(path);
            lock (_fswBufferLock)
            {
                _activePaths.Remove(normalizedPath);
            }
        }
        catch
        {
            // Bad paths are already handled by the ingestion pipeline.
        }
    }

    private static PollFingerprint GetPollFingerprint(string filePath)
    {
        var info = new FileInfo(filePath);
        return new PollFingerprint(
            info.Exists ? info.Length : 0,
            info.Exists ? info.LastWriteTimeUtc : DateTime.MinValue);
    }

    private bool TryGetPollFingerprint(string path, out PollFingerprint fingerprint)
    {
        lock (_fswBufferLock)
        {
            return _pollFingerprints.TryGetValue(path, out fingerprint);
        }
    }

    private void TrackPollFingerprint(string path, PollFingerprint fingerprint)
    {
        lock (_fswBufferLock)
        {
            _pollFingerprints[path] = fingerprint;
        }
    }

    private void PrunePollFingerprints(IReadOnlySet<string> existingPaths)
    {
        lock (_fswBufferLock)
        {
            foreach (var path in _pollFingerprints.Keys.ToList())
            {
                if (!existingPaths.Contains(path) && !File.Exists(path))
                {
                    _pollFingerprints.Remove(path);
                }
            }
        }
    }

    /// <summary>
    /// Called when the quiet period expires. Creates a batch record with the
    /// exact file count, stamps every buffered event with the batch ID,
    /// and flushes them all into the debounce queue for processing.
    /// </summary>
    private void QueueFswBufferFlush()
    {
        if (!LifetimeToken.IsCancellationRequested)
        {
            TrackBackgroundTask(
                FlushFswBufferAsync(LifetimeToken),
                "file-watcher buffer flush");
        }
    }

    private async Task FlushFswBufferAsync(CancellationToken ct = default)
    {
        try
        {
            await FlushFswBufferCoreAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "FSW buffer flush failed - pipeline continues");
        }
    }

    private async Task FlushFswBufferCoreAsync(CancellationToken ct = default)
    {
        List<FileEvent> snapshot;
        lock (_fswBufferLock)
        {
            if (_fswBuffer.Count == 0)
            {
                return;
            }

            snapshot = [.. _fswBuffer];
            _fswBuffer.Clear();
            _fswFlushTimer?.Dispose();
            _fswFlushTimer = null;
        }

        var newEvents = new List<FileEvent>();
        var resumedEvents = new List<FileEvent>();
        foreach (var evt in snapshot)
        {
            var normalizedPath = Path.GetFullPath(evt.Path);
            var extension = Path.GetExtension(normalizedPath);
            if (string.IsNullOrWhiteSpace(extension) || IsIgnoredScanFile(normalizedPath))
            {
                ReleaseActivePath(evt.Path);
                continue;
            }

            if (evt.EventType != FileEventType.Deleted
                && !File.Exists(normalizedPath))
            {
                ReleaseActivePath(evt.Path);
                continue;
            }

            var trackedOperation = await GetTrackedIngestionOperationAsync(evt.Path, ct).ConfigureAwait(false);
            if (trackedOperation is null)
            {
                newEvents.Add(evt);
                continue;
            }

            if (IsTerminalMediaOperation(trackedOperation))
            {
                ReleaseActivePath(evt.Path);
                continue;
            }

            if (trackedOperation.BatchId.HasValue)
            {
                evt.BatchId = trackedOperation.BatchId;
                await RequeueTrackedIngestionOperationAsync(trackedOperation, ct).ConfigureAwait(false);
                resumedEvents.Add(evt);
                continue;
            }

            newEvents.Add(evt);
        }

        if (newEvents.Count == 0 && resumedEvents.Count == 0)
        {
            return;
        }

        if (newEvents.Count > 0)
        {
            var sourcePath = ResolveBufferedBatchSourcePath(newEvents);
            var activeBatches = await _batchRepo.GetActiveAsync(ct).ConfigureAwait(false);
            var activeBatch = activeBatches
                .Where(batch => string.Equals(batch.Status, "running", StringComparison.OrdinalIgnoreCase))
                .Where(batch => string.Equals(batch.SourcePath, sourcePath, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(batch.SourcePath, "Multiple source folders", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(sourcePath, "Multiple source folders", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(batch => batch.UpdatedAt)
                .FirstOrDefault();
            var batchId = activeBatch?.Id ?? Guid.NewGuid();

            try
            {
                if (activeBatch is null)
                {
                    await _batchRepo.CreateAsync(new IngestionBatch
                    {
                        Id = batchId,
                        Status = "running",
                        SourcePath = sourcePath,
                        FilesTotal = newEvents.Count,
                        StartedAt = DateTimeOffset.UtcNow,
                    }, ct).ConfigureAwait(false);
                }
                else
                {
                    await _batchRepo.IncrementCounterByAsync(
                        batchId,
                        BatchCounterColumn.FilesTotal,
                        newEvents.Count,
                        ct).ConfigureAwait(false);
                }

                await PublishInitialBatchProgressAsync(
                    batchId,
                    (activeBatch?.FilesTotal ?? 0) + newEvents.Count).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "Batch record creation failed for FSW flush batchId {BatchId} - pipeline continues", batchId);
            }

            foreach (var evt in newEvents)
            {
                evt.BatchId = batchId;
            }
        }

        foreach (var resumedBatchId in resumedEvents
            .Select(evt => evt.BatchId)
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .Distinct())
        {
            await PublishQueuedBatchSnapshotAsync(resumedBatchId, ct).ConfigureAwait(false);
        }

        foreach (var evt in newEvents.Concat(resumedEvents))
        {
            TrackBackgroundTask(
                EnsureIngestionOperationAsync(evt, MediaOperationStage.Discovered, LifetimeToken),
                "durable ingestion-operation discovery");
            _debounce.Enqueue(evt);
        }
    }

    private string ResolveBufferedBatchSourcePath(IReadOnlyList<FileEvent> events)
    {
        var roots = events
            .Select(evt => ResolveContainingWatchDirectory(evt.Path))
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return roots.Count switch
        {
            0 => _options.EffectiveWatchDirectories.FirstOrDefault() ?? "Unknown source folder",
            1 => roots[0]!,
            _ => "Multiple source folders",
        };
    }

    private string? ResolveContainingWatchDirectory(string filePath)
    {
        var normalizedFile = Path.GetFullPath(filePath).Replace('\\', '/').TrimEnd('/');

        return _options.EffectiveWatchDirectories
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .OrderByDescending(path => path.Length)
            .FirstOrDefault(path =>
            {
                var normalizedRoot = Path.GetFullPath(path).Replace('\\', '/').TrimEnd('/');
                return normalizedFile.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase)
                    || normalizedFile.StartsWith(normalizedRoot + "/", StringComparison.OrdinalIgnoreCase);
            });
    }

}
