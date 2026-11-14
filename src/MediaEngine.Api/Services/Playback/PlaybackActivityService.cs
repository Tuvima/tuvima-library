using MediaEngine.Contracts.Playback;
using MediaEngine.Domain.Contracts;

namespace MediaEngine.Api.Services.Playback;

/// <summary>
/// Keeps the Playing now list honest every few seconds: measures how heavy each running encode is, and stops an encode by itself once
/// nobody has been watching for the grace period (playing-now spec D6, WP2 and WP3).
/// </summary>
public sealed class PlaybackActivityService(
    PlaybackActivityRegistry registry,
    IEncodeLoadSampler sampler,
    IConfigurationLoader configuration,
    TimeProvider clock,
    ILogger<PlaybackActivityService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                await TickAsync().ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // The Engine is shutting down.
        }
    }

    /// <summary>One pass: weigh the running encodes, then stop the forgotten ones. Public so tests drive it with a fake clock.</summary>
    public async Task TickAsync()
    {
        try
        {
            var settings = configuration.LoadTranscoding();
            var running = registry.Encodes.Where(e => !e.WaitingForSlot).ToArray();
            var share = sampler.Sample();
            if (share is { } total && running.Length > 0)
            {
                var each = EncodeHeaviness.Classify(total / running.Length);
                foreach (var encode in running)
                {
                    encode.Heaviness = each;
                }
            }

            var due = registry.Evaluate(
                clock.GetUtcNow(),
                TimeSpan.FromSeconds(Math.Max(1, settings.GraceSecondsAfterLastViewer)),
                TimeSpan.FromMinutes(Math.Max(1, settings.PausedCountsAsWatchingMinutes)));
            foreach (var encode in due)
            {
                logger.LogInformation("Stopping an encode nobody has watched for {Seconds} seconds (asset {AssetId})",
                    settings.GraceSecondsAfterLastViewer, encode.AssetId);
                await registry.StopEntryAsync(encode).ConfigureAwait(false);
            }

            registry.RaiseChanged();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Playing now could not be refreshed");
        }
    }
}
