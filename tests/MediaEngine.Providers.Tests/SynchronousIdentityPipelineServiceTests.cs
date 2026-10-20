using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Models;
using MediaEngine.Providers.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Providers.Tests;

public sealed class SynchronousIdentityPipelineServiceTests
{
    [Fact]
    public async Task EnqueueAsync_SkipRetailStage_CreatesWorkerVisibleRetailMatchedJob()
    {
        var jobs = new CapturingIdentityJobRepository();
        var service = new SynchronousIdentityPipelineService(
            jobs,
            canonicalRepo: null!,
            retailWorker: null!,
            bridgeWorker: null!,
            hydrationWorker: null!,
            NullLogger<SynchronousIdentityPipelineService>.Instance);

        var entityId = Guid.NewGuid();

        await service.EnqueueAsync(new HarvestRequest
        {
            EntityId = entityId,
            EntityType = EntityType.MediaAsset,
            MediaType = MediaType.Books,
            SkipRetailStage = true,
            IsUserResolution = true,
        });

        var job = Assert.Single(jobs.Created);
        Assert.Equal(entityId, job.EntityId);
        Assert.Equal(nameof(IdentityJobState.RetailMatched), job.State);
    }

    [Fact]
    public async Task EnqueueAsync_UserPreResolvedQid_CreatesHydrationReadyJob()
    {
        var jobs = new CapturingIdentityJobRepository();
        var service = new SynchronousIdentityPipelineService(
            jobs,
            canonicalRepo: null!,
            retailWorker: null!,
            bridgeWorker: null!,
            hydrationWorker: null!,
            NullLogger<SynchronousIdentityPipelineService>.Instance);

        await service.EnqueueAsync(new HarvestRequest
        {
            EntityId = Guid.NewGuid(),
            EntityType = EntityType.MediaAsset,
            MediaType = MediaType.Books,
            PreResolvedQid = "Q123",
            IsUserResolution = true,
        });

        var job = Assert.Single(jobs.Created);
        Assert.Equal(nameof(IdentityJobState.QidResolved), job.State);
        Assert.Equal("Q123", job.ResolvedQid);
        Assert.Equal((job.Id, "Q123"), Assert.Single(jobs.ResolvedQidUpdates));
    }

    private sealed class CapturingIdentityJobRepository : IIdentityJobRepository
    {
        public List<IdentityJob> Created { get; } = [];
        public List<(Guid JobId, string Qid)> ResolvedQidUpdates { get; } = [];

        public Task CreateAsync(IdentityJob job, CancellationToken ct = default)
        {
            Created.Add(job);
            return Task.CompletedTask;
        }

        public Task SetResolvedQidAsync(Guid jobId, string qid, CancellationToken ct = default)
        {
            ResolvedQidUpdates.Add((jobId, qid));
            var job = Created.FirstOrDefault(j => j.Id == jobId);
            if (job is not null)
            {
                job.ResolvedQid = qid;
            }

            return Task.CompletedTask;
        }

        public Task<IdentityJob?> GetByEntityAsync(Guid entityId, CancellationToken ct = default) =>
            Task.FromResult(Created.FirstOrDefault(j => j.EntityId == entityId));

        public Task<IdentityJob?> GetByIdAsync(Guid jobId, CancellationToken ct = default) =>
            Task.FromResult(Created.FirstOrDefault(j => j.Id == jobId));

        public Task<IReadOnlyList<IdentityJob>> LeaseNextAsync(
            string workerName,
            IReadOnlyList<IdentityJobState> states,
            int batchSize,
            TimeSpan leaseDuration,
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<IdentityJob>>([]);

        public Task UpdateStateAsync(Guid jobId, IdentityJobState newState, string? error = null, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task ScheduleRetryAsync(Guid jobId, IdentityJobState retryState, DateTimeOffset nextRetryAt, string error, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task MarkDeadLetteredAsync(Guid jobId, string error, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task SetSelectedCandidateAsync(Guid jobId, Guid candidateId, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<IdentityJob>> GetStaleAsync(TimeSpan age, int limit, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<IdentityJob>>([]);

        public Task<int> ReclaimStuckJobsAsync(
            IdentityJobState processingState,
            TimeSpan stuckThreshold,
            CancellationToken ct = default) =>
            Task.FromResult(0);

        public Task<IReadOnlyList<IdentityJob>> GetByStateAsync(IdentityJobState state, int limit, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<IdentityJob>>([]);

        public Task ReleaseLeaseAsync(Guid jobId, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<int> CountActiveAsync(CancellationToken ct = default) =>
            Task.FromResult(0);
    }
}
