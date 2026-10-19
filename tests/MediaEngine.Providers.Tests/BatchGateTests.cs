using MediaEngine.Domain;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Models;
using MediaEngine.Domain.Services;
using MediaEngine.Intelligence.Contracts;
using MediaEngine.Intelligence.Models;
using MediaEngine.Providers.Contracts;
using MediaEngine.Providers.Helpers;
using MediaEngine.Providers.Services;
using MediaEngine.Providers.Workers;
using MediaEngine.Storage.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
// Disambiguate ProviderConfiguration — the IConfigurationLoader uses the Storage.Models one
using ProviderConfiguration = MediaEngine.Domain.Configuration.ProviderConfiguration;

namespace MediaEngine.Providers.Tests;

/// <summary>
/// Tests for the batch gate logic in <see cref="WikidataBridgeWorker.GetGatedRunIdsAsync"/>.
///
/// The gate prevents Stage 2 (Wikidata bridge) from processing jobs that belong
/// to an ingestion run that still has pending Stage 1 (retail match) work.
/// This ensures a full album/season lands as one coherent Wikidata batch instead
/// of trickling through piecemeal.
///
/// Tests verify the gate through <c>PollAsync</c> by observing which jobs
/// <c>LeaseNextAsync</c> picks up, using a recording <see cref="SpyIdentityJobRepository"/>
/// to capture the <c>excludeRunIds</c> parameter.
/// </summary>
public sealed class BatchGateTests
{
    // ══════════════════════════════════════════════════════════════════════
    //  Test 1 — Gate HOLDS when Stage 1 is still in progress for a run
    // ══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Gate_HoldsRunId_WhenStage1StillPending()
    {
        // Arrange — one running batch with 10 files (above small-batch threshold)
        var runId = Guid.NewGuid();

        var batchRepo = new SpyIngestionBatchRepository(
        [
            MakeRunningBatch(runId, filesTotal: 10, startedSecondsAgo: 10),
        ]);

        // Stage 1 still has 3 pending jobs for this run
        var jobRepo = new SpyIdentityJobRepository(
            pendingStage1Counts: new Dictionary<string, int>
            {
                [runId.ToString()] = 3,
            });

        var configLoader = new BatchGateConfigLoader(
            enabled: true, smallBatchThreshold: 5, timeoutSeconds: 300);

        var worker = MakeBridgeWorker(jobRepo, batchRepo, configLoader);

        // Act
        await worker.PollAsync(CancellationToken.None);

        // Assert — LeaseNextAsync was called with the run ID excluded
        Assert.NotNull(jobRepo.LastExcludeRunIds);
        Assert.Contains(runId.ToString(), jobRepo.LastExcludeRunIds!);
    }

    // ══════════════════════════════════════════════════════════════════════
    //  Test 2 — Gate OPENS when Stage 1 has no pending jobs
    // ══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Gate_OpensRunId_WhenStage1Complete()
    {
        // Arrange — running batch, but Stage 1 reports 0 pending jobs
        var runId = Guid.NewGuid();

        var batchRepo = new SpyIngestionBatchRepository(
        [
            MakeRunningBatch(runId, filesTotal: 10, startedSecondsAgo: 10),
        ]);

        // GetPendingStage1CountsByRunAsync returns the run but with 0 pending
        var jobRepo = new SpyIdentityJobRepository(
            pendingStage1Counts: new Dictionary<string, int>
            {
                [runId.ToString()] = 0,
            });

        var configLoader = new BatchGateConfigLoader(
            enabled: true, smallBatchThreshold: 5, timeoutSeconds: 300);

        var worker = MakeBridgeWorker(jobRepo, batchRepo, configLoader);

        // Act
        await worker.PollAsync(CancellationToken.None);

        // Assert — no run IDs should be excluded (gate is open)
        var excluded = jobRepo.LastExcludeRunIds;
        var isOpen = excluded is null || !excluded.Contains(runId.ToString());
        Assert.True(isOpen,
            $"Expected gate to be open for run {runId}, but it was held.");
    }

    // ══════════════════════════════════════════════════════════════════════
    //  Test 3 — Small-batch bypass: batches at or below the threshold skip
    //           the gate even when Stage 1 is still pending
    // ══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Gate_Bypassed_WhenBatchAtOrBelowSmallBatchThreshold()
    {
        // Arrange — batch has exactly 5 files, threshold is 5  (≤ threshold → bypass)
        var runId = Guid.NewGuid();

        var batchRepo = new SpyIngestionBatchRepository(
        [
            MakeRunningBatch(runId, filesTotal: 5, startedSecondsAgo: 10),
        ]);

        // Stage 1 still pending — would gate if the batch were large
        var jobRepo = new SpyIdentityJobRepository(
            pendingStage1Counts: new Dictionary<string, int>
            {
                [runId.ToString()] = 2,
            });

        var configLoader = new BatchGateConfigLoader(
            enabled: true, smallBatchThreshold: 5, timeoutSeconds: 300);

        var worker = MakeBridgeWorker(jobRepo, batchRepo, configLoader);

        // Act
        await worker.PollAsync(CancellationToken.None);

        // Assert — small batch is NOT excluded
        var excluded = jobRepo.LastExcludeRunIds;
        var notExcluded = excluded is null || !excluded.Contains(runId.ToString());
        Assert.True(notExcluded,
            $"Expected small batch {runId} to bypass gate, but it was held.");
    }

