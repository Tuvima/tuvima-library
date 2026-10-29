using MediaEngine.Domain.Contracts;

namespace MediaEngine.Api.Services.LocalAssets.Intake;

/// <summary>
/// Runs Tidy this folder jobs (Photo and video filing spec D17). It finishes any run a stop interrupted, runs new ones as they are asked
/// for, and once a night at the configured hour starts a run for every folder the administrator scheduled.
/// </summary>
public sealed class ViewTidyHostedService(
    ViewTidyService tidy,
    IConfigurationLoader configuration,
    TimeProvider time,
    ILogger<ViewTidyHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        DateOnly? lastScheduledDay = null;
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken).ConfigureAwait(false);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var now = time.GetLocalNow();
                    var today = DateOnly.FromDateTime(now.DateTime);
                    if (now.Hour == Math.Clamp(configuration.LoadPhotoFiling().TidyHour, 0, 23) && lastScheduledDay != today)
                    {
                        lastScheduledDay = today;
                        await tidy.StartScheduledAsync(stoppingToken).ConfigureAwait(false);
                    }

                    await tidy.RunPendingAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Tidying a folder failed; it is tried again shortly and resumes where it stopped.");
                }

                await tidy.WaitForRequestAsync(TimeSpan.FromSeconds(60), stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutdown: a run in progress stays marked running and resumes from its ledger at the next start.
        }
    }
}
