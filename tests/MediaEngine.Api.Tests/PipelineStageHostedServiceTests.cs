using System.Reflection;
using MediaEngine.Api.Services;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Providers.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Api.Tests;

public sealed class PipelineStageHostedServiceTests
{
    [Fact]
    public async Task ExecuteAsync_ReclaimsPollsSignalsAndKeepsScopeAliveThroughWait()
    {
        using var stopping = new CancellationTokenSource();
        var repository = new StubIdentityJobRepository(reclaimedCount: 2);
        var scopeState = new ScopeState();
        var signal = new RecordingSignal(stopping, scopeState);
        using var provider = BuildProvider(repository, scopeState, processedCount: 3);
        var service = new TestPipelineStageHostedService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            signal,
            new EnrichmentPipelineExecutionGate());

        await service.StartAsync(stopping.Token);
        await signal.WaitObserved.WaitAsync(TimeSpan.FromSeconds(5));
        await service.StopAsync(CancellationToken.None);

        Assert.Equal(1, service.StartedCount);
        Assert.Equal(IdentityJobState.RetailSearching, repository.LastProcessingState);
        Assert.Equal(TimeSpan.FromMinutes(7), repository.LastStuckThreshold);
        Assert.Equal(1, scopeState.PollCount);
        Assert.Equal([IdentityPipelineSignalKind.Hydration], signal.Signals);
        var wait = Assert.Single(signal.Waits);
        Assert.Equal(IdentityPipelineSignalKind.Retail, wait.Kind);
        Assert.Equal(TimeSpan.FromSeconds(5), wait.Delay);
        Assert.True(signal.ScopeWasAliveDuringWait);
        Assert.True(scopeState.Disposed);
        Assert.Equal(0, service.PollErrorCount);
    }

    [Fact]
    public async Task ExecuteAsync_WhenPollFails_LogsAndUsesIdleBackoff()
    {
        using var stopping = new CancellationTokenSource();
        var repository = new StubIdentityJobRepository(reclaimedCount: 0);
        var scopeState = new ScopeState { PollException = new InvalidOperationException("poll failed") };
        var signal = new RecordingSignal(stopping, scopeState);
        using var provider = BuildProvider(repository, scopeState, processedCount: 0);
        var service = new TestPipelineStageHostedService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            signal,
            new EnrichmentPipelineExecutionGate());

        await service.StartAsync(stopping.Token);
        await signal.WaitObserved.WaitAsync(TimeSpan.FromSeconds(5));
        await service.StopAsync(CancellationToken.None);

        Assert.Equal(1, service.PollErrorCount);
        Assert.Empty(signal.Signals);
        var wait = Assert.Single(signal.Waits);
        Assert.Equal(IdentityPipelineSignalKind.Retail, wait.Kind);
        Assert.Equal(TimeSpan.FromSeconds(30), wait.Delay);
    }

    [Fact]
    public void ConcreteStages_PreserveTheirSignalsAndReclaimThresholds()
    {
        using var provider = new ServiceCollection().BuildServiceProvider();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();
        var signal = new PassiveSignal();
        var executionGate = new EnrichmentPipelineExecutionGate();

        AssertStageConfiguration(
            new RetailMatchHostedService(
                scopeFactory,
                signal,
                executionGate,
                NullLogger<RetailMatchHostedService>.Instance),
            IdentityPipelineSignalKind.Retail,
            TimeSpan.FromMinutes(5),
            IdentityJobState.RetailSearching,
            IdentityPipelineSignalKind.WikidataBridge);
        AssertStageConfiguration(
            new WikidataBridgeHostedService(
                scopeFactory,
                signal,
                executionGate,
                NullLogger<WikidataBridgeHostedService>.Instance),
            IdentityPipelineSignalKind.WikidataBridge,
            TimeSpan.FromMinutes(10),
            IdentityJobState.BridgeSearching,
            IdentityPipelineSignalKind.Hydration);
        AssertStageConfiguration(
            new QuickHydrationHostedService(
                scopeFactory,
                signal,
                executionGate,
                NullLogger<QuickHydrationHostedService>.Instance),
            IdentityPipelineSignalKind.Hydration,
            TimeSpan.FromMinutes(5),
            IdentityJobState.Hydrating,
            downstreamSignal: null);
    }

    private static ServiceProvider BuildProvider(
        IIdentityJobRepository repository,
        ScopeState scopeState,
        int processedCount)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => repository);
        services.AddScoped(_ => scopeState);
        services.AddScoped(provider => new TestWorker(
            provider.GetRequiredService<ScopeState>(),
            processedCount));
        return services.BuildServiceProvider();
    }

    private static void AssertStageConfiguration(
        object service,
        IdentityPipelineSignalKind wakeSignal,
        TimeSpan stuckJobThreshold,
        IdentityJobState processingState,
        IdentityPipelineSignalKind? downstreamSignal)
    {
        const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var type = service.GetType();

        Assert.Equal(
            wakeSignal,
            type.GetProperty("WakeSignal", Flags)!.GetValue(service));
        Assert.Equal(
            stuckJobThreshold,
            type.GetProperty("StuckJobThreshold", Flags)!.GetValue(service));
        Assert.Equal(
            processingState,
            type.GetProperty("ProcessingState", Flags)!.GetValue(service));
        Assert.Equal(
            downstreamSignal,
            type.GetProperty("DownstreamSignal", Flags)!.GetValue(service));
    }

    private sealed class TestPipelineStageHostedService
        : PipelineStageHostedService<TestWorker>
    {
        public TestPipelineStageHostedService(
            IServiceScopeFactory scopeFactory,
            IIdentityPipelineSignal signal,
            EnrichmentPipelineExecutionGate executionGate)
            : base(
                scopeFactory,
                signal,
                executionGate,
                NullLogger<TestPipelineStageHostedService>.Instance)
        {
        }

        public int StartedCount { get; private set; }

        public int PollErrorCount { get; private set; }

        protected override IdentityPipelineSignalKind WakeSignal =>
            IdentityPipelineSignalKind.Retail;

        protected override TimeSpan StuckJobThreshold => TimeSpan.FromMinutes(7);

        protected override IdentityJobState ProcessingState => IdentityJobState.RetailSearching;

        protected override IdentityPipelineSignalKind? DownstreamSignal =>
            IdentityPipelineSignalKind.Hydration;

        protected override Task<int> PollAsync(
            TestWorker worker,
            CancellationToken stoppingToken) =>
            worker.PollAsync(stoppingToken);

        protected override void LogStarted() => StartedCount++;

        protected override void LogPollError(Exception exception) => PollErrorCount++;
    }

    private sealed class TestWorker
    {
        private readonly ScopeState _scopeState;
        private readonly int _processedCount;

        public TestWorker(ScopeState scopeState, int processedCount)
        {
            _scopeState = scopeState;
            _processedCount = processedCount;
        }

        public Task<int> PollAsync(CancellationToken stoppingToken)
        {
            stoppingToken.ThrowIfCancellationRequested();
            _scopeState.PollCount++;
            return _scopeState.PollException is { } exception
                ? Task.FromException<int>(exception)
                : Task.FromResult(_processedCount);
        }
    }

    private sealed class ScopeState : IDisposable
    {
        public int PollCount { get; set; }

        public Exception? PollException { get; set; }

        public bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;
    }

    private sealed class RecordingSignal(
        CancellationTokenSource stopping,
        ScopeState scopeState) : IIdentityPipelineSignal
    {
        private readonly TaskCompletionSource _waitObserved =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<IdentityPipelineSignalKind> Signals { get; } = [];

        public List<(IdentityPipelineSignalKind Kind, TimeSpan Delay)> Waits { get; } = [];

        public bool ScopeWasAliveDuringWait { get; private set; }

        public Task WaitObserved => _waitObserved.Task;

        public void Signal(IdentityPipelineSignalKind kind) => Signals.Add(kind);

        public Task WaitAsync(
            IdentityPipelineSignalKind kind,
            TimeSpan fallbackDelay,
            CancellationToken ct = default)
        {
            Waits.Add((kind, fallbackDelay));
            ScopeWasAliveDuringWait = !scopeState.Disposed;
            _waitObserved.TrySetResult();
            stopping.Cancel();
            return Task.CompletedTask;
        }
    }

    private sealed class PassiveSignal : IIdentityPipelineSignal
    {
        public void Signal(IdentityPipelineSignalKind kind)
        {
        }

        public Task WaitAsync(
            IdentityPipelineSignalKind kind,
            TimeSpan fallbackDelay,
            CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed class StubIdentityJobRepository(int reclaimedCount)
        : IIdentityJobRepository
    {
        public TimeSpan? LastStuckThreshold { get; private set; }

        public IdentityJobState? LastProcessingState { get; private set; }

        public Task<int> ReclaimStuckJobsAsync(
            IdentityJobState processingState,
            TimeSpan stuckThreshold,
            CancellationToken ct = default)
        {
            LastProcessingState = processingState;
            LastStuckThreshold = stuckThreshold;
            return Task.FromResult(reclaimedCount);
        }

        public Task CreateAsync(IdentityJob job, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IdentityJob?> GetByEntityAsync(
            Guid entityId,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IdentityJob?> GetByIdAsync(
            Guid jobId,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<IdentityJob>> LeaseNextAsync(
            string workerName,
            IReadOnlyList<IdentityJobState> states,
            int batchSize,
            TimeSpan leaseDuration,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task UpdateStateAsync(
            Guid jobId,
            IdentityJobState newState,
            string? error = null,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task SetSelectedCandidateAsync(
            Guid jobId,
            Guid candidateId,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task SetResolvedQidAsync(
            Guid jobId,
            string qid,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<IdentityJob>> GetStaleAsync(
            TimeSpan age,
            int limit,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<IdentityJob>> GetByStateAsync(
            IdentityJobState state,
            int limit,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task ReleaseLeaseAsync(
            Guid jobId,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<int> CountActiveAsync(CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
