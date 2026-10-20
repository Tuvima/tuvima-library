using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Providers.Contracts;
using MediaEngine.Providers.Services;
using MediaEngine.Providers.Workers;

namespace MediaEngine.Providers.Tests;

public sealed class IdentityPipelineSignalTests
{
    [Fact]
    public async Task WaitAsync_CompletesWhenSignalArrivesBeforeFallbackDelay()
    {
        var signal = new IdentityPipelineSignal();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        var waitTask = signal.WaitAsync(
            IdentityPipelineSignalKind.Retail,
            TimeSpan.FromMinutes(1),
            cts.Token);

        signal.Signal(IdentityPipelineSignalKind.Retail);

        await waitTask;
        Assert.True(waitTask.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task RetryPolicy_DeadLettersAfterConfiguredMaxAttempts()
    {
        var repo = new RecordingIdentityJobRepository();
        var job = new IdentityJob { AttemptCount = 1, PoisonAttemptCount = 1 };

        await IdentityJobRetryPolicy.ScheduleRetryOrDeadLetterAsync(
            repo,
            job,
            IdentityJobState.Queued,
            new InvalidDataException("malformed content"),
            new HydrationSettings { IdentityRetryMaxAttempts = 2 },
            CancellationToken.None);

        Assert.True(repo.DeadLettered);
        Assert.False(repo.RetryScheduled);
    }

    [Fact]
    public async Task RetryPolicy_TimeoutDoesNotConsumePoisonBudgetOrDeadLetter()
    {
        var repo = new RecordingIdentityJobRepository();
        var job = new IdentityJob { AttemptCount = 20, PoisonAttemptCount = 20 };

        await IdentityJobRetryPolicy.ScheduleRetryOrDeadLetterAsync(
            repo,
            job,
            IdentityJobState.Queued,
            new TimeoutException("dependency offline"),
            new HydrationSettings { IdentityRetryMaxAttempts = 2 },
            CancellationToken.None);

        Assert.True(repo.RetryScheduled);
        Assert.False(repo.DeadLettered);
    }

    [Fact]
    public async Task RetryPolicy_UsesConfiguredBackoffDelay()
    {
        var repo = new RecordingIdentityJobRepository();
        var job = new IdentityJob { AttemptCount = 0 };
        var before = DateTimeOffset.UtcNow;

        await IdentityJobRetryPolicy.ScheduleRetryOrDeadLetterAsync(
            repo,
            job,
            IdentityJobState.Queued,
            new TimeoutException("temporary"),
            new HydrationSettings
            {
                IdentityRetryMaxAttempts = 5,
                IdentityRetryBaseDelaySeconds = 1,
                IdentityRetryMaxDelaySeconds = 2,
                IdentityRetryJitterMinMilliseconds = 0,
                IdentityRetryJitterMaxMilliseconds = 1,
            },
            CancellationToken.None);

        Assert.True(repo.RetryScheduled);
        Assert.False(repo.DeadLettered);
        Assert.Equal(IdentityJobState.Queued, repo.RetryState);
        Assert.NotNull(repo.NextRetryAt);
        Assert.InRange(repo.NextRetryAt!.Value - before, TimeSpan.FromMilliseconds(1500), TimeSpan.FromMilliseconds(2500));
    }

    private sealed class RecordingIdentityJobRepository : IIdentityJobRepository
    {
        public bool RetryScheduled { get; private set; }
        public bool DeadLettered { get; private set; }
        public IdentityJobState? RetryState { get; private set; }
        public DateTimeOffset? NextRetryAt { get; private set; }

        public Task CreateAsync(IdentityJob job, CancellationToken ct = default) => Task.CompletedTask;

        public Task<IdentityJob?> GetByEntityAsync(Guid entityId, CancellationToken ct = default) =>
            Task.FromResult<IdentityJob?>(null);

        public Task<IdentityJob?> GetByIdAsync(Guid jobId, CancellationToken ct = default) =>
            Task.FromResult<IdentityJob?>(null);

        public Task<IReadOnlyList<IdentityJob>> LeaseNextAsync(
            string workerName,
            IReadOnlyList<IdentityJobState> states,
            int batchSize,
            TimeSpan leaseDuration,
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<IdentityJob>>([]);

        public Task UpdateStateAsync(
            Guid jobId,
            IdentityJobState newState,
            string? error = null,
            CancellationToken ct = default) => Task.CompletedTask;

        public Task ScheduleRetryAsync(
            Guid jobId,
            IdentityJobState retryState,
            DateTimeOffset nextRetryAt,
            string error,
            CancellationToken ct = default)
        {
            RetryScheduled = true;
            RetryState = retryState;
            NextRetryAt = nextRetryAt;
            return Task.CompletedTask;
        }

        public Task MarkDeadLetteredAsync(Guid jobId, string error, CancellationToken ct = default)
        {
            DeadLettered = true;
            return Task.CompletedTask;
        }

        public Task SetSelectedCandidateAsync(Guid jobId, Guid candidateId, CancellationToken ct = default) => Task.CompletedTask;

        public Task SetResolvedQidAsync(Guid jobId, string qid, CancellationToken ct = default) => Task.CompletedTask;

        public Task<IReadOnlyList<IdentityJob>> GetStaleAsync(TimeSpan age, int limit, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<IdentityJob>>([]);

        public Task<int> ReclaimStuckJobsAsync(
            IdentityJobState processingState,
            TimeSpan stuckThreshold,
            CancellationToken ct = default) =>
            Task.FromResult(0);

        public Task<IReadOnlyList<IdentityJob>> GetByStateAsync(IdentityJobState state, int limit, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<IdentityJob>>([]);

        public Task ReleaseLeaseAsync(Guid jobId, CancellationToken ct = default) => Task.CompletedTask;

        public Task<int> CountActiveAsync(CancellationToken ct = default) => Task.FromResult(0);
    }
}
