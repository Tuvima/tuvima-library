using MediaEngine.Domain.Contracts;

namespace MediaEngine.Api.Services;

/// <summary>
/// Completes activity runs once nothing new has joined for five quiet minutes (spec library-activity D21). Runs left open by a stopped
/// Engine are completed on the first pass, so History never shows a run as Active that nothing is working on. Never throws.
/// </summary>
public sealed class ActivityRunSweeper(IActivityRunRecorder recorder, TimeProvider time, ILogger<ActivityRunSweeper> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();

        // On start nothing is being worked on yet: every run left open by the last session is finished.
        await SweepAsync(TimeSpan.Zero, stoppingToken).ConfigureAwait(false);

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
