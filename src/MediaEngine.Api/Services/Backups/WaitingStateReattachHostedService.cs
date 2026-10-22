using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Storage.Backup;

namespace MediaEngine.Api.Services.Backups;

/// <summary>
/// After a restore, puts a person's waiting state (progress, bookmarks, favourites, typed corrections) back on its title as the title
/// comes into the Library, and clears what has waited too long (backup and restore spec D3 to D5, D13). It runs one pass shortly after
/// start-up, one each time the ingestion queue goes from working to idle, and at least one a day so the waiting period is enforced even
/// in a quiet Library. It costs one count while nothing is waiting, so a Library that was never restored pays nothing. The waiting
/// period (<c>missing_file_retention_days</c>) is read from the settings on every run, so changing it affects rows already waiting.
/// A run looks at every waiting row once, in batches, however many there are: the write lock is free between batches, there is a short
/// pause after each so playback and ingestion writes are not starved, and the run stops (to be resumed when the queue is idle again) as
/// soon as ingestion becomes busy.
/// </summary>
public sealed class WaitingStateReattachHostedService : BackgroundService
{
    internal const int BatchSize = 200;

    /// <summary>The pause between two batches of one run.</summary>
    internal static readonly TimeSpan BatchPause = TimeSpan.FromMilliseconds(100);

    internal static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(45);
    internal static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(1);
    internal static readonly TimeSpan PurgeInterval = TimeSpan.FromDays(1);

    private readonly WaitingPersonalStateRepository _repository;
    private readonly IIngestionQueueProgressService _ingestion;
    private readonly Func<int> _retentionDays;
    private readonly ILogger<WaitingStateReattachHostedService> _logger;
    private readonly int _batchSize;
    private readonly Action<TimeSpan, CancellationToken> _pause;
    private volatile bool _unfinished;

    public WaitingStateReattachHostedService(
        WaitingPersonalStateRepository repository,
        IIngestionQueueProgressService ingestion,
        IConfigurationLoader configuration,
        ILogger<WaitingStateReattachHostedService> logger)
        : this(repository, ingestion, () => configuration.LoadMaintenance().MissingFileRetentionDays, logger)
    {
    }

    /// <summary>
    /// A test seam: <paramref name="retentionDays"/> supplies the waiting period each run; <paramref name="batchSize"/> and
    /// <paramref name="pause"/> (the wait between two batches, which returns when the time is up or the run is cancelled) default to
    /// the real ones.
    /// </summary>
    internal WaitingStateReattachHostedService(
        WaitingPersonalStateRepository repository,
        IIngestionQueueProgressService ingestion,
        Func<int> retentionDays,
        ILogger<WaitingStateReattachHostedService> logger,
        int batchSize = BatchSize,
        Action<TimeSpan, CancellationToken>? pause = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(ingestion);
        ArgumentNullException.ThrowIfNull(retentionDays);
        ArgumentNullException.ThrowIfNull(logger);
        _repository = repository;
        _ingestion = ingestion;
        _retentionDays = retentionDays;
        _logger = logger;
        _batchSize = batchSize;
        _pause = pause ?? ((span, token) => token.WaitHandle.WaitOne(span));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(InitialDelay, stoppingToken).ConfigureAwait(false);
            var wasWorking = IsWorking();
            var lastRun = DateTimeOffset.MinValue;
            var due = true;
            while (!stoppingToken.IsCancellationRequested)
            {
                var working = IsWorking();

                // The Library has just finished taking files in: a title may have arrived.
                due |= wasWorking && !working;
                wasWorking = working;
                var now = DateTimeOffset.UtcNow;
                due |= now - lastRun >= PurgeInterval;
                if (due && !working)
                {
                    _unfinished = false;
                    await RunOnceSafelyAsync(now, stoppingToken).ConfigureAwait(false);
                    lastRun = now;

                    // A run that stopped because ingestion got busy carries on once the queue is idle again.
                    due = _unfinished;
                }

                await Task.Delay(PollInterval, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _logger.LogDebug("Restored state re-attach stopped.");
        }
    }

    private bool IsWorking() => string.Equals(_ingestion.Current.State, "working", StringComparison.OrdinalIgnoreCase);

    private async Task RunOnceSafelyAsync(DateTimeOffset now, CancellationToken ct)
    {
        try
        {
            await Task.Run(() => RunOnce(now, ct), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Nothing here names a title; the next run tries again.
            _logger.LogWarning(ex, "Restored state could not be re-attached this time; it will be tried again.");
        }
    }

    /// <summary>
    /// Looks at every waiting row once, in batches, when anything is waiting. The write lock is not held between batches, and there is
    /// a short pause after each; if ingestion becomes busy the run stops early (<c>_unfinished</c> asks the loop for another run when
    /// the queue is idle). Logs one line when something was re-attached or cleared, and one Warning (counts and table names, never a
    /// title) when rows could not be written. Returns null when nothing was waiting.
    /// </summary>
    internal ReattachResult? RunOnce(DateTimeOffset now, CancellationToken ct)
    {
        if (_repository.CountWaiting(includeWithheld: true) == 0)
        {
            return null;
        }

        // Read every run, and kept inside the limits the settings allow, so a bad value can never clear everything at once.
        var retentionDays = Math.Clamp(_retentionDays(), MissingFileRetentionLimits.MinDays, MissingFileRetentionLimits.MaxDays);
        int attached = 0, yielded = 0, purged = 0, stillWaiting = 0, failed = 0;
        var failedTables = new SortedSet<string>(StringComparer.Ordinal);
        while (true)
        {
            var result = _repository.RunPass(now, _batchSize, retentionDays, ct);
            attached += result.Attached;
            yielded += result.Yielded;
            purged += result.Purged;
            failed += result.FailedRows;
            failedTables.UnionWith(result.FailedTables);
            stillWaiting = result.StillWaiting;

            // Every row of a batch is removed or stamped as looked at, so a short batch means each row has been seen once.
            if (result.Checked < _batchSize)
            {
                break;
            }

            if (IsWorking())
            {
                _unfinished = true;
                break;
            }

            _pause(BatchPause, ct);
            ct.ThrowIfCancellationRequested();
        }

        if (attached > 0 || yielded > 0 || purged > 0)
        {
            _logger.LogInformation(
                "Restored state: {Attached} re-attached, {Yielded} left to rows made since, {StillWaiting} still waiting, {Purged} cleared after the waiting period",
                attached,
                yielded,
                stillWaiting,
                purged);
        }

        if (failed > 0)
        {
            // Counts and tables only: a title is exactly what must not be written down. Per-row detail is at Debug in the repository.
            _logger.LogWarning(
                "Restored state: {Failed} waiting rows could not be written back this time and keep waiting (tables: {Tables}); they will be tried again.",
                failed,
                string.Join(", ", failedTables));
        }

        return new ReattachResult(attached, stillWaiting, purged, 0, yielded, failed, failedTables.ToList());
    }
}
