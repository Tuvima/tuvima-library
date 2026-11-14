using MediaEngine.Domain.Contracts;

namespace MediaEngine.Api.Services;

/// <summary>
/// Completes activity runs once the Engine has been idle for five quiet minutes (spec library-activity D21, D30): a run stays open while the
/// Engine is busy with the import, however long its titles take. Runs left open by a stopped Engine are completed and marked as cut off on the
/// first pass, so History never shows a run as Active that nothing is working on, and the restart can continue them. Never throws.
/// </summary>
public sealed class ActivityRunSweeper(IActivityRunRecorder recorder, TimeProvider time, ILogger<ActivityRunSweeper> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();

        // On start nothing is being worked on yet: every run left open by the last session is finished, and marked so that
        // work picked up again just after the restart continues it (D30).
        try
        {
            await recorder.CloseLeftOpenRunsAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Closing the activity runs the last session left open failed; the quiet-minutes sweep will close them.");
        }

        using var timer = new PeriodicTimer(Interval, time);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                await SweepAsync(ActivityRunRecorder.QuietWindow, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // The Engine is stopping.
        }
    }

    private async Task SweepAsync(TimeSpan quiet, CancellationToken ct)
    {
        try
        {
            await recorder.CloseQuietRunsAsync(quiet, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Closing quiet activity runs failed; it will be tried again in a minute.");
        }
    }
}