    // ══════════════════════════════════════════════════════════════════════
    //  Test 4 — Timeout bypass: batches past the deadline skip the gate
    // ══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Gate_Bypassed_WhenBatchExceedsTimeoutDeadline()
    {
        // Arrange — batch started 400 seconds ago, timeout is 300 seconds
        var runId = Guid.NewGuid();

        var batchRepo = new SpyIngestionBatchRepository(
        [
            MakeRunningBatch(runId, filesTotal: 20, startedSecondsAgo: 400),
        ]);

        // Stage 1 still pending, but we've waited long enough
        var jobRepo = new SpyIdentityJobRepository(
            pendingStage1Counts: new Dictionary<string, int>
            {
                [runId.ToString()] = 5,
            });

        var configLoader = new BatchGateConfigLoader(
            enabled: true, smallBatchThreshold: 5, timeoutSeconds: 300);

        var worker = MakeBridgeWorker(jobRepo, batchRepo, configLoader);

        // Act
        await worker.PollAsync(CancellationToken.None);

        // Assert — timed-out batch is NOT excluded
        var excluded = jobRepo.LastExcludeRunIds;
        var notExcluded = excluded is null || !excluded.Contains(runId.ToString());
        Assert.True(notExcluded,
            $"Expected timed-out batch {runId} to bypass gate, but it was still held.");
    }

    // ══════════════════════════════════════════════════════════════════════
    //  Test 5 — Null run ID (ad-hoc job) is never gated
    // ══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Gate_NeverExcludes_AdHocJobsWithNullRunId()
    {
        // Arrange — no batches at all (ad-hoc jobs have no IngestionRunId)
        var batchRepo = new SpyIngestionBatchRepository([]);
        var jobRepo = new SpyIdentityJobRepository(
            pendingStage1Counts: new Dictionary<string, int>());

        // Seed a RetailMatched job with no IngestionRunId
        var job = new IdentityJob
        {
            Id = Guid.NewGuid(),
            EntityId = Guid.NewGuid(),
            EntityType = "MediaAsset",
            MediaType = "Books",
            State = IdentityJobState.RetailMatched.ToString(),
            IngestionRunId = null,
        };
        jobRepo.SeedJob(job);

        var configLoader = new BatchGateConfigLoader(
            enabled: true, smallBatchThreshold: 5, timeoutSeconds: 300);

        var worker = MakeBridgeWorker(jobRepo, batchRepo, configLoader);

        // Act
        await worker.PollAsync(CancellationToken.None);

        // Assert — LeaseNextAsync was called with null or empty excludeRunIds
        var excluded = jobRepo.LastExcludeRunIds;
        var noExclusions = excluded is null || excluded.Count == 0;
        Assert.True(noExclusions,
            "Expected no exclusions for ad-hoc jobs with null IngestionRunId.");
    }

    // ══════════════════════════════════════════════════════════════════════
    //  Test 6 — Cross-batch independence: two concurrent runs gate independently
    // ══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Gate_ExcludesOnlyIncompleteRun_WhenTwoBatchesConcurrent()
    {
        // Arrange — two concurrent runs:
        //   runA: Stage 1 still in progress → gated
        //   runB: Stage 1 complete → not gated
        var runA = Guid.NewGuid();
        var runB = Guid.NewGuid();

        var batchRepo = new SpyIngestionBatchRepository(
        [
            MakeRunningBatch(runA, filesTotal: 20, startedSecondsAgo: 10),
            MakeRunningBatch(runB, filesTotal: 20, startedSecondsAgo: 10),
        ]);

        var jobRepo = new SpyIdentityJobRepository(
            pendingStage1Counts: new Dictionary<string, int>
            {
                [runA.ToString()] = 4,   // still pending → gated
                [runB.ToString()] = 0,   // all done → open
            });

        var configLoader = new BatchGateConfigLoader(
            enabled: true, smallBatchThreshold: 5, timeoutSeconds: 300);

        var worker = MakeBridgeWorker(jobRepo, batchRepo, configLoader);

        // Act
        await worker.PollAsync(CancellationToken.None);

        // Assert — runA excluded, runB not excluded
        var excluded = jobRepo.LastExcludeRunIds ?? [];
        Assert.Contains(runA.ToString(), excluded);
        Assert.DoesNotContain(runB.ToString(), excluded);
    }

