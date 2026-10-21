using MediaEngine.Providers.Services;

namespace MediaEngine.Api.Services;

/// <summary>
/// Background worker for the subtitle text index (Universes U-WP14). It waits until the Engine has settled and until no
/// ingestion is running, then reads waiting subtitle tracks, probes waiting video files and refreshes out-of-date name tags a
/// few at a time. Its to-do list is durable (rows in the data store, see <c>subtitle_index_tracks</c> and
/// <c>subtitle_index_assets</c>), so a restart picks up where it stopped. It never runs on the ingestion path.
/// </summary>
public sealed class SubtitleIndexHostedService : BackgroundService
{
    private const int BatchSize = 10;

    private static readonly TimeSpan InitialDelay = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan BusyDelay = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan MoreWorkDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan IdleDelay = TimeSpan.FromMinutes(15);

    private readonly SubtitleIndexer _indexer;
    private readonly IIngestionQueueProgressService _ingestion;
    private readonly ILogger<SubtitleIndexHostedService> _logger;

    public SubtitleIndexHostedService(
        SubtitleIndexer indexer,
        IIngestionQueueProgressService ingestion,
        ILogger<SubtitleIndexHostedService> logger)
    {
        ArgumentNullException.ThrowIfNull(indexer);
        ArgumentNullException.ThrowIfNull(ingestion);
        ArgumentNullException.ThrowIfNull(logger);
        _indexer = indexer;
        _ingestion = ingestion;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(InitialDelay, stoppingToken).ConfigureAwait(false);
            while (!stoppingToken.IsCancellationRequested)
            {
                var delay = await RunPassAsync(stoppingToken).ConfigureAwait(false);
                await Task.Delay(delay, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _logger.LogDebug("SubtitleIndexHostedService stopped");
        }
    }

    /// <summary>Runs one pass (unless ingestion is busy) and returns how long to wait before the next.</summary>
    internal async Task<TimeSpan> RunPassAsync(CancellationToken ct)
    {
        if (string.Equals(_ingestion.Current.State, "working", StringComparison.OrdinalIgnoreCase))
        {
            return BusyDelay;
        }

        try
        {
            var result = await _indexer.RunOnceAsync(BatchSize, ct).ConfigureAwait(false);
            if (result.DidWork)
            {
                _logger.LogInformation(
                    "Subtitle index: {Indexed} tracks indexed, {Failed} failed, {Probed} files probed, {Retagged} tracks re-tagged",
                    result.TracksIndexed, result.TracksFailed, result.FilesProbed, result.TracksRetagged);
            }

            return result.DidWork ? MoreWorkDelay : IdleDelay;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Subtitle index: a pass failed; the next one runs later");
            return IdleDelay;
        }
    }
}
