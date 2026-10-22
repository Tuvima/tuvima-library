namespace MediaEngine.Api.Services.Artwork;

/// <summary>Makes the copies that <see cref="ArtworkRenditionQueue"/> asks for, one picture at a time.</summary>
public sealed class ArtworkRenditionBackgroundService(
    ArtworkRenditionQueue queue,
    IArtworkRenditionService renditions,
    ILogger<ArtworkRenditionBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var assetId in queue.ReadAllAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    await renditions.CreateMissingCopiesAsync(assetId, stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Could not make the missing copies of artwork {AssetId}.", assetId);
                }
                finally
                {
                    queue.MarkDone(assetId);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // The Engine is stopping; queued pictures are asked for again by the next request.
        }
    }
}
