using System.Diagnostics;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Ingestion.Organise;

/// <summary>Which merge to run: the operation that owns it and the folder whose files are merged.</summary>
public sealed record FolderMergeJob(Guid OperationId, Guid SourceId);

/// <summary>
/// Runs the merge job over one folder: finishes anything a stopped run left half done, walks the media files in a stable order,
/// copies (or moves) each one into the target with the same naming, duplicate and tag rules as converting a folder, retries
/// files that were in use at the end of the pass, and leaves a ledger entry for everything it looked at. In Copy mode it never
/// writes to the source. In Move mode it deletes an original only after its copy has been checked byte for byte, and removes
/// the source folders it has emptied (never the source folder itself).
/// </summary>
public sealed class FolderMergeRunner
{
    private const int LeftoverListLimit = 5000;

    private readonly IConfigurationLoader _configuration;
    private readonly IMediaAssetRepository _assets;
    private readonly IFolderOrganiseLogRepository _ledger;
    private readonly FolderMergeOrganiser _organiser;
    private readonly IMediaTypeExtensionCatalog? _extensions;
    private readonly IFileIntake? _intake;
    private readonly FolderOrganiseTiming _timing;
    private readonly ILogger<FolderMergeRunner> _logger;

    public FolderMergeRunner(
        IConfigurationLoader configuration,
        IMediaAssetRepository assets,
        IFolderOrganiseLogRepository ledger,
        FolderMergeOrganiser organiser,
        ILogger<FolderMergeRunner> logger,
        IMediaTypeExtensionCatalog? extensions = null,
        IFileIntake? intake = null,
        FolderOrganiseTiming? timing = null)
    {
        _configuration = configuration;
        _assets = assets;
        _ledger = ledger;
        _organiser = organiser;
        _extensions = extensions;
        _intake = intake;
        _timing = timing ?? new FolderOrganiseTiming();
        _logger = logger;
    }

