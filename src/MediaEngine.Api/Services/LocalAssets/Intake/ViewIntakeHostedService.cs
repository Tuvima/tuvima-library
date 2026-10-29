using MediaEngine.Domain.Contracts;

namespace MediaEngine.Api.Services.LocalAssets.Intake;

/// <summary>
/// Looks at the Import and Rejected areas on a short timer (Photo and video filing spec D1, D9). On start it finishes or puts back any
/// file a stop left half filed, and from then on files whatever has settled. <see cref="RequestPass"/> makes the next look happen now.
/// </summary>
public sealed class ViewIntakeHostedService(
    ViewIntakeFiler filer,
    IConfigurationLoader configuration,
    ILogger<ViewIntakeHostedService> logger) : BackgroundService
{
    private readonly SemaphoreSlim _wake = new(0, 1);

    /// <summary>Asks for a pass now instead of waiting for the timer (after Scan now or Add files).</summary>
    public void RequestPass()
    {
        try
        {
            _wake.Release();
        }
        catch (SemaphoreFullException)
        {
            // A pass is already asked for.
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);
            try
            {
                await filer.RecoverAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Finishing the files left half filed failed; the next pass tries again.");
            }

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await filer.FilePendingAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Filing the photos and videos in the Import area failed; the next pass tries again.");
                }

                var seconds = Math.Clamp(configuration.LoadPhotoFiling().ScanIntervalSeconds, 5, 3600);
                await _wake.WaitAsync(TimeSpan.FromSeconds(seconds), stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutdown.
        }
    }
}
