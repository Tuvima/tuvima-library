using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;

namespace MediaEngine.Api.Services.LocalAssets.Filing;

/// <summary>
/// Applies auto-filing rules and works out Inbox suggestions from the durable media-operation queue, a few documents
/// at a time, so reading a document never waits on it. Queued work survives an Engine restart.
/// </summary>
public sealed class DocumentApplyRulesOperationWorker(
    IMediaOperationRepository operations,
    DocumentFilingService filing,
    ILogger<DocumentApplyRulesOperationWorker> logger) : BackgroundService
{
    private static readonly string[] OperationTypes = [MediaOperationType.ViewDocumentApplyRules];

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
                logger.LogError(ex, "Auto-filing worker failed; queued work will be retried.");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Leases and processes one batch. Returns how many operations were handled.</summary>
    public async Task<int> RunBatchAsync(CancellationToken ct)
    {
        var leased = await operations.LeaseNextAsync(
            Environment.MachineName + ":document-rules",
            OperationTypes,
            batchSize: 8,
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
            await operations.UpdateStageAsync(operation.Id, MediaOperationStage.Analyzing, 10, ct).ConfigureAwait(false);
            var outcome = await filing.ProcessAsync(itemId, ct).ConfigureAwait(false);
            if (outcome.Status == DocumentFilingStatus.Applied)
            {
                await operations.MarkSucceededAsync(operation.Id, outcome.Summary, ct).ConfigureAwait(false);
            }
            else
            {
                await operations.MarkMissingConfirmedAsync(operation.Id, outcome.Summary, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // One bad document must not stop the rest of the batch; the job is retried later.
            logger.LogWarning(ex, "Auto-filing {OperationId} failed for item {ItemId}.", operation.Id, itemId);
            await operations.MarkFailedRetryableAsync(operation.Id, ex.GetType().Name, DateTimeOffset.UtcNow.AddMinutes(5), ct)
                .ConfigureAwait(false);
        }
    }
}