    public async Task<FolderOrganiseRunResult> RunAsync(
        FolderMergeJob job,
        IFolderOrganiseHost host,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(host);

        var context = Resolve(job, out var check);
        if (context is null)
        {
            // Copies already made are still recorded, even though nothing more may be changed on disk.
            foreach (var unfinished in await _ledger.ListUnfinishedAsync(job.OperationId, ct).ConfigureAwait(false))
            {
                await _organiser.RecoverPathOnlyAsync(unfinished, ct).ConfigureAwait(false);
            }

            return new FolderOrganiseRunResult { Stop = StopFor(check) };
        }

        // 1. Finish what a stopped run left half done.
        foreach (var unfinished in await _ledger.ListUnfinishedAsync(job.OperationId, ct).ConfigureAwait(false))
        {
            await _organiser.RecoverAsync(unfinished, context, ct).ConfigureAwait(false);
        }

        // 2. See what the folder holds now: files on disk, and which of them the library already knows.
        var disk = FolderMediaEnumerator.Enumerate(context.SourceRoot, context.Source.IncludeSubdirectories, MediaExtensions, ct: ct, skipLinks: true);
        var catalogued = await CataloguedByPathAsync(context, ct).ConfigureAwait(false);
        var finished = await _ledger.GetFinishedPathsAsync(job.OperationId, ct).ConfigureAwait(false);
        var work = disk.Where(path => !finished.Contains(path)).ToList();

        // After a Move resumes, the files already moved are no longer on disk but still count towards the job's total.
        var total = disk.Count + await CountFinishedAndGoneAsync(job, disk, ct).ConfigureAwait(false);
        var completed = Math.Max(0, total - work.Count);
        var failed = (await _ledger.CountByOutcomeAsync(job.OperationId, ct).ConfigureAwait(false))
            .GetValueOrDefault(FolderOrganiseOutcomes.Failed);
        var progress = Stopwatch.StartNew();
        var lastRefresh = Stopwatch.StartNew();
        var deferred = new List<string>();
        var deferredReasons = new Dictionary<string, string>(PathSafety.Comparer);

        await host.ReportProgressAsync(new FolderOrganiseProgress(total, completed, failed, null), ct).ConfigureAwait(false);

        // 3. The pass.
        for (var index = 0; index < work.Count; index++)
        {
            var path = work[index];
            ct.ThrowIfCancellationRequested();

            var signal = await host.GetSignalAsync(ct).ConfigureAwait(false);
            if (signal != FolderJobSignal.Continue)
            {
                var remaining = work.Skip(index).Concat(deferred).ToList();
                return await StopAsync(job, host, FromSignal(signal), remaining, total, completed, failed, ct).ConfigureAwait(false);
            }

            // A Move deletes originals, so it reads the saved settings before every file; a Copy only needs the regular refresh.
            if (context.IsMove || lastRefresh.Elapsed >= _timing.SettingsRefresh)
            {
                lastRefresh.Restart();
                var refreshed = Resolve(job, out var refreshCheck);
                if (refreshed is null)
                {
                    var remaining = work.Skip(index).Concat(deferred).ToList();
                    return await StopAsync(job, host, StopFor(refreshCheck), remaining, total, completed, failed, ct).ConfigureAwait(false);
                }

                context = refreshed;
            }

            var outcome = await ProcessAsync(path, catalogued, context, host, () => new FolderOrganiseProgress(total, completed, failed, path), ct)
                .ConfigureAwait(false);
            if (outcome.Locked)
            {
                deferred.Add(path);
                deferredReasons[path] = outcome.Detail ?? string.Empty;
                continue;
            }

            completed++;
            if (outcome.Outcome == FolderOrganiseOutcomes.Failed)
            {
                failed++;
            }

            if (progress.Elapsed >= _timing.ProgressInterval)
            {
                progress.Restart();
                await host.ReportProgressAsync(new FolderOrganiseProgress(total, completed, failed, path), ct).ConfigureAwait(false);
            }
        }

        // 4. Files that were in use (open for playback, say) are tried again, a little later each round.
        for (var round = 1; round <= _timing.RetryRounds && deferred.Count > 0; round++)
        {
            await Task.Delay(TimeSpan.FromTicks(_timing.RetryDelay.Ticks * (1L << (round - 1))), ct).ConfigureAwait(false);

            var stillLocked = new List<string>();
            for (var index = 0; index < deferred.Count; index++)
            {
                var path = deferred[index];
                var signal = await host.GetSignalAsync(ct).ConfigureAwait(false);
                if (signal != FolderJobSignal.Continue)
                {
                    var remaining = deferred.Skip(index).Concat(stillLocked).ToList();
                    return await StopAsync(job, host, FromSignal(signal), remaining, total, completed, failed, ct).ConfigureAwait(false);
                }

                var outcome = await ProcessAsync(path, catalogued, context, host, () => new FolderOrganiseProgress(total, completed, failed, path), ct)
                    .ConfigureAwait(false);
                if (outcome.Locked)
                {
                    stillLocked.Add(path);
                    deferredReasons[path] = outcome.Detail ?? string.Empty;
                    continue;
                }

                completed++;
                if (outcome.Outcome == FolderOrganiseOutcomes.Failed)
                {
                    failed++;
                }
            }

            deferred = stillLocked;
            await host.ReportProgressAsync(new FolderOrganiseProgress(total, completed, failed, null), ct).ConfigureAwait(false);
        }

        foreach (var path in deferred)
        {
            var reason = deferredReasons.GetValueOrDefault(path);
            await _ledger.UpsertAsync(
                new FolderOrganiseLogEntry(
                    job.OperationId, job.SourceId, path, path, FolderOrganiseOutcomes.Failed, DateTimeOffset.UtcNow,
                    string.IsNullOrWhiteSpace(reason)
                        ? "The file was in use or could not be copied, even after trying again. It is still in the source folder."
                        : $"{reason} It is still in the source folder."),
                ct).ConfigureAwait(false);
            completed++;
            failed++;
        }

        // 5. After a Move, anything the job did not move is listed, so nothing is left unexplained.
        if (context.IsMove)
        {
            await ListLeftoversAsync(job, context, ct).ConfigureAwait(false);
        }

        await host.ReportProgressAsync(new FolderOrganiseProgress(total, completed, failed, null), ct).ConfigureAwait(false);
        return new FolderOrganiseRunResult
        {
            Stop = FolderOrganiseStop.Finished,
            Total = total,
            Completed = completed,
            Failed = failed,
            IsMove = context.IsMove,
            Outcomes = await _ledger.CountByOutcomeAsync(job.OperationId, ct).ConfigureAwait(false),
        };
    }