    // ══════════════════════════════════════════════════════════════════════
    //  Test 7 — Gate disabled: no exclusions regardless of batch state
    // ══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Gate_NoExclusions_WhenGateDisabledInConfig()
    {
        // Arrange — a large running batch with pending Stage 1 jobs,
        // but the gate is turned off in config
        var runId = Guid.NewGuid();

        var batchRepo = new SpyIngestionBatchRepository(
        [
            MakeRunningBatch(runId, filesTotal: 50, startedSecondsAgo: 10),
        ]);

        var jobRepo = new SpyIdentityJobRepository(
            pendingStage1Counts: new Dictionary<string, int>
            {
                [runId.ToString()] = 10,
            });

        // Gate explicitly disabled
        var configLoader = new BatchGateConfigLoader(
            enabled: false, smallBatchThreshold: 5, timeoutSeconds: 300);

        var worker = MakeBridgeWorker(jobRepo, batchRepo, configLoader);

        // Act
        await worker.PollAsync(CancellationToken.None);

        // Assert — gate is disabled, so nothing should be excluded
        var excluded = jobRepo.LastExcludeRunIds;
        var noExclusions = excluded is null || excluded.Count == 0;
        Assert.True(noExclusions,
            $"Expected no exclusions when gate is disabled, but run {runId} was held.");
    }

    // ══════════════════════════════════════════════════════════════════════
    //  Test 8 — Completed/failed batches are never gating candidates
    // ══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Gate_IgnoresCompletedAndFailedBatches()
    {
        // Arrange — two non-running batches (completed + failed).
        // The job repo reports pending jobs for both, but the gate should
        // never query them because they aren't "running".
        var runCompleted = Guid.NewGuid();
        var runFailed = Guid.NewGuid();

        var completedBatch = MakeRunningBatch(runCompleted, filesTotal: 20, startedSecondsAgo: 5);
        completedBatch.Status = "completed";

        var failedBatch = MakeRunningBatch(runFailed, filesTotal: 20, startedSecondsAgo: 5);
        failedBatch.Status = "failed";

        var batchRepo = new SpyIngestionBatchRepository([completedBatch, failedBatch]);

        // If the gate consulted these run IDs, it would gate them (pending > 0).
        // Correct behaviour: it never asks.
        var jobRepo = new SpyIdentityJobRepository(
            pendingStage1Counts: new Dictionary<string, int>
            {
                [runCompleted.ToString()] = 5,
                [runFailed.ToString()] = 3,
            });

        var configLoader = new BatchGateConfigLoader(
            enabled: true, smallBatchThreshold: 5, timeoutSeconds: 300);

        var worker = MakeBridgeWorker(jobRepo, batchRepo, configLoader);

        // Act
        await worker.PollAsync(CancellationToken.None);

        // Assert — neither completed nor failed run should be excluded
        var excluded = jobRepo.LastExcludeRunIds ?? [];
        Assert.DoesNotContain(runCompleted.ToString(), excluded);
        Assert.DoesNotContain(runFailed.ToString(), excluded);
    }

    // ══════════════════════════════════════════════════════════════════════
    //  Helpers
    // ══════════════════════════════════════════════════════════════════════

    private static IngestionBatch MakeRunningBatch(Guid id, int filesTotal, int startedSecondsAgo)
        => new()
        {
            Id = id,
            Status = "running",
            FilesTotal = filesTotal,
            StartedAt = DateTimeOffset.UtcNow.AddSeconds(-startedSecondsAgo),
            CreatedAt = DateTimeOffset.UtcNow.AddSeconds(-startedSecondsAgo),
            UpdatedAt = DateTimeOffset.UtcNow,
        };

    private static WikidataBridgeWorker MakeBridgeWorker(
        SpyIdentityJobRepository jobRepo,
        SpyIngestionBatchRepository batchRepo,
        BatchGateConfigLoader configLoader)
    {
        var bridgeIdHelper = new BridgeIdHelper(configLoader);

        // No ReconciliationAdapter in provider list — gate logic runs before
        // the adapter is needed; PollAsync returns early after LeaseNextAsync
        // (either 0 jobs found, or transitions to QidNoMatch on no adapter).
        return new WikidataBridgeWorker(
            jobRepo,
            new StubWikidataCandidateRepository(),
            CreateStubStageOutcomeFactory(),
            new TimelineRecorder(new StubEntityTimelineRepository()),
            bridgeIdHelper,
            [],  // no providers — forces QidNoMatch path if any jobs are leased
            new StubBridgeIdRepository(),
            new StubMetadataClaimRepository(),
            new StubCanonicalValueRepository(),
            new StubScoringEngine(),
            configLoader,
            new StubWorkRepository(),
            new WorkClaimRouter(),
            new CatalogUpsertService(new StubWorkRepository()),
            batchRepo,
            null!, // PostPipelineService — batch gate tests stop before organization
            CoverArtWorkerTestFactory.Create(new StubCanonicalValueRepository(), new StubWorkRepository()),
            NullLogger<WikidataBridgeWorker>.Instance);
    }

