using MediaEngine.Domain;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Models;
using MediaEngine.Providers.Services;
using MediaEngine.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Providers.Tests;

/// <summary>
/// A match a person applied in the editor is pinned (universal editor v5, D8): a later scan, or a folder hint that now disagrees, never
/// re-identifies the item, but only the retail stage is skipped: the Wikidata bridge, hydration and enrichment still run. Real temp SQLite;
/// the workers the identity pipeline would call are absent, and a recording limiter shows which stage the pipeline asked for.
/// </summary>
public sealed class PinnedMatchTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), $"tuvima_pinned_{Guid.NewGuid():N}");
    private readonly DatabaseConnection _db;
    private readonly IdentityJobRepository _jobs;
    private readonly CanonicalValueRepository _canonicals;
    private readonly PinnedMatchGuard _guard;

    public PinnedMatchTests()
    {
        DapperConfiguration.Configure();
        Directory.CreateDirectory(_tempRoot);
        _db = new DatabaseConnection(Path.Combine(_tempRoot, "library.db"));
        _db.InitializeSchema();
        _db.RunStartupChecks();
        _jobs = new IdentityJobRepository(_db);
        _canonicals = new CanonicalValueRepository(_db);
        _guard = new PinnedMatchGuard(_canonicals, NullLogger<PinnedMatchGuard>.Instance);
    }

    public void Dispose()
    {
        try { _db.Dispose(); } catch (Exception) { /* temp store: cleanup is best effort */ }
        try { Directory.Delete(_tempRoot, recursive: true); } catch (Exception) { /* best effort */ }
    }

    [Fact]
    public async Task AnItemWithAnAppliedMatch_IsPinned_AndOneWithoutIsNot()
    {
        var pinned = await SeedWorkAsync(pinned: true);
        var loose = await SeedWorkAsync(pinned: false);

        Assert.True(await _guard.IsPinnedAsync(pinned));
        Assert.False(await _guard.IsPinnedAsync(loose));
        Assert.False(await _guard.IsPinnedAsync(Guid.NewGuid()));
        Assert.False(await _guard.IsPinnedAsync(Guid.Empty));
    }

    [Fact]
    public async Task ARescanOfAPinnedItem_WithAFolderHintThatDisagrees_QueuesItsJobPastTheRetailStage()
    {
        var pinned = await SeedWorkAsync(pinned: true);
        var service = Pipeline();

        var queued = await service.EnqueueAsync(new HarvestRequest
        {
            EntityId = pinned,
            EntityType = EntityType.Work,
            MediaType = MediaType.Movies,
            Hints = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["title"] = "Some Other Film (2001)" },
        });

        Assert.NotEqual(Guid.Empty, queued);
        var job = await _jobs.GetByEntityAsync(pinned);
        Assert.Equal(nameof(IdentityJobState.RetailMatched), job!.State);
    }

    [Fact]
    public async Task APinnedItemRunInline_SkipsTheRetailStage_ButStillAsksForTheWikidataBridge()
    {
        var pinned = await SeedWorkAsync(pinned: true);
        var loose = await SeedWorkAsync(pinned: false);
        var pinnedKinds = new RecordingLimiter();
        var looseKinds = new RecordingLimiter();

        await Pipeline(pinnedKinds).RunSynchronousAsync(new HarvestRequest { EntityId = pinned, EntityType = EntityType.Work, MediaType = MediaType.Movies });
        await Pipeline(looseKinds).RunSynchronousAsync(new HarvestRequest { EntityId = loose, EntityType = EntityType.Work, MediaType = MediaType.Movies });

        Assert.DoesNotContain(EnrichmentWorkKind.RetailProvider, pinnedKinds.Kinds);
        Assert.Contains(EnrichmentWorkKind.Wikidata, pinnedKinds.Kinds);
        Assert.Contains(EnrichmentWorkKind.RetailProvider, looseKinds.Kinds);
    }

    [Fact]
    public async Task APinnedItemWithNoWikidataId_StillGetsItsWikidataAlignmentQueued()
    {
        var pinned = await SeedWorkAsync(pinned: true);

        await Pipeline().EnqueueAsync(new HarvestRequest { EntityId = pinned, EntityType = EntityType.Work, MediaType = MediaType.Movies });

        // The bridge worker leases RetailMatched jobs; a job parked at RetailMatched is exactly what asks Wikidata about the item.
        var leased = await _jobs.LeaseNextAsync("bridge", [IdentityJobState.RetailMatched], 10, TimeSpan.FromMinutes(5));
        Assert.Contains(leased, job => job.EntityId == pinned);
    }

    [Fact]
    public async Task ARescanOfAnItemNobodyPinned_StillQueuesItsIdentityJob()
    {
        var loose = await SeedWorkAsync(pinned: false);

        var queued = await Pipeline().EnqueueAsync(new HarvestRequest { EntityId = loose, EntityType = EntityType.Work, MediaType = MediaType.Movies });

        Assert.NotEqual(Guid.Empty, queued);
        Assert.NotNull(await _jobs.GetByEntityAsync(loose));
    }

    [Fact]
    public async Task ThePersonsOwnRematchOfAPinnedItem_StillRuns()
    {
        var pinned = await SeedWorkAsync(pinned: true);

        var queued = await Pipeline().EnqueueAsync(new HarvestRequest
        {
            EntityId = pinned,
            EntityType = EntityType.Work,
            MediaType = MediaType.Movies,
            SkipRetailStage = true,
            IsUserResolution = true,
        });

        Assert.NotEqual(Guid.Empty, queued);
    }

    [Fact]
    public async Task AQueuedAutomaticJobForAPinnedItem_SkipsTheRetailStageOnly_AndOthersGoOn()
    {
        var pinned = await SeedWorkAsync(pinned: true);
        var loose = await SeedWorkAsync(pinned: false);
        var pinnedJob = await QueueAsync(pinned);
        var looseJob = await QueueAsync(loose);

        var leased = await _jobs.LeaseNextAsync("test", [IdentityJobState.Queued], 10, TimeSpan.FromMinutes(5));
        var allowed = await ((IPinnedMatchGuard)_guard).SettlePinnedJobsAsync(_jobs, leased, NullLogger.Instance, CancellationToken.None);

        Assert.Equal([looseJob], allowed.Select(job => job.Id).ToArray());
        Assert.Equal(nameof(IdentityJobState.RetailMatched), (await _jobs.GetByIdAsync(pinnedJob))!.State);
    }

    [Fact]
    public async Task ThePersonsOwnMatch_WritesThePinOnTheAssetAndTheWorkItSavedTo()
    {
        // The retail-match write pins every target it saves to (the work or container and the asset); the guard reads either one.
        var work = await SeedWorkAsync(pinned: true);
        var asset = await SeedWorkAsync(pinned: true);

        Assert.True(await _guard.IsPinnedAsync(work));
        Assert.True(await _guard.IsPinnedAsync(asset));
    }

    private SynchronousIdentityPipelineService Pipeline(IEnrichmentConcurrencyLimiter? limiter = null) => new(
        _jobs, _canonicals, null!, null!, null!, NullLogger<SynchronousIdentityPipelineService>.Instance, limiter, pinnedGuard: _guard);

    // Records which stage the pipeline asked for and runs nothing, so a stage whose worker is absent is seen without being reached.
    private sealed class RecordingLimiter : IEnrichmentConcurrencyLimiter
    {
        public List<EnrichmentWorkKind> Kinds { get; } = [];

        public Task RunAsync(EnrichmentWorkKind kind, Func<CancellationToken, Task> operation, CancellationToken ct = default)
        {
            Kinds.Add(kind);
            return Task.CompletedTask;
        }

        public Task<T> RunAsync<T>(EnrichmentWorkKind kind, Func<CancellationToken, Task<T>> operation, CancellationToken ct = default)
        {
            Kinds.Add(kind);
            return Task.FromResult(default(T)!);
        }

        public int GetAvailableSlots(EnrichmentWorkKind kind) => int.MaxValue;
    }

    private async Task<Guid> QueueAsync(Guid entityId)
    {
        var jobId = Guid.NewGuid();
        await _jobs.CreateAsync(new IdentityJob
        {
            Id = jobId,
            EntityId = entityId,
            EntityType = "Work",
            MediaType = "Movies",
            State = nameof(IdentityJobState.Queued),
        });
        return jobId;
    }

    private async Task<Guid> SeedWorkAsync(bool pinned)
    {
        var workId = await new WorkRepository(_db).InsertStandaloneAsync(MediaType.Movies);
        if (pinned)
        {
            await _canonicals.UpsertBatchAsync(
            [
                new CanonicalValue
                {
                    EntityId = workId,
                    Key = MetadataFieldConstants.MatchPinned,
                    Value = "true",
                    LastScoredAt = DateTimeOffset.UtcNow,
                    WinningProviderId = WellKnownProviders.UserManual,
                },
            ]);
        }

        return workId;
    }
}
