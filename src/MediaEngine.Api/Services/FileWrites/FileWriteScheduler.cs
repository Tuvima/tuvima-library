using MediaEngine.Contracts.WriteBack;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Models;
using MediaEngine.Storage;

namespace MediaEngine.Api.Services.FileWrites;

/// <summary>
/// Works through the durable file write list (batched write-back D2, D3, D10).
/// <list type="bullet">
///   <item>The <b>move lane</b> runs every couple of seconds: a file whose library move was requested is renamed and moved at once,
///   through the organiser's re-file (which asks the shared mutation policy again and leaves As-is, protected and read-only files where
///   they are). Each step is written on the row before and after it happens, and a move cut short by a stop is finished or undone from
///   what is on disk when the Engine starts. Its tags are not written here; the row goes back to waiting for the scheduled run.</item>
///   <item>The <b>batch lane</b> starts a run when an administrator chose Write now, or when something is waiting and the interval
///   (<c>run_interval_minutes</c>) has passed since the last run finished (the first interval counts from when the scheduler first
///   started; after an idle spell the slots keep their regular rhythm, so a change waits for the next slot rather than being written
///   at once). A run claims the waiting rows in chunks per kind of target, hands each chunk to that kind's writer and records the
///   outcome. If the Engine was off when a run was due, the run happens shortly after it starts.</item>
/// </list>
/// Both lanes are safe to repeat: a row that was being worked on when the Engine stopped is claimed again once its lease runs out.
/// </summary>
public sealed class FileWriteScheduler(
    MediaFileWriteIntentRepository intents,
    IEnumerable<IFileWriteTargetWriter> writers,
    LibraryMoveRunner mover,
    IConfigurationLoader configuration,
    TimeProvider clock,
    ILogger<FileWriteScheduler> logger) : BackgroundService
{
    internal static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(5);
    internal static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(2);

    /// <summary>A run that was already due when the Engine started waits this long, so start-up finishes first.</summary>
    internal static readonly TimeSpan StartupGrace = TimeSpan.FromSeconds(30);

    internal const int MaxAttempts = 5;
    internal const int ChunkSize = 100;

    private readonly Lock _stateLock = new();
    private readonly HashSet<string> _warnedKinds = [];
    private DateTimeOffset? _startedAt;
    private bool _runRequested;
    private bool _running;
    private int _runTotal;
    private int _runDone;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await AnchorScheduleAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "The file write schedule could not record its starting point; it will use this start.");
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken);
                await Task.Delay(TickInterval, clock, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "The file write schedule failed; retrying.");
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), clock, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }
    }

    /// <summary>
    /// Remembers when the scheduler first started (kept in the data store) and when this start happened, then finishes or undoes any
    /// library move that a stop left part-way, before the move lane begins.
    /// </summary>
    internal async Task AnchorScheduleAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        _startedAt = now;
        await intents.EnsureAnchorAsync(now, ct);
        try
        {
            var recovered = await mover.RecoverInterruptedAsync(ct);
            if (recovered > 0)
            {
                logger.LogInformation("Looked at {Count} library move(s) that a stop left part-way.", recovered);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Library moves that a stop left part-way could not be looked at; they are tried again at the next start.");
        }
    }

    /// <summary>One pass of both lanes. Returns true when it did any work.</summary>
    public async Task<bool> TickAsync(CancellationToken ct = default)
    {
        _startedAt ??= clock.GetUtcNow();
        var worked = await RunMoveLaneAsync(ct);
        if (await ShouldStartRunAsync(ct))
        {
            worked |= await RunBatchAsync(ct);
        }
        return worked;
    }

    // ----- Write now and status ---------------------------------------------------------------------------------------------

    /// <summary>Asks for a run at the next pass. Returns false when a run is already going or already requested.</summary>
    public bool RequestRun()
    {
        lock (_stateLock)
        {
            if (_running || _runRequested)
            {
                return false;
            }
            _runRequested = true;
            return true;
        }
    }

    public FileWriteProgress GetProgress()
    {
        lock (_stateLock)
        {
            return new FileWriteProgress(_running || _runRequested, _runTotal, _runDone);
        }
    }

    /// <summary>
    /// The numbers behind Settings > Libraries > Advanced. Files in <paramref name="hiddenLibraryIds"/> (libraries the caller may not
    /// know about) are left out of the counts.
    /// </summary>
    public async Task<FileWriteScheduleDto> GetScheduleAsync(IReadOnlySet<string>? hiddenLibraryIds = null, CancellationToken ct = default)
    {
        var settings = LoadSettings();
        var counts = await intents.GetCountsAsync(hiddenLibraryIds, ct);
        var state = await intents.GetRunStateAsync(ct);
        var progress = GetProgress();
        if (hiddenLibraryIds is { Count: > 0 })
        {
            // A run writes files in every library, so its numbers would reveal how many files sit in libraries this caller may not
            // know about: show that a run is going, without its numbers.
            progress = progress with { Total = 0, Done = 0 };
        }

        return new FileWriteScheduleDto
        {
            Enabled = settings.Enabled,
            IntervalMinutes = (int)settings.RunInterval.TotalMinutes,
            WaitingCount = counts.Waiting,
            FailedCount = counts.Failed,
            IsRunning = progress.IsRunning,
            RunTotal = progress.Total,
            RunDone = progress.Done,
            LastRunAt = ParseTime(state?.LastRunFinishedAt),
            NextRunAt = settings.Enabled && counts.Waiting > 0 ? ComputeNextRun(state, settings.RunInterval) : null,
        };
    }

    /// <summary>When the next scheduled run starts, whether or not anything is waiting.</summary>
    public async Task<DateTimeOffset> GetNextRunAtAsync(CancellationToken ct = default) =>
        ComputeNextRun(await intents.GetRunStateAsync(ct), LoadSettings().RunInterval);

    private DateTimeOffset ComputeNextRun(FileWriteRunState? state, TimeSpan interval)
    {
        var next = Baseline(state) + interval;
        var earliest = (_startedAt ?? clock.GetUtcNow()) + StartupGrace;
        return next > earliest ? next : earliest;
    }

    /// <summary>The slot counts from the later of the schedule's anchor (moved forward across idle spells) and the last finished run.</summary>
    private DateTimeOffset Baseline(FileWriteRunState? state)
    {
        var anchor = ParseTime(state?.AnchorAt);
        var finished = ParseTime(state?.LastRunFinishedAt);
        return anchor is { } a && finished is { } f
            ? (a > f ? a : f)
            : anchor ?? finished ?? _startedAt ?? clock.GetUtcNow();
    }

    private async Task<bool> ShouldStartRunAsync(CancellationToken ct)
    {
        bool requested;
        lock (_stateLock)
        {
            if (_running)
            {
                return false;
            }
            requested = _runRequested;
        }

        // Only rows a run could take count: a file waiting on its move, or on a retry that is not yet due, does not start a run.
        var counts = await intents.GetCountsAsync(ct: ct);
        if (counts.Claimable == 0)
        {
            // Nothing to do: Write now with an empty list does nothing, and the schedule never runs for an empty list.
            lock (_stateLock)
            {
                _runRequested = false;
            }
            await SkipIdleSlotsAsync(ct);
            return false;
        }

        if (requested)
        {
            return true;
        }

        // With write-back off nothing new is queued and the schedule stays quiet; what is waiting is written once it is turned back on.
        var settings = LoadSettings();
        return settings.Enabled
            && clock.GetUtcNow() >= ComputeNextRun(await intents.GetRunStateAsync(ct), settings.RunInterval);
    }

    /// <summary>
    /// A slot that comes round with nothing waiting is simply skipped: the anchor moves forward by whole intervals to the latest slot
    /// that has passed, so a change made after an idle spell waits for the next slot instead of being written within seconds.
    /// </summary>
    private async Task SkipIdleSlotsAsync(CancellationToken ct)
    {
        var interval = LoadSettings().RunInterval;
        var state = await intents.GetRunStateAsync(ct);
        if (state is null)
        {
            return;
        }

        var now = clock.GetUtcNow();
        var due = Baseline(state) + interval;
        if (now < due)
        {
            return;
        }

        var passed = (long)((now - due).Ticks / interval.Ticks);
        await intents.MoveAnchorForwardAsync(due + (interval * passed), ct);
    }

    // ----- The move lane ----------------------------------------------------------------------------------------------------

    /// <summary>
    /// Moves every file whose library move was requested and is due, once each, after looking again at part-way moves whose retry time
    /// has come. A move that fails waits (a minute, five minutes, half an hour, two hours) before it is tried again, so no move is
    /// retried in a tight loop. Returns true when a move was attempted.
    /// </summary>
    internal async Task<bool> RunMoveLaneAsync(CancellationToken ct)
    {
        var worked = await mover.RecoverDueAsync(ct) > 0;
        while (true)
        {
            var intent = await intents.ClaimNextMoveAsync(LeaseDuration, ct);
            if (intent is null)
            {
                return worked;
            }

            worked = true;
            var result = await mover.MoveAsync(intent, ct);
            switch (result.Kind)
            {
                case LibraryMoveRunner.MoveResultKind.Done:
                    if (result.Completed)
                    {
                        logger.LogInformation(
                            "Library move for asset {AssetId} finished after a part-way step was settled; its tags wait for the next write run.",
                            intent.AssetId);
                        break;
                    }

                    var done = await intents.CompleteMoveAsync(
                        intent.Id, intent.Generation, LoadSettings().AllowsTrigger(MediaFileWriteTriggers.LibraryMove), ct);
                    logger.LogInformation(
                        "Library move for asset {AssetId} finished; its tags wait for the next write run{Stale}.",
                        intent.AssetId, done ? string.Empty : " (a newer change arrived while it ran, so the move is planned again)");
                    break;
                case LibraryMoveRunner.MoveResultKind.Retry:
                    var wait = FileWriteBackoff.After(intent.Attempts);
                    await intents.FailMoveAsync(intent.Id, wait is null ? "failed" : "pending", result.Reason, wait, ct);
                    break;
                case LibraryMoveRunner.MoveResultKind.Lost:
                    logger.LogWarning("The file of asset {AssetId} was lost mid-move; it is on the failed list.", intent.AssetId);
                    break;
                default:
                    // Not settled yet: the row keeps its steps (or its request) and is looked at again later. An unplugged drive can hold
                    // thousands of files, so waiting for one is logged quietly.
                    if (result.Reason == LibraryMoveRunner.OfflineReason)
                    {
                        logger.LogDebug("Library move for asset {AssetId} waits: {Reason}", intent.AssetId, result.Reason);
                    }
                    else
                    {
                        logger.LogWarning("Library move for asset {AssetId} is part-way: {Reason}", intent.AssetId, result.Reason);
                    }
                    break;
            }
        }
    }

    // ----- The batch lane ---------------------------------------------------------------------------------------------------

    /// <summary>Writes everything that is waiting, once. Returns false when another run is going or nothing was waiting.</summary>
    public async Task<bool> RunBatchAsync(CancellationToken ct = default)
    {
        lock (_stateLock)
        {
            if (_running)
            {
                return false;
            }
            _running = true;
            _runRequested = false;
            _runDone = 0;
            _runTotal = 0;
        }

        var startedAt = clock.GetUtcNow();
        int written = 0, failed = 0, kept = 0, handledCount = 0;
        try
        {
            var counts = await intents.GetCountsAsync(ct: ct);
            lock (_stateLock)
            {
                _runTotal = counts.Claimable;
            }

            // Rows are marked with this run as they are claimed, so no file is taken twice however many chunks the run needs.
            var runId = Guid.NewGuid().ToString("N");
            foreach (var kind in new[] { MediaFileWriteIntentRepository.MediaAssetKind, MediaFileWriteIntentRepository.ViewItemKind })
            {
                var writer = writers.FirstOrDefault(candidate => string.Equals(candidate.TargetKind, kind, StringComparison.Ordinal));
                if (writer is null)
                {
                    WarnOnce(kind);
                    continue;
                }

                var begun = false;
                try
                {
                    while (true)
                    {
                        ct.ThrowIfCancellationRequested();
                        var chunk = await intents.ClaimBatchAsync(kind, ChunkSize, LeaseDuration, startedAt, runId, ct);
                        if (chunk.Count == 0)
                        {
                            break;
                        }

                        if (!begun)
                        {
                            // The writer's helper program starts once, when this run first has a row for it.
                            begun = true;
                            await writer.BeginRunAsync(ct);
                        }

                        var results = await WriteChunkAsync(writer, chunk, ct);
                        foreach (var intent in chunk)
                        {
                            var result = results.TryGetValue(intent.Id, out var found)
                                ? found
                                : new FileWriteResult(intent.Id, WriteBackOutcome.Failed("The writer gave no result for this file."));
                            if (result.ReadAgainFirst)
                            {
                                // The file was changed outside Library and has been read back; it waits for the next run, attempt not counted.
                                await intents.ReleaseForReReadAsync(intent.Id, intent.Generation, result.Outcome.Reason, ct);
                                continue;
                            }

                            var (status, error) = MapCompletion(intent, result.Outcome);
                            if (!await intents.CompleteByIdAsync(intent.Id, intent.Generation, status, error, ct))
                            {
                                logger.LogInformation(
                                    "Ignored stale write completion for {Kind} {Id}, generation {Generation}; a newer change is waiting.",
                                    kind, intent.Id, intent.Generation);
                            }
                            else
                            {
                                logger.LogInformation(
                                    "File write {OperationToken} for {Kind} {Id} completed as {Status} after attempt {Attempt}.",
                                    intent.OperationToken, kind, intent.Id, status, intent.Attempts);
                            }

                            switch (status)
                            {
                                case "verified":
                                    written++;
                                    break;
                                case "blocked":
                                case "unsupported":
                                    kept++;
                                    break;
                                default:
                                    failed++;
                                    break;
                            }
                        }

                        handledCount += chunk.Count;
                        lock (_stateLock)
                        {
                            _runDone = handledCount;
                            _runTotal = Math.Max(_runTotal, handledCount);
                        }

                        // A library move asked for during a long run still happens within seconds, not after the whole run. A failure there
                        // must not stop the run: the move lane is tried again on the next pass.
                        try
                        {
                            await RunMoveLaneAsync(ct);
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            logger.LogWarning(ex, "The move lane failed between write chunks; the run carries on and moves are tried again on the next pass.");
                        }
                    }
                }
                finally
                {
                    if (begun)
                    {
                        await EndWriterRunAsync(writer);
                    }
                }
            }

            return handledCount > 0;
        }
        finally
        {
            try
            {
                if (handledCount > 0)
                {
                    // A run stopped part-way is recorded for the files it did handle (the rest come back by lease and the next run
                    // counts them again).
                    await intents.RecordRunAsync(startedAt, clock.GetUtcNow(), written, failed, kept, CancellationToken.None);
                }
                else if (!ct.IsCancellationRequested)
                {
                    // A run that found nothing to write still counts as a pass: the next slot is a whole interval away, so the
                    // schedule does not start another run every couple of seconds.
                    await intents.MoveAnchorForwardAsync(clock.GetUtcNow(), CancellationToken.None);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "The result of the file write run could not be recorded.");
            }

            lock (_stateLock)
            {
                _running = false;
            }
        }
    }

    /// <summary>Lets a writer put its helper program away. A stop must still end it, and a failure here never fails the run.</summary>
    private async Task EndWriterRunAsync(IFileWriteTargetWriter writer)
    {
        try
        {
            await writer.EndRunAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "The {Kind} writer could not finish its run cleanly.", writer.TargetKind);
        }
    }

    private async Task<Dictionary<Guid, FileWriteResult>> WriteChunkAsync(
        IFileWriteTargetWriter writer, IReadOnlyList<MediaFileWriteIntent> chunk, CancellationToken ct)
    {
        var outcomes = new Dictionary<Guid, FileWriteResult>();
        try
        {
            foreach (var result in await writer.WriteBatchAsync(chunk, ct))
            {
                outcomes[result.IntentId] = result;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Lease expiry safely recovers an interrupted run.
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "A batch of {Count} {Kind} file write(s) failed; each will be retried.", chunk.Count, writer.TargetKind);
            foreach (var intent in chunk)
            {
                outcomes.TryAdd(intent.Id, new FileWriteResult(intent.Id, WriteBackOutcome.Failed(ex.Message)));
            }
        }

        return outcomes;
    }

    private void WarnOnce(string kind)
    {
        bool first;
        lock (_stateLock)
        {
            first = _warnedKinds.Add(kind);
        }

        if (first)
        {
            logger.LogWarning("Files of kind {Kind} are waiting to be written but nothing can write them yet; they stay on the list.", kind);
        }
    }

    private WriteBackConfiguration LoadSettings() =>
        configuration.LoadConfig<WriteBackConfiguration>(string.Empty, "writeback") ?? new WriteBackConfiguration();

    private static DateTimeOffset? ParseTime(string? value) =>
        DateTimeOffset.TryParse(value, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind, out var parsed) ? parsed : null;

    internal static (string Status, string? Error) MapCompletion(
        MediaFileWriteIntent intent, WriteBackOutcome outcome) => outcome.Kind switch
        {
            WriteBackOutcomeKind.Verified => ("verified", null),
            WriteBackOutcomeKind.Blocked => ("blocked", outcome.Reason),
            WriteBackOutcomeKind.Unsupported => ("unsupported", outcome.Reason),
            WriteBackOutcomeKind.Unverified when intent.Attempts < MaxAttempts =>
                ("pending", outcome.Reason ?? "Physical read-back was unverified."),
            WriteBackOutcomeKind.Failed when intent.Attempts < MaxAttempts =>
                ("pending", outcome.Reason ?? "Write-back failed."),
            WriteBackOutcomeKind.Unverified =>
                ("failed", outcome.Reason ?? "Physical read-back remained unverified."),
            _ => ("failed", outcome.Reason ?? "Write-back failed."),
        };
}

/// <summary>How far the current write run has got (for the status action).</summary>
public sealed record FileWriteProgress(bool IsRunning, int Total, int Done);
