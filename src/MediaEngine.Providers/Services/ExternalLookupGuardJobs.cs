using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Providers.Services;

/// <summary>What an identity worker does with a leased job whose file sits in a library that never looks anything up.</summary>
public static class ExternalLookupGuardJobs
{
    /// <summary>
    /// True (and logged) when the entity's file sits in a library that never looks anything up, so the worker must stop before it
    /// asks a provider. With no guard nothing is blocked.
    /// </summary>
    public static async Task<bool> RefusesAsync(this IExternalLookupGuard? guard, Guid entityId, ILogger logger, string work, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(logger);
        if (guard is null || !await guard.BlocksEntityAsync(entityId, ct).ConfigureAwait(false))
        {
            return false;
        }

        logger.LogInformation("{Work} for {EntityId} was skipped: its library never looks anything up.", work, entityId);
        return true;
    }

    /// <summary>
    /// Settles every leased job whose file sits in a library that blocks outside lookup (it is closed as Ready without Universe, with
    /// nothing asked of any provider) and returns the jobs that may go on. Such a job should never exist, since ingestion does not
    /// queue one; this is the explicit check, so the promise does not rest on that alone. With no guard every job goes on.
    /// </summary>
    public static async Task<IReadOnlyList<IdentityJob>> SettleBlockedJobsAsync(
        this IExternalLookupGuard? guard,
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
            if (await guard.BlocksEntityAsync(job.EntityId, ct).ConfigureAwait(false))
            {
                logger.LogInformation(
                    "Identity job {JobId} for {EntityId} belongs to a library that never looks anything up; no outside provider was asked.",
                    job.Id,
                    job.EntityId);
                await jobRepo.UpdateStateAsync(job.Id, IdentityJobState.ReadyWithoutUniverse, ct: ct).ConfigureAwait(false);
                continue;
            }

            allowed.Add(job);
        }

        return allowed;
    }
}
