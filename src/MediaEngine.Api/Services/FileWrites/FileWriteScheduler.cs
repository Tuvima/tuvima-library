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
///   they are). Its tags are not written here; the row goes back to waiting for the scheduled run.</item>
///   <item>The <b>batch lane</b> starts a run when an administrator chose Write now, or when something is waiting and the interval
///   (<c>run_interval_minutes</c>) has passed since the last run finished (the first interval counts from when the scheduler first
///   started). A run claims the waiting rows in chunks per kind of target, hands each chunk to that kind's writer and records the
///   outcome. If the Engine was off when a run was due, the run happens shortly after it starts.</item>
/// </list>
/// Both lanes are safe to repeat: a row that was being worked on when the Engine stopped is claimed again once its lease runs out.
/// </summary>
public sealed class FileWriteScheduler(
    MediaFileWriteIntentRepository intents,
    IEnumerable<IFileWriteTargetWriter> writers,
    IAutoOrganizeService organizer,
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

    /// <summary>Remembers when the scheduler first started (kept in the data store) and when this start happened.</summary>
    internal async Task AnchorScheduleAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        _startedAt = now;
        await intents.EnsureAnchorAsync(now, ct);
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

    /// <summary>The numbers behind Settings > Libraries > Advanced.</summary>
    public async Task<FileWriteScheduleDto> GetScheduleAsync(CancellationToken ct = default)
    {
        var settings = LoadSettings();
        var counts = await intents.GetCountsAsync(ct);
        var state = await intents.GetRunStateAsync(ct);
        var progress = GetProgress();
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
        var baseline = ParseTime(state?.LastRunFinishedAt) ?? ParseTime(state?.AnchorAt) ?? _startedAt ?? clock.GetUtcNow();
        var next = baseline + interval;
        var earliest = (_startedAt ?? clock.GetUtcNow()) + StartupGrace;
        return next > earliest ? next : earliest;
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

        var counts = await intents.GetCountsAsync(ct);
        if (counts.Waiting == 0)
        {
            // Nothing to do: Write now with an empty list does nothing, and the schedule never runs for an empty list.
            lock (_stateLock)
            {
                _runRequested = false;
            }
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

    // ----- The move lane ----------------------------------------------------------------------------------------------------

    /// <summary>Moves every file whose library move was requested, once each. Returns true when a move was attempted.</summary>
    internal async Task<bool> RunMoveLaneAsync(CancellationToken ct)
    {
        var handled = new List<Guid>();
        while (true)
        {
            var intent = await intents.ClaimNextMoveAsync(LeaseDuration, handled, ct);
            if (intent is null)
            {
                return handled.Count > 0;
            }

            handled.Add(intent.Id);
            var failure = await RefileAsync(intent, ct);
            if (failure is null)
            {
                var done = await intents.CompleteMoveAsync(intent.Id, intent.Generation, ct);
                logger.LogInformation(
                    "Library move for asset {AssetId} finished; its tags wait for the next write run{Stale}.",
                    intent.AssetId, done ? string.Empty : " (a newer change arrived while it ran)");
                continue;
            }

            var status = intent.Attempts < MaxAttempts ? "pending" : "failed";
            await intents.FailMoveAsync(intent.Id, intent.Generation, status, failure, ct);
        }
    }

    /// <summary>
    /// Renames and moves the file of a library move. Returns why it should be tried again, or null when the file is where it should be
    /// or was deliberately left alone (the organiser records that in the activity history).
    /// </summary>
    private async Task<string?> RefileAsync(MediaFileWriteIntent intent, CancellationToken ct)
    {
        try
        {
            var filed = await organizer.RefileAsync(intent.AssetId, ct);
            return filed.Kind == AssetRefileOutcomeKind.Failed ? filed.Reason ?? "The file could not be renamed and moved." : null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Lease expiry safely recovers an interrupted process.
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Renaming the file of asset {AssetId} after a library move failed.", intent.AssetId);
            return ex.Message;
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
            var counts = await intents.GetCountsAsync(ct);
            lock (_stateLock)
            {
                _runTotal = counts.Waiting;
            }

            var handled = new HashSet<Guid>();
            foreach (var kind in new[] { MediaFileWriteIntentRepository.MediaAssetKind, MediaFileWriteIntentRepository.ViewItemKind })
            {
                var writer = writers.FirstOrDefault(candidate => string.Equals(candidate.TargetKind, kind, StringComparison.Ordinal));
                if (writer is null)
                {
                    WarnOnce(kind);
                    continue;
                }

                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    var chunk = await intents.ClaimBatchAsync(kind, ChunkSize, LeaseDuration, startedAt, handled, ct);
                    if (chunk.Count == 0)
                    {
                        break;
                    }

                    foreach (var intent in chunk)
                    {
                        handled.Add(intent.Id);
                    }

                    var results = await WriteChunkAsync(writer, chunk, ct);
                    foreach (var intent in chunk)
                    {
                        var outcome = results.TryGetValue(intent.Id, out var result)
                            ? result
                            : WriteBackOutcome.Failed("The writer gave no result for this file.");
                        var (status, error) = MapCompletion(intent, outcome);
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
                }
            }

            return handledCount > 0;
        }
        finally
        {
            if (handledCount > 0)
            {
                // A stop part-way records nothing here: the rows come back by lease and the next run counts again.
                try
                {
                    await intents.RecordRunAsync(startedAt, clock.GetUtcNow(), written, failed, kept, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "The result of the file write run could not be recorded.");
                }
            }

            lock (_stateLock)
            {
                _running = false;
            }
        }
    }

    private async Task<Dictionary<Guid, WriteBackOutcome>> WriteChunkAsync(
        IFileWriteTargetWriter writer, IReadOnlyList<MediaFileWriteIntent> chunk, CancellationToken ct)
    {
        var outcomes = new Dictionary<Guid, WriteBackOutcome>();
        try
        {
            foreach (var result in await writer.WriteBatchAsync(chunk, ct))
            {
                outcomes[result.IntentId] = result.Outcome;
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
                outcomes.TryAdd(intent.Id, WriteBackOutcome.Failed(ex.Message));
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