    /// <summary>Media files the ledger finished that are no longer in the source (moved out, or removed as duplicates).</summary>
    private async Task<int> CountFinishedAndGoneAsync(FolderMergeJob job, IReadOnlyList<string> disk, CancellationToken ct)
    {
        var onDisk = new HashSet<string>(disk, PathSafety.Comparer);
        var count = 0;
        var offset = 0;
        const int pageSize = 1000;
        while (true)
        {
            var page = await _ledger.ListAsync(job.OperationId, offset, pageSize, ct).ConfigureAwait(false);
            count += page.Count(entry => entry.Outcome is FolderOrganiseOutcomes.Moved
                or FolderOrganiseOutcomes.MovedWithUniqueName
                or FolderOrganiseOutcomes.DuplicateRemoved
                or FolderOrganiseOutcomes.MergedForReview
                && MediaExtensions.Contains(Path.GetExtension(entry.OldPath))
                && !onDisk.Contains(entry.OldPath)
                && !File.Exists(entry.OldPath));
            if (page.Count < pageSize)
            {
                return count;
            }

            offset += pageSize;
        }
    }

    private IReadOnlySet<string> MediaExtensions =>
        _extensions?.GetAllMediaExtensions() ?? FolderMediaEnumerator.DefaultMediaExtensions;

    private FolderMergeContext? Resolve(FolderMergeJob job, out FolderMergeCheck check) =>
        FolderMergeContext.TryCreateForJob(_configuration.LoadLibraries(), job.OperationId, job.SourceId, out check);

    private static FolderOrganiseStop StopFor(FolderMergeCheck check) => check.Problem switch
    {
        FolderMergeProblem.SourceRemoved => FolderOrganiseStop.SourceMissing,
        FolderMergeProblem.NotActive => FolderOrganiseStop.Cancelled,
        FolderMergeProblem.MoveNeedsManagedSource => FolderOrganiseStop.SourceNotManaged,
        _ => FolderOrganiseStop.TargetUnavailable,
    };

    private async Task<ConversionOutcome> ProcessAsync(
        string path,
        IReadOnlyDictionary<string, MediaAssetFileRef> catalogued,
        FolderMergeContext context,
        IFolderOrganiseHost host,
        Func<FolderOrganiseProgress> snapshot,
        CancellationToken ct)
    {
        // A big file can take longer than the job's lease to copy; the job keeps reporting in while it does.
        using var beating = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var heartbeat = HeartbeatAsync(host, snapshot, beating.Token);
        try
        {
            // A merge that was stopped and started again carries on from the copies the earlier run already made.
            var carriedOn = await _organiser.TryCarryOnEarlierCopyAsync(path, context, ct).ConfigureAwait(false);
            if (carriedOn is not null)
            {
                return carriedOn;
            }

            if (!catalogued.TryGetValue(path, out var file))
            {
                return await _organiser.HandleUncataloguedAsync(path, context, _intake, ct).ConfigureAwait(false);
            }

            var decision = await _organiser.DecideAsync(file, context, plannedTargets: null, ct).ConfigureAwait(false);
            return await _organiser.ApplyAsync(file, decision, context, ct).ConfigureAwait(false);
        }
        finally
        {
            await beating.CancelAsync().ConfigureAwait(false);
            try
            {
                await heartbeat.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // The file is done (or the job is stopping); the heartbeat ending is the expected way for it to finish.
            }
        }
    }

    private async Task HeartbeatAsync(IFolderOrganiseHost host, Func<FolderOrganiseProgress> snapshot, CancellationToken ct)
    {
        if (_timing.HeartbeatInterval <= TimeSpan.Zero)
        {
            return;
        }

        using var timer = new PeriodicTimer(_timing.HeartbeatInterval);
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            try
            {
                await host.ReportProgressAsync(snapshot(), ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A missed progress report must never fail the merge; the next beat (or the end of the file) reports again.
                _logger.LogDebug(ex, "A merge progress report did not go through");
            }
        }
    }

    private async Task<IReadOnlyDictionary<string, MediaAssetFileRef>> CataloguedByPathAsync(
        FolderMergeContext context, CancellationToken ct)
    {
        var map = new Dictionary<string, MediaAssetFileRef>(PathSafety.Comparer);
        foreach (var file in await _assets.ListFilesUnderAsync(context.SourceRoot, ct).ConfigureAwait(false))
        {
            map.TryAdd(Path.GetFullPath(file.FilePath), file);
        }

        return map;
    }

