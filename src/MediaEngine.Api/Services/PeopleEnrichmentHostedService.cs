using MediaEngine.Domain.Contracts;
using MediaEngine.Providers.Services;
using MediaEngine.Providers.Workers;

namespace MediaEngine.Api.Services;

/// <summary>Optional contributor work has its own lease, timeout and outcome.</summary>
public sealed class PeopleEnrichmentHostedService(
    IServiceScopeFactory scopes,
    EnrichmentPipelineExecutionGate executionGate,
    ILogger<PeopleEnrichmentHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var execution = await executionGate.EnterAsync(stoppingToken);
                using var poll = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, execution.PauseCancellationToken);
                var ct = poll.Token;
                using var scope = scopes.CreateScope();
                var repository = scope.ServiceProvider.GetRequiredService<IMediaOperationRepository>();
                var worker = scope.ServiceProvider.GetRequiredService<PersonEnrichmentWorker>();
                // Recover contributor operations created before their queue status was explicit.
                foreach (var pending in await repository.GetQueueAsync("people", 1000, ct))
                {
                    if (pending.OperationType == "enrichment.people" && (pending.Status == "pending"
                            || (pending.Status is "leased" or "running" && pending.LeaseExpiresAt <= DateTimeOffset.UtcNow)))
                    {
                        await repository.RequeueAsync(pending.Id, ct);
                    }
                }
                var jobs = await repository.LeaseNextAsync(nameof(PeopleEnrichmentHostedService),
                    ["enrichment.people"], 1, TimeSpan.FromMinutes(6), ct);
                foreach (var job in jobs)
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeout.CancelAfter(TimeSpan.FromMinutes(4));
                    try
                    {
                        if (job.EntityId is not { } entityId)
                        {
                            throw new InvalidOperationException("Contributor operation has no entity.");
                        }
                        await worker.EnrichFromClaimsAsync(entityId, timeout.Token);
                        await repository.MarkSucceededAsync(job.Id, "Contributor lookup completed", stoppingToken);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        await repository.MarkInterruptedAsync(job.Id, "Contributor work interrupted; queued for recovery", CancellationToken.None);
                        throw;
                    }
                    catch (Exception ex) when (!ct.IsCancellationRequested)
                    {
                        if (job.AttemptCount >= 3)
                        {
                            await repository.MarkBlockedAsync(job.Id, "Contributor lookup needs attention after repeated failures: " + ex.Message, stoppingToken);
                        }
                        else
                        {
                            await repository.MarkFailedRetryableAsync(job.Id, ex.Message, DateTimeOffset.UtcNow.AddMinutes(5), stoppingToken);
                        }
                    }
                    if (job.BatchId is { } batchId)
                    {
                        await scope.ServiceProvider.GetRequiredService<BatchProgressService>()
                                .EmitProgressAsync(batchId, isFinal: false, stoppingToken);
                    }
                }
                if (jobs.Count > 0)
                {
                    continue;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogWarning(ex, "Contributor queue poll failed"); }
            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
        }
    }
}
