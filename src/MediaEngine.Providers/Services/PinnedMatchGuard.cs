using MediaEngine.Domain;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Providers.Services;

/// <summary>Reads the <see cref="MetadataFieldConstants.MatchPinned"/> value the editor writes when a person applies a match.</summary>
public sealed class PinnedMatchGuard(ICanonicalValueRepository canonicals, ILogger<PinnedMatchGuard> logger) : IPinnedMatchGuard
{
    public async Task<bool> IsPinnedAsync(Guid entityId, CancellationToken ct = default)
    {
        if (entityId == Guid.Empty)
        {
            return false;
        }

        try
        {
            var values = await canonicals.GetByEntityAsync(entityId, ct).ConfigureAwait(false);
            return values.Any(value =>
                string.Equals(value.Key, MetadataFieldConstants.MatchPinned, StringComparison.OrdinalIgnoreCase)
                && string.Equals(value.Value, "true", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A pin that cannot be read must not stop identification for good, but the reason is kept.
            logger.LogWarning(ex, "Could not read whether the match of {EntityId} is pinned; treating it as not pinned.", entityId);
            return false;
        }
    }
}

/// <summary>What an identity worker does with a leased job for an item whose match a person pinned.</summary>
public static class PinnedMatchGuardJobs
{
    /// <summary>
    /// Settles every leased automatic job whose item carries a match a person applied: its retail stage is skipped (nothing is asked of a
    /// retail provider) and the job moves on as <see cref="IdentityJobState.RetailMatched"/>, so the Wikidata bridge, hydration and enrichment
    /// still run for the matched record. Returns the jobs that still need the retail stage. A user-resolution job never reaches the queue as
    /// Queued, so a Queued job is always an automatic one. With no guard every job goes on.
    /// </summary>
    public static async Task<IReadOnlyList<IdentityJob>> SettlePinnedJobsAsync(
        this IPinnedMatchGuard? guard,
        IIdentityJobRepository jobRepo,
        IReadOnlyList<IdentityJob> jobs,
        ILogger logger,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(jobRepo);
        ArgumentNullException.ThrowIfNull(jobs);
        ArgumentNullException.ThrowIfNull(logger);
        if (guard is null || jobs.Count == 0)
        {
            return jobs;
        }

        var allowed = new List<IdentityJob>(jobs.Count);
        foreach (var job in jobs)
        {
            if (await guard.IsPinnedAsync(job.EntityId, ct).ConfigureAwait(false))
            {
                logger.LogInformation(
                    "Identity job {JobId} for {EntityId} skips the retail stage: a person applied this match, so a scan does not re-identify it.",
                    job.Id,
                    job.EntityId);
                await jobRepo.UpdateStateAsync(job.Id, IdentityJobState.RetailMatched, ct: ct).ConfigureAwait(false);
                continue;
            }

            allowed.Add(job);
        }

        return allowed;
    }
}
