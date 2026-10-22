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
/// </summary>
public sealed class WaitingStateReattachHostedService : BackgroundService
{
    internal const int BatchSize = 200;
    internal const int MaxBatchesPerRun = 50;

    internal static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(45);
    internal static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(1);
    internal static readonly TimeSpan PurgeInterval = TimeSpan.FromDays(1);

    private readonly WaitingPersonalStateRepository _repository;
    private readonly IIngestionQueueProgressService _ingestion;
    private readonly Func<int> _retentionDays;
    private readonly ILogger<WaitingStateReattachHostedService> _logger;

    public WaitingStateReattachHostedService(
        WaitingPersonalStateRepository repository,
        IIngestionQueueProgressService ingestion,
        IConfigurationLoader configuration,
        ILogger<WaitingStateReattachHostedService> logger)
        : this(repository, ingestion, () => configuration.LoadMaintenance().MissingFileRetentionDays, logger)
    {
    }

    /// <summary>A test seam: <paramref name="retentionDays"/> supplies the waiting period each run.</summary>
    internal WaitingStateReattachHostedService(
        WaitingPersonalStateRepository repository,
        IIngestionQueueProgressService ingestion,
        Func<int> retentionDays,
        ILogger<WaitingStateReattachHostedService> logger)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(ingestion);
        ArgumentNullException.ThrowIfNull(retentionDays);
        ArgumentNullException.ThrowIfNull(logger);
        _repository = repository;
        _ingestion = ingestion;
        _retentionDays = retentionDays;
        _logger = logger;
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
                    await RunOnceSafelyAsync(now, stoppingToken).ConfigureAwait(false);
                    lastRun = now;
                    due = false;
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
    /// Runs the passes needed to look at every waiting row once, when anything is waiting. Logs one line when a pass re-attached or
    /// cleared something. Returns null when nothing was waiting.
    /// </summary>
    internal ReattachResult? RunOnce(DateTimeOffset now, CancellationToken ct)
    {
        if (_repository.CountWaiting(includeWithheld: true) == 0)
        {
            return null;
        }

        // Read every run, and kept inside the limits the settings allow, so a bad value can never clear everything at once.
        var retentionDays = Math.Clamp(_retentionDays(), MissingFileRetentionLimits.MinDays, MissingFileRetentionLimits.MaxDays);
        int attached = 0, purged = 0, stillWaiting = 0;
        for (var batch = 0; batch < MaxBatchesPerRun; batch++)
        {
            var result = _repository.RunPass(now, BatchSize, retentionDays, ct);
            attached += result.Attached;
            purged += result.Purged;
            stillWaiting = result.StillWaiting;
            if (result.Checked < BatchSize)
            {
                break;
            }
        }

        if (attached > 0 || purged > 0)
        {
            _logger.LogInformation(
                "Restored state: {Attached} re-attached, {StillWaiting} still waiting, {Purged} cleared after the waiting period",
                attached,
                stillWaiting,
                purged);
        }

        return new ReattachResult(attached, stillWaiting, purged);
    }
}