    private static FolderOrganiseStop FromSignal(FolderJobSignal signal) => signal switch
    {
        FolderJobSignal.Pause => FolderOrganiseStop.Paused,
        FolderJobSignal.Cancel => FolderOrganiseStop.Cancelled,
        _ => FolderOrganiseStop.TargetUnavailable,
    };

    private async Task<FolderOrganiseRunResult> StopAsync(
        FolderMergeJob job,
        IFolderOrganiseHost host,
        FolderOrganiseStop stop,
        IReadOnlyList<string> remaining,
        int total,
        int completed,
        int failed,
        CancellationToken ct)
    {
        // A merge that ends here (not just paused) keeps what was copied. The log says what is still only in the source folder.
        if (stop != FolderOrganiseStop.Paused)
        {
            await RecordLeftInSourceAsync(job, remaining, ct).ConfigureAwait(false);
        }

        await host.ReportProgressAsync(new FolderOrganiseProgress(total, completed, failed, null), ct).ConfigureAwait(false);
        return new FolderOrganiseRunResult
        {
            Stop = stop,
            Total = total,
            Completed = completed,
            Failed = failed,
            Outcomes = await _ledger.CountByOutcomeAsync(job.OperationId, ct).ConfigureAwait(false),
        };
    }

    private async Task RecordLeftInSourceAsync(FolderMergeJob job, IReadOnlyList<string> remaining, CancellationToken ct)
    {
        var finished = await _ledger.GetFinishedPathsAsync(job.OperationId, ct).ConfigureAwait(false);
        var listed = 0;
        foreach (var path in remaining)
        {
            if (finished.Contains(path) || listed >= LeftoverListLimit)
            {
                continue;
            }

            await _ledger.UpsertAsync(
                new FolderOrganiseLogEntry(
                    job.OperationId, job.SourceId, path, path, FolderOrganiseOutcomes.LeftInSource, DateTimeOffset.UtcNow,
                    "The merge was stopped before this file was reached. It is still only in the source folder."),
                ct).ConfigureAwait(false);
            listed++;
        }
    }

    /// <summary>
    /// After a Move: in folders that files moved out of, lists what is still there (notes, a stray picture, anything the job
    /// does not know how to move) as "not moved", and leaves those folders in place.
    /// </summary>
    private async Task ListLeftoversAsync(FolderMergeJob job, FolderMergeContext context, CancellationToken ct)
    {
        var touched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var offset = 0;
        const int pageSize = 1000;
        while (true)
        {
            var page = await _ledger.ListAsync(job.OperationId, offset, pageSize, ct).ConfigureAwait(false);
            foreach (var entry in page.Where(item => item.Outcome is FolderOrganiseOutcomes.Moved
                         or FolderOrganiseOutcomes.MovedWithUniqueName
                         or FolderOrganiseOutcomes.DuplicateRemoved
                         or FolderOrganiseOutcomes.SidecarMoved
                         or FolderOrganiseOutcomes.MergedForReview))
            {
                if (Path.GetDirectoryName(entry.OldPath) is { Length: > 0 } folder)
                {
                    touched.Add(folder);
                }
            }

            if (page.Count < pageSize)
            {
                break;
            }

            offset += pageSize;
        }

        var finished = await _ledger.GetFinishedPathsAsync(job.OperationId, ct).ConfigureAwait(false);
        var listed = 0;
        foreach (var folder in touched.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(folder) || !PathSafety.IsContainedBy(folder, context.SourceRoot))
            {
                continue;
            }

            string[] left;
            try
            {
                left = Directory.GetFiles(folder);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogDebug(ex, "Could not list what is left in {Folder}", folder);
                continue;
            }

            foreach (var file in left.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                if (finished.Contains(file) || listed >= LeftoverListLimit)
                {
                    continue;
                }

                await _ledger.UpsertAsync(
                    new FolderOrganiseLogEntry(
                        job.OperationId, job.SourceId, file, file, FolderOrganiseOutcomes.NotMoved, DateTimeOffset.UtcNow,
                        "Left where it was: Library does not know how to move this kind of file."),
                    ct).ConfigureAwait(false);
                listed++;
            }
        }
    }
}
