using MediaEngine.Api.Services.Activity;
using MediaEngine.Domain.Contracts;

namespace MediaEngine.Api.Services.Playback;

/// <summary>
/// Closes playback sessions that stopped reporting. It never deletes history: playback telemetry and each person's
/// own activity record are kept until a server administrator removes them by hand.
/// </summary>
public sealed class PlaybackTelemetryMaintenanceService(
    IPlaybackTelemetryRepository repository,
    PersonalActivityRecorder activity,
    TimeProvider clock,
    ILogger<PlaybackTelemetryMaintenanceService> logger) : BackgroundService
{
    internal static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(90);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1), clock);
        do
        {
            try
            {
                var now = clock.GetUtcNow();
                var closed = await repository.CloseStaleAsync(now - StaleAfter, stoppingToken).ConfigureAwait(false);
                await activity.RecordClosedAsync(closed, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Playback telemetry maintenance failed and will retry.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }
}