    private static StageOutcomeFactory CreateStubStageOutcomeFactory()
        => new StageOutcomeFactory(
            new StubReviewQueueRepository(),
            new StubSystemActivityRepository(),
            new StubEventPublisher(),
            new StubCanonicalValueRepository(),
            NullLogger<StageOutcomeFactory>.Instance);

    // ══════════════════════════════════════════════════════════════════════
    //  Spy / stub implementations
    // ══════════════════════════════════════════════════════════════════════

    // ── SpyIdentityJobRepository ─────────────────────────────────────────
    // Records the excludeRunIds parameter passed to LeaseNextAsync so tests
    // can assert on which run IDs were held by the gate.

    private sealed class SpyIdentityJobRepository : IIdentityJobRepository
    {
        private readonly List<IdentityJob> _jobs = [];
        private readonly IReadOnlyDictionary<string, int> _pendingStage1Counts;

        /// <summary>The excludeRunIds value from the most recent LeaseNextAsync call.</summary>
        public IReadOnlyList<string>? LastExcludeRunIds { get; private set; }

        public SpyIdentityJobRepository(IReadOnlyDictionary<string, int> pendingStage1Counts)
            => _pendingStage1Counts = pendingStage1Counts;

        public void SeedJob(IdentityJob job) => _jobs.Add(job);

        public Task<IReadOnlyList<IdentityJob>> LeaseNextAsync(
            string workerName,
            IReadOnlyList<IdentityJobState> states,
            int batchSize,
            TimeSpan leaseDuration,
            IReadOnlyList<string>? excludeRunIds = null,
            CancellationToken ct = default)
        {
            LastExcludeRunIds = excludeRunIds;

            var stateStrings = states.Select(s => s.ToString()).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var excluded = excludeRunIds is { Count: > 0 }
                ? new HashSet<string>(excludeRunIds, StringComparer.OrdinalIgnoreCase)
                : null;

            var matches = _jobs
                .Where(j => stateStrings.Contains(j.State)
                         && (excluded is null || j.IngestionRunId is null
                             || !excluded.Contains(j.IngestionRunId.ToString()!)))
                .Take(batchSize)
                .ToList();

            foreach (var j in matches)
            {
                j.LeaseOwner = workerName;
                j.LeaseExpiresAt = DateTimeOffset.UtcNow.Add(leaseDuration);
            }

            return Task.FromResult<IReadOnlyList<IdentityJob>>(matches);
        }

        public Task<IReadOnlyDictionary<string, int>> GetPendingStage1CountsByRunAsync(
            IReadOnlyList<string> ingestionRunIds, CancellationToken ct = default)
        {
            // Return counts only for the run IDs the caller actually asked about.
            var result = ingestionRunIds
                .Where(id => _pendingStage1Counts.ContainsKey(id))
                .ToDictionary(id => id, id => _pendingStage1Counts[id]);

            return Task.FromResult<IReadOnlyDictionary<string, int>>(result);
        }

        // ── No-op implementations for the rest of the interface ────────────

        public Task CreateAsync(IdentityJob job, CancellationToken ct = default)
        {
            _jobs.Add(job);
            return Task.CompletedTask;
        }

        public Task UpdateStateAsync(Guid jobId, IdentityJobState newState, string? error = null, CancellationToken ct = default)
        {
            var job = _jobs.FirstOrDefault(j => j.Id == jobId);
            if (job is not null)
            {
                job.State = newState.ToString();
                job.LastError = error;
                job.UpdatedAt = DateTimeOffset.UtcNow;
            }
            return Task.CompletedTask;
        }

