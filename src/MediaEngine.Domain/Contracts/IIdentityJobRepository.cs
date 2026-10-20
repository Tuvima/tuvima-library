using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Jobs;

namespace MediaEngine.Domain.Contracts;

/// <summary>
/// Persistence contract for the <c>identity_jobs</c> table.
///
/// Identity jobs track each media asset through the retail-first pipeline:
/// Stage 1 (retail match), Stage 2 (Wikidata bridge), and Quick hydration.
/// Jobs are durable — they survive engine restarts and replace the former
/// in-memory <c>BoundedChannel</c> queue.
///
/// Workers lease jobs atomically via <see cref="LeaseNextAsync"/> to prevent
/// double-processing. Leases expire after a configurable timeout so stuck
/// jobs are automatically retried.
/// </summary>
public interface IIdentityJobRepository
{
    /// <summary>Creates a new identity job.</summary>
    Task CreateAsync(IdentityJob job, CancellationToken ct = default);

    /// <summary>
    /// Creates a user-resolved job or resumes the existing active job for the
    /// same entity and pass. This prevents a confirmed review item from
    /// remaining parked in a needs-review state behind the active-job guard.
    /// </summary>
    async Task<Guid> CreateOrResumeUserResolutionAsync(IdentityJob job, CancellationToken ct = default)
    {
        await CreateAsync(job, ct);
        return job.Id;
    }

    /// <summary>Returns the identity job for a given entity, if one exists.</summary>
    Task<IdentityJob?> GetByEntityAsync(Guid entityId, CancellationToken ct = default);

    /// <summary>Returns a job by its primary key.</summary>
    Task<IdentityJob?> GetByIdAsync(Guid jobId, CancellationToken ct = default);

    /// <summary>
    /// Atomically leases up to <paramref name="batchSize"/> jobs in the specified
    /// <paramref name="states"/> for the named <paramref name="workerName"/>.
    /// Only jobs with no active lease (or an expired lease) are eligible.
    /// </summary>
    Task<IReadOnlyList<IdentityJob>> LeaseNextAsync(
        string workerName,
        IReadOnlyList<IdentityJobState> states,
        int batchSize,
        TimeSpan leaseDuration,
        CancellationToken ct = default);

    /// <summary>Transitions the job to a new state and clears the lease.</summary>
    Task UpdateStateAsync(Guid jobId, IdentityJobState newState, string? error = null, CancellationToken ct = default);

    /// <summary>Clears the lease, increments the attempt count, and delays the next lease attempt.</summary>
    Task ScheduleRetryAsync(Guid jobId, IdentityJobState retryState, DateTimeOffset nextRetryAt, string error, CancellationToken ct = default)
        => UpdateStateAsync(jobId, retryState, error, ct);

    /// <summary>
    /// Schedules an outcome-aware retry. Only content failures consume the
    /// poison-work budget; dependency and capability failures remain retryable.
    /// </summary>
    Task ScheduleRetryForOutcomeAsync(
        Guid jobId,
        IdentityJobState retryState,
        DateTimeOffset nextRetryAt,
        string error,
        BackgroundJobOutcomeCategory category,
        CancellationToken ct = default)
        => ScheduleRetryAsync(jobId, retryState, nextRetryAt, error, ct);

    /// <summary>Marks a poison job as terminally failed using the existing failed state.</summary>
    Task MarkDeadLetteredAsync(Guid jobId, string error, CancellationToken ct = default)
        => UpdateStateAsync(jobId, IdentityJobState.Failed, error, ct);

    Task MarkDeadLetteredForOutcomeAsync(
        Guid jobId,
        string error,
        BackgroundJobOutcomeCategory category,
        CancellationToken ct = default)
        => MarkDeadLetteredAsync(jobId, error, ct);

    /// <summary>Sets the accepted retail candidate on the job.</summary>
    Task SetSelectedCandidateAsync(Guid jobId, Guid candidateId, CancellationToken ct = default);

    /// <summary>Sets the confirmed Wikidata QID on the job.</summary>
    Task SetResolvedQidAsync(Guid jobId, string qid, CancellationToken ct = default);

    /// <summary>Returns jobs that have been stuck (lease expired, state not terminal) for retry.</summary>
    Task<IReadOnlyList<IdentityJob>> GetStaleAsync(TimeSpan age, int limit, CancellationToken ct = default);

    /// <summary>
    /// Finds jobs stuck in the specified intermediate processing state that
    /// have no active lease and have been in that state for longer than
    /// <paramref name="stuckThreshold"/>.
    /// Resets them to the appropriate "ready" state so the next poll picks
    /// them up. Returns the number of jobs reclaimed.
    /// </summary>
    Task<int> ReclaimStuckJobsAsync(
        IdentityJobState processingState,
        TimeSpan stuckThreshold,
        CancellationToken ct = default);

    /// <summary>
    /// Clears leases left behind by a previous Engine process and resets in-flight
    /// jobs to the state their worker can lease again immediately after startup.
    /// </summary>
    Task<int> RecoverInterruptedJobsAsync(CancellationToken ct = default)
        => Task.FromResult(0);

    /// <summary>Returns jobs in a specific state, ordered by creation time.</summary>
    Task<IReadOnlyList<IdentityJob>> GetByStateAsync(IdentityJobState state, int limit, CancellationToken ct = default);

    /// <summary>Releases the lease on a job without changing state (e.g. on graceful shutdown).</summary>
    Task ReleaseLeaseAsync(Guid jobId, CancellationToken ct = default);

    /// <summary>
    /// Returns the count of identity jobs in non-terminal states (active pipeline work).
    /// Used by DeferredEnrichmentService to yield to Pass 1 ingestion.
    /// </summary>
    Task<int> CountActiveAsync(CancellationToken ct = default);
}
