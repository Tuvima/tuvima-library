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
/// re-identifies the item. Real temp SQLite; the workers the identity pipeline would call are absent, so reaching them would fail.
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
    public async Task ARescanOfAPinnedItem_WithAFolderHintThatDisagrees_QueuesNoIdentityJob()
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
        var ran = await service.RunSynchronousAsync(new HarvestRequest { EntityId = pinned, EntityType = EntityType.Work, MediaType = MediaType.Movies });

        Assert.Equal(Guid.Empty, queued);
        Assert.Equal(0, ran.TotalClaimsAdded);
        Assert.Null(await _jobs.GetByEntityAsync(pinned));
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
    public async Task AQueuedAutomaticJobForAPinnedItem_IsClosedWithoutAskingAnyProvider_AndOthersGoOn()
    {
        var pinned = await SeedWorkAsync(pinned: true);
        var loose = await SeedWorkAsync(pinned: false);
        var pinnedJob = await QueueAsync(pinned);
        var looseJob = await QueueAsync(loose);

        var leased = await _jobs.LeaseNextAsync("test", [IdentityJobState.Queued], 10, TimeSpan.FromMinutes(5));
        var allowed = await ((IPinnedMatchGuard)_guard).SettlePinnedJobsAsync(_jobs, leased, NullLogger.Instance, CancellationToken.None);

        Assert.Equal([looseJob], allowed.Select(job => job.Id).ToArray());
        Assert.Equal(nameof(IdentityJobState.ReadyWithoutUniverse), (await _jobs.GetByIdAsync(pinnedJob))!.State);
    }

    private SynchronousIdentityPipelineService Pipeline() => new(
        _jobs, _canonicals, null!, null!, null!, NullLogger<SynchronousIdentityPipelineService>.Instance, pinnedGuard: _guard);

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