        public Task SetSelectedCandidateAsync(Guid jobId, Guid candidateId, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetResolvedQidAsync(Guid jobId, string qid, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IdentityJob?> GetByIdAsync(Guid jobId, CancellationToken ct = default) => Task.FromResult(_jobs.FirstOrDefault(j => j.Id == jobId));
        public Task<IdentityJob?> GetByEntityAsync(Guid entityId, CancellationToken ct = default) => Task.FromResult(_jobs.FirstOrDefault(j => j.EntityId == entityId));
        public Task<IReadOnlyList<IdentityJob>> GetStaleAsync(TimeSpan age, int limit, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<IdentityJob>>([]);
        public Task<IReadOnlyList<IdentityJob>> GetByStateAsync(IdentityJobState state, int limit, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<IdentityJob>>(_jobs.Where(j => j.State == state.ToString()).Take(limit).ToList());
        public Task<IReadOnlyDictionary<string, int>> GetStateCountsByRunAsync(Guid ingestionRunId, CancellationToken ct = default) => Task.FromResult<IReadOnlyDictionary<string, int>>(new Dictionary<string, int>());
        public Task<int> ReclaimStuckJobsAsync(
            IdentityJobState processingState,
            TimeSpan stuckThreshold,
            CancellationToken ct = default) => Task.FromResult(0);
        public Task ReleaseLeaseAsync(Guid jobId, CancellationToken ct = default) => Task.CompletedTask;
        public Task<int> CountActiveAsync(CancellationToken ct = default) => Task.FromResult(_jobs.Count(j =>
            j.State != IdentityJobState.Ready.ToString() &&
            j.State != IdentityJobState.ReadyWithoutUniverse.ToString() &&
            j.State != IdentityJobState.Failed.ToString()));
    }

    // ── SpyIngestionBatchRepository ──────────────────────────────────────

    private sealed class SpyIngestionBatchRepository : IIngestionBatchRepository
    {
        private readonly IReadOnlyList<IngestionBatch> _batches;

        public SpyIngestionBatchRepository(IReadOnlyList<IngestionBatch> batches)
            => _batches = batches;

        public Task<IReadOnlyList<IngestionBatch>> GetRecentAsync(int limit = 20, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<IngestionBatch>>(_batches.Take(limit).ToList());

        // ── No-op implementations ──────────────────────────────────────────

        public Task CreateAsync(IngestionBatch batch, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpdateCountsAsync(Guid id, int filesTotal, int filesProcessed, int filesIdentified, int filesReview, int filesNoMatch, int filesFailed, CancellationToken ct = default) => Task.CompletedTask;
        public Task CompleteAsync(Guid id, string status, CancellationToken ct = default) => Task.CompletedTask;
        public Task IncrementCounterAsync(Guid id, BatchCounterColumn column, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IngestionBatch?> GetByIdAsync(Guid id, CancellationToken ct = default) => Task.FromResult<IngestionBatch?>(null);
        public Task<int> GetNeedsAttentionCountAsync(CancellationToken ct = default) => Task.FromResult(0);
        public Task<IngestionBatchProgressSnapshot> GetProgressSnapshotAsync(Guid batchId, CancellationToken ct = default)
            => Task.FromResult(new IngestionBatchProgressSnapshot());
    }

    // ── BatchGateConfigLoader ────────────────────────────────────────────
    // Minimal IConfigurationLoader that returns a CoreConfiguration with the
    // BatchGate settings under test. All other methods throw — not called.

    private sealed class BatchGateConfigLoader : IConfigurationLoader
    {
        private readonly CoreConfiguration _core;

        public BatchGateConfigLoader(bool enabled, int smallBatchThreshold, int timeoutSeconds)
        {
            _core = new CoreConfiguration
            {
                Pipeline = new PipelineSettings
                {
                    BatchGate = new BatchGateSettings
                    {
                        Enabled = enabled,
                        SmallBatchThreshold = smallBatchThreshold,
                        TimeoutSeconds = timeoutSeconds,
                    },
                    LeaseSizes = new LeaseSizeSettings
                    {
                        Wikidata = 50,
                    },
                },
            };
        }

        public CoreConfiguration LoadCore() => _core;
        public PipelineConfiguration LoadPipelines() => new()
        {
            Pipelines = new Dictionary<string, MediaTypePipeline>(StringComparer.OrdinalIgnoreCase),
        };
        public ScoringSettings LoadScoring() => new();
        public HydrationSettings LoadHydration() => new();
        public IReadOnlyList<ProviderConfiguration> LoadAllProviders() => [];
        public T? LoadConfig<T>(string subdirectory, string name) where T : class => default;

        // Everything below is not exercised by batch gate tests
        public void SaveCore(CoreConfiguration config) => throw new NotImplementedException();
        public void SaveScoring(ScoringSettings settings) => throw new NotImplementedException();
        public MaintenanceSettings LoadMaintenance() => throw new NotImplementedException();
        public void SaveMaintenance(MaintenanceSettings settings) => throw new NotImplementedException();
        public void SaveHydration(HydrationSettings settings) => throw new NotImplementedException();
        public void SavePipelines(PipelineConfiguration config) => throw new NotImplementedException();
        public DisambiguationSettings LoadDisambiguation() => throw new NotImplementedException();
        public void SaveDisambiguation(DisambiguationSettings settings) => throw new NotImplementedException();
        public TranscodingSettings LoadTranscoding() => throw new NotImplementedException();
        public void SaveTranscoding(TranscodingSettings settings) => throw new NotImplementedException();
        public MediaTypeConfiguration LoadMediaTypes() => throw new NotImplementedException();
        public void SaveMediaTypes(MediaTypeConfiguration config) => throw new NotImplementedException();
        public LibrariesConfiguration LoadLibraries() => throw new NotImplementedException();
        public FieldPriorityConfiguration LoadFieldPriorities() => throw new NotImplementedException();
        public void SaveFieldPriorities(FieldPriorityConfiguration config) => throw new NotImplementedException();
        public ProviderConfiguration? LoadProvider(string name) => throw new NotImplementedException();
        public void SaveProvider(ProviderConfiguration config) => throw new NotImplementedException();
        public T? LoadAi<T>() where T : class => throw new NotImplementedException();
        public void SaveAi<T>(T settings) where T : class => throw new NotImplementedException();
        public PaletteConfiguration LoadPalette() => throw new NotImplementedException();
        public void SavePalette(PaletteConfiguration palette) => throw new NotImplementedException();
        public void SaveConfig<T>(string subdirectory, string name, T config) where T : class => throw new NotImplementedException();
    }

    // ── Remaining stubs (minimal — copied pattern from WorkerPipelineTests) ──

    private sealed class StubWikidataCandidateRepository : IWikidataCandidateRepository
    {
        public Task InsertBatchAsync(IReadOnlyList<WikidataBridgeCandidate> candidates, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<WikidataBridgeCandidate>> GetByJobAsync(Guid jobId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<WikidataBridgeCandidate>>([]);
        public Task<WikidataBridgeCandidate?> GetSelectedAsync(Guid jobId, CancellationToken ct = default) => Task.FromResult<WikidataBridgeCandidate?>(null);
        public Task<WikidataBridgeCandidate?> GetByIdAsync(Guid candidateId, CancellationToken ct = default) => Task.FromResult<WikidataBridgeCandidate?>(null);
    }

    private sealed class StubBridgeIdRepository : IBridgeIdRepository
    {
        public Task<IReadOnlyList<BridgeIdEntry>> GetByEntityAsync(Guid entityId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<BridgeIdEntry>>([]);

        public Task<IReadOnlyDictionary<Guid, IReadOnlyList<BridgeIdEntry>>> GetByEntitiesAsync(IReadOnlyList<Guid> entityIds, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyDictionary<Guid, IReadOnlyList<BridgeIdEntry>>>(
                entityIds.ToDictionary(id => id, _ => (IReadOnlyList<BridgeIdEntry>)[]));

        public Task UpsertBatchAsync(IReadOnlyList<BridgeIdEntry> entries, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpsertAsync(BridgeIdEntry entry, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteByEntityAsync(Guid entityId, CancellationToken ct = default) => Task.CompletedTask;

        public Task<BridgeIdEntry?> FindAsync(Guid entityId, string idType, CancellationToken ct = default)
            => Task.FromResult<BridgeIdEntry?>(null);

        public Task<IReadOnlyList<BridgeIdEntry>> FindByValueAsync(string idType, string idValue, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<BridgeIdEntry>>([]);
    }

    private sealed class StubCanonicalValueRepository : ICanonicalValueRepository
    {
        public Task<IReadOnlyList<CanonicalValue>> GetByEntityAsync(Guid entityId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<CanonicalValue>>([]);

        public Task<IReadOnlyDictionary<Guid, IReadOnlyList<CanonicalValue>>> GetByEntitiesAsync(IReadOnlyList<Guid> entityIds, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyDictionary<Guid, IReadOnlyList<CanonicalValue>>>(
                new Dictionary<Guid, IReadOnlyList<CanonicalValue>>());

        public Task UpsertBatchAsync(IReadOnlyList<CanonicalValue> values, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteByEntityAsync(Guid entityId, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<CanonicalValue>> GetConflictedAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<CanonicalValue>>([]);
        public Task DeleteByKeyAsync(Guid entityId, string key, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<Guid>> FindByValueAsync(string key, string value, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Guid>>([]);
        public Task<IReadOnlyList<CanonicalValue>> FindByKeyAndPrefixAsync(string key, string prefix, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<CanonicalValue>>([]);
        public Task<IReadOnlyList<Guid>> GetEntitiesNeedingEnrichmentAsync(string hasField, string missingField, int limit, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Guid>>([]);
    }

    private sealed class StubMetadataClaimRepository : IMetadataClaimRepository
    {
        public Task InsertBatchAsync(IReadOnlyList<MetadataClaim> claims, CancellationToken ct = default) => Task.CompletedTask;

        public Task<IReadOnlyList<MetadataClaim>> GetByEntityAsync(Guid entityId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<MetadataClaim>>([]);

        public Task DeleteByEntityAsync(Guid entityId, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class StubScoringEngine : IScoringEngine
    {
        public Task<ScoringResult> ScoreEntityAsync(ScoringContext context, CancellationToken ct = default)
            => Task.FromResult(new ScoringResult
            {
                EntityId = context.EntityId,
                OverallConfidence = 0.90,
                ScoredAt = DateTimeOffset.UtcNow,
                FieldScores = [],
            });

        public Task<IReadOnlyList<ScoringResult>> ScoreBatchAsync(IEnumerable<ScoringContext> contexts, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ScoringResult>>(
                contexts.Select(c => ScoreEntityAsync(c, ct).Result).ToList());
    }

    private sealed class StubReviewQueueRepository : IReviewQueueRepository
    {
        public Task<Guid> InsertAsync(ReviewQueueEntry entry, CancellationToken ct = default) => Task.FromResult(entry.Id);
        public Task<IReadOnlyList<ReviewQueueEntry>> GetPendingAsync(int limit = 50, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ReviewQueueEntry>>([]);
        public Task<ReviewQueueEntry?> GetByIdAsync(Guid id, CancellationToken ct = default) => Task.FromResult<ReviewQueueEntry?>(null);
        public Task<IReadOnlyList<ReviewQueueEntry>> GetByEntityAsync(Guid entityId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ReviewQueueEntry>>([]);
        public Task<IReadOnlyList<ReviewQueueEntry>> GetPendingByEntityAsync(Guid entityId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ReviewQueueEntry>>([]);
        public Task UpdateStatusAsync(Guid id, string status, string? resolvedBy = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task<int> MarkPendingReadyByEntityAsync(Guid entityId, CancellationToken ct = default) => Task.FromResult(0);
        public Task<IReadOnlyList<ReviewQueueEntry>> PromotePendingReadyByEntityAsync(Guid entityId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ReviewQueueEntry>>([]);
        public Task<int> GetPendingCountAsync(CancellationToken ct = default) => Task.FromResult(0);
        public Task<int> DismissAllByEntityAsync(Guid entityId, CancellationToken ct = default) => Task.FromResult(0);
        public Task<int> ResolveAllByEntityAsync(Guid entityId, string resolvedBy = "system:auto-organize", CancellationToken ct = default) => Task.FromResult(0);
        public Task<int> ResolvePendingByEntityAndTriggersAsync(Guid entityId, IReadOnlyCollection<string> triggers, string resolvedBy, CancellationToken ct = default) => Task.FromResult(0);
        public Task<int> PurgeOrphanedAsync(CancellationToken ct = default) => Task.FromResult(0);
    }

    private sealed class StubSystemActivityRepository : ISystemActivityRepository
    {
        public Task LogAsync(SystemActivityEntry entry, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<SystemActivityEntry>> GetRecentAsync(int limit = 50, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<SystemActivityEntry>>([]);
        public Task<int> PruneOlderThanAsync(int retentionDays, CancellationToken ct = default) => Task.FromResult(0);
        public Task<long> CountAsync(CancellationToken ct = default) => Task.FromResult(0L);
        public Task<IReadOnlyList<SystemActivityEntry>> GetByRunIdAsync(Guid runId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<SystemActivityEntry>>([]);
        public Task<IReadOnlyList<SystemActivityEntry>> GetRecentByTypesAsync(IReadOnlyList<string> actionTypes, int limit = 50, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<SystemActivityEntry>>([]);
        public Task<IReadOnlyList<SystemActivityEntry>> GetRecentByProfileAsync(Guid profileId, int limit = 50, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<SystemActivityEntry>>([]);

        public Task<(IReadOnlyList<SystemActivityEntry> Items, int Total)> QueryAsync(IReadOnlyList<string> actionTypes, string? search, int offset, int limit, CancellationToken ct = default)
            => Task.FromResult<(IReadOnlyList<SystemActivityEntry>, int)>(([], 0));
    }

    private sealed class StubEventPublisher : IEventPublisher
    {
        public Task PublishAsync<TPayload>(string eventName, TPayload payload, CancellationToken ct = default)
            where TPayload : notnull
            => Task.CompletedTask;
    }

    private sealed class StubEntityTimelineRepository : IEntityTimelineRepository
    {
        public Task InsertEventAsync(EntityEvent evt, CancellationToken ct = default) => Task.CompletedTask;
        public Task InsertEventsAsync(IReadOnlyList<EntityEvent> events, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<EntityEvent>> GetEventsByEntityAsync(Guid entityId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<EntityEvent>>([]);
        public Task<EntityEvent?> GetLatestEventAsync(Guid entityId, int stage, CancellationToken ct = default) => Task.FromResult<EntityEvent?>(null);
        public Task<IReadOnlyList<EntityEvent>> GetCurrentPipelineStateAsync(Guid entityId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<EntityEvent>>([]);
        public Task<EntityEvent?> GetEventByIdAsync(Guid eventId, CancellationToken ct = default) => Task.FromResult<EntityEvent?>(null);
        public Task InsertFieldChangesAsync(IReadOnlyList<EntityFieldChange> changes, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<EntityFieldChange>> GetFieldChangesByEventAsync(Guid eventId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<EntityFieldChange>>([]);
        public Task<IReadOnlyList<EntityFieldChange>> GetFieldHistoryAsync(Guid entityId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<EntityFieldChange>>([]);
        public Task<IReadOnlyList<EntityFieldChange>> GetFieldHistoryAsync(Guid entityId, string field, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<EntityFieldChange>>([]);
        public Task<IReadOnlyList<EntityFieldChange>> GetFileOriginalsForEventAsync(Guid eventId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<EntityFieldChange>>([]);
        public Task<IReadOnlyDictionary<Guid, EntityEvent>> GetLatestStage2EventsAsync(IReadOnlyList<Guid> entityIds, CancellationToken ct = default) => Task.FromResult<IReadOnlyDictionary<Guid, EntityEvent>>(new Dictionary<Guid, EntityEvent>());
        public Task<int> CullOldEventsAsync(TimeSpan retention, CancellationToken ct = default) => Task.FromResult(0);
        public Task DeleteByEntityAsync(Guid entityId, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class StubWorkRepository : IWorkRepository
    {
        public Task<Guid?> FindParentByKeyAsync(MediaType mediaType, string parentKey, CancellationToken ct = default) => Task.FromResult<Guid?>(null);
        public Task<Guid> GetOrCreateParentAsync(MediaType mediaType, string parentKey, Guid? grandparentWorkId, int? ordinal, double? ordinalSort = null, CancellationToken ct = default) => Task.FromResult(Guid.NewGuid());
        public Task<Guid?> FindChildByOrdinalAsync(Guid parentWorkId, int ordinal, CancellationToken ct = default) => Task.FromResult<Guid?>(null);
        public Task<Guid?> FindChildByOrdinalSortAsync(Guid parentWorkId, double ordinalSort, CancellationToken ct = default) => Task.FromResult<Guid?>(null);
        public Task<Guid?> FindChildByTitleAsync(Guid parentWorkId, string title, CancellationToken ct = default) => Task.FromResult<Guid?>(null);
        public Task<Guid?> FindByExternalIdentifierAsync(string scheme, string value, CancellationToken ct = default) => Task.FromResult<Guid?>(null);
        public Task<Guid> InsertParentAsync(MediaType mediaType, string parentKey, Guid? grandparentWorkId, int? ordinal, CancellationToken ct = default) => Task.FromResult(Guid.NewGuid());
        public Task<Guid> InsertChildAsync(MediaType mediaType, Guid parentWorkId, int? ordinal, CancellationToken ct = default) => Task.FromResult(Guid.NewGuid());
        public Task<Guid> GetOrCreateChildAsync(MediaType mediaType, Guid parentWorkId, int? ordinal, double? ordinalSort = null, CancellationToken ct = default) => Task.FromResult(Guid.NewGuid());
        public Task UpdateOrdinalSortAsync(Guid workId, double? ordinalSort, CancellationToken ct = default) => Task.CompletedTask;
        public Task<Guid> InsertStandaloneAsync(MediaType mediaType, CancellationToken ct = default) => Task.FromResult(Guid.NewGuid());
        public Task<Guid> InsertCatalogChildAsync(MediaType mediaType, Guid parentWorkId, int? ordinal, IReadOnlyDictionary<string, string>? externalIdentifiers, CancellationToken ct = default) => Task.FromResult(Guid.NewGuid());
        public Task PromoteCatalogToOwnedAsync(Guid workId, CancellationToken ct = default) => Task.CompletedTask;
        public Task WriteExternalIdentifiersAsync(Guid workId, IReadOnlyDictionary<string, string> identifiers, CancellationToken ct = default, bool overwrite = false) => Task.CompletedTask;
        public Task<WorkLineage?> GetLineageByAssetAsync(Guid assetId, CancellationToken ct = default) => Task.FromResult<WorkLineage?>(null);
        public Task<ConfirmedSiblingWorkQid?> FindConfirmedSiblingQidAsync(MediaType sourceMediaType, IReadOnlyList<MediaType> candidateMediaTypes, string title, string? creator, Guid? excludeWorkId = null, CancellationToken ct = default) => Task.FromResult<ConfirmedSiblingWorkQid?>(null);
    }
}
