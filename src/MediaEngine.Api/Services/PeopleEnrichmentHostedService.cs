using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Providers.Helpers;
using MediaEngine.Providers.Services;
using MediaEngine.Providers.Workers;

namespace MediaEngine.Api.Services;

/// <summary>Optional contributor work has its own lease, timeout and outcome.</summary>
public sealed class PeopleEnrichmentHostedService(
    IServiceScopeFactory scopes,
    EnrichmentPipelineExecutionGate executionGate,
    ILogger<PeopleEnrichmentHostedService> logger) : BackgroundService
{
    // How many titles' contributors are looked up side by side.
    internal const int TitlesAtOnce = 3;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var execution = await executionGate.EnterAsync(nameof(PeopleEnrichmentHostedService), stoppingToken);
                using var poll = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, execution.PauseCancellationToken);
                var ct = poll.Token;
                using var scope = scopes.CreateScope();
                var repository = scope.ServiceProvider.GetRequiredService<IMediaOperationRepository>();
                // Recover contributor operations created before their queue status was explicit.
                foreach (var pending in await repository.GetQueueAsync("people", 1000, ct))
                {
                    if (pending.OperationType is MediaOperationType.EnrichmentPeople or MediaOperationType.EnrichmentPeopleRemainder && (pending.Status == "pending"
                            || (pending.Status is "leased" or "running" && pending.LeaseExpiresAt <= DateTimeOffset.UtcNow)))
                    {
                        await repository.RequeueAsync(pending.Id, ct);
                    }
                }
                var jobs = await repository.LeaseNextAsync(nameof(PeopleEnrichmentHostedService),
                    [MediaOperationType.EnrichmentPeople], TitlesAtOnce, TimeSpan.FromMinutes(6), ct);
                if (jobs.Count == 0)
                {
                    // The cast beyond the first billed actors is looked up only when no first-pass contributor work is waiting.
                    jobs = await repository.LeaseNextAsync(nameof(PeopleEnrichmentHostedService),
                        [MediaOperationType.EnrichmentPeopleRemainder], TitlesAtOnce, TimeSpan.FromMinutes(6), ct);
                }
                // Titles are looked up side by side: one title at a time left most of the Wikidata request allowance unused
                // (3 to 5 titles a minute in the synthetic run, #1360). Wikidata's own per-host limit still caps the requests.
                await Task.WhenAll(jobs.Select(job => RunJobAsync(job, ct, stoppingToken)));
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

    private async Task RunJobAsync(MediaOperation job, CancellationToken ct, CancellationToken stoppingToken)
    {
        using var scope = scopes.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IMediaOperationRepository>();
        var worker = scope.ServiceProvider.GetRequiredService<PersonEnrichmentWorker>();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(4));
        try
        {
            if (job.EntityId is not { } entityId)
            {
                throw new InvalidOperationException("Contributor operation has no entity.");
            }
            var remainder = job.OperationType == MediaOperationType.EnrichmentPeopleRemainder;
            await worker.EnrichFromClaimsAsync(entityId, timeout.Token, remainder ? PersonPass.Remainder : PersonPass.FirstBilled);
            // The universe pass links actors to characters only for people who already exist, and it may have run before this
            // lookup finished, so the links are made again now that the people are here.
            if (!string.IsNullOrWhiteSpace(job.SubKey))
            {
                await worker.EnrichActorCharacterMappingsAsync(entityId, job.SubKey, timeout.Token);
            }
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
    }
}
