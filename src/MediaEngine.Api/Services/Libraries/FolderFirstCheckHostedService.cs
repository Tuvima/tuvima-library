using MediaEngine.Storage;

namespace MediaEngine.Api.Services.Libraries;

/// <summary>
/// Decides the first check of each newly added folder (Library setup spec D35, D61) as soon as its files have their outcomes: it wakes when a
/// file's outcome is counted for a folder that is being checked, and looks again every few seconds so a folder with fewer files than the
/// check needs is decided once its ingestion has settled. When no folder is being checked each look is one small indexed query.
/// </summary>
public sealed class FolderFirstCheckHostedService(
    FolderFirstCheckService service,
    FolderFirstCheckSignal signal,
    ILogger<FolderFirstCheckHostedService> logger) : BackgroundService
{
    private static readonly TimeSpan LookEvery = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await service.EvaluateAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "FolderFirstCheckHostedService: a look at the folders being checked failed; the next look tries again");
                }

                await signal.WaitAsync(LookEvery, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // The Engine is stopping.
        }
    }
}
