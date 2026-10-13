using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;

namespace MediaEngine.Api.Services.LocalAssets;

/// <summary>
/// Reads document text from the durable media-operation queue, a few files at a time, so the
/// folder scan never waits on PDF or Office parsing. Queued work survives an Engine restart.
/// </summary>
public sealed class DocumentTextReadOperationWorker(
    IMediaOperationRepository operations,
    DocumentTextReadService reader,
    ILogger<DocumentTextReadOperationWorker> logger) : BackgroundService
{
    private static readonly string[] OperationTypes = [MediaOperationType.ViewDocumentTextRead];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (await RunBatchAsync(stoppingToken).ConfigureAwait(false) == 0)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Document text worker failed; queued work will be retried.");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Leases and processes one batch. Returns how many operations were handled.</summary>
    public async Task<int> RunBatchAsync(CancellationToken ct)
    {
        var leased = await operations.LeaseNextAsync(
            Environment.MachineName + ":document-text",
            OperationTypes,
            batchSize: 4,
            leaseDuration: TimeSpan.FromMinutes(2),
            ct).ConfigureAwait(false);
        foreach (var operation in leased)
        {
            await ProcessAsync(operation, ct).ConfigureAwait(false);
        }

        return leased.Count;
    }

    private async Task ProcessAsync(MediaOperation operation, CancellationToken ct)
    {
        if (operation.EntityId is not { } itemId)
        {
            await operations.MarkFailedTerminalAsync(operation.Id, "The queued operation has no document target.", ct).ConfigureAwait(false);
            return;
        }

        try
        {
            await operations.UpdateStageAsync(operation.Id, MediaOperationStage.Parsing, 10, ct).ConfigureAwait(false);
            var outcome = await reader.ReadItemAsync(itemId, ct).ConfigureAwait(false);
            switch (outcome.Status)
            {
                case DocumentTextReadStatus.Read:
                    await operations.MarkSucceededAsync(operation.Id, outcome.Summary, ct).ConfigureAwait(false);
                    break;
                case DocumentTextReadStatus.NoText:
                    await operations.MarkNoResultAsync(operation.Id, "no_text", outcome.Summary, ct).ConfigureAwait(false);
                    break;
                default:
                    await operations.MarkFailedTerminalAsync(operation.Id, outcome.Summary, ct).ConfigureAwait(false);
                    break;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // One bad document must not stop the rest of the batch; the job is retried later.
            logger.LogWarning(ex, "Document text read {OperationId} failed for item {ItemId}.", operation.Id, itemId);
            await operations.MarkFailedRetryableAsync(operation.Id, ex.GetType().Name, DateTimeOffset.UtcNow.AddMinutes(5), ct)
                .ConfigureAwait(false);
        }
    }
}
