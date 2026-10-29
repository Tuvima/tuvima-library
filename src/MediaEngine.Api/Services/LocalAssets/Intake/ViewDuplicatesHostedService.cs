using MediaEngine.Domain.Contracts;

namespace MediaEngine.Api.Services.LocalAssets.Intake;

/// <summary>Runs the duplicates cleanup once a day at the configured hour (Photo and video filing spec D16). It only writes the report.</summary>
public sealed class ViewDuplicatesHostedService(
    ViewDuplicatesService duplicates,
    IConfigurationLoader configuration,
    TimeProvider time,
    ILogger<ViewDuplicatesHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var settings = configuration.LoadPhotoFiling();
                var now = time.GetLocalNow();
                var next = new DateTimeOffset(now.Year, now.Month, now.Day, Math.Clamp(settings.DuplicateScanHour, 0, 23), 0, 0, now.Offset);
                if (next <= now)
                {
                    next = next.AddDays(1);
                }

                await Task.Delay(next - now, stoppingToken).ConfigureAwait(false);
                if (!configuration.LoadPhotoFiling().DuplicateScanEnabled)
                {
                    continue;
                }

                try
                {
                    await duplicates.ScanAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "The duplicates cleanup failed; it runs again tomorrow.");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutdown.
        }
    }
}
