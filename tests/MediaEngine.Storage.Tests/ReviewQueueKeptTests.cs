using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Entities;

namespace MediaEngine.Storage.Tests;

/// <summary>
/// "Keep as it is" rows (Needs Review redesign): a row a person kept stops being kept once its problem is solved, and putting one back never
/// leaves two open copies of one question. Real temporary SQLite data store.
/// </summary>
public sealed class ReviewQueueKeptTests : IDisposable
{
    private readonly string _dbPath;
    private readonly DatabaseConnection _db;
    private readonly ReviewQueueRepository _repo;

    public ReviewQueueKeptTests()
    {
        DapperConfiguration.Configure();
        _dbPath = Path.Combine(Path.GetTempPath(), $"tuvima-review-kept-{Guid.NewGuid():N}.db");
        _db = new DatabaseConnection(_dbPath);
        _db.InitializeSchema();
        _db.RunStartupChecks();
        _repo = new ReviewQueueRepository(_db);
    }

    public void Dispose()
    {
        try { _db.Dispose(); } catch { }
        try { File.Delete(_dbPath); } catch { }
    }

    [Fact]
    public async Task ResolvingAnEntitysReviews_AlsoClosesTheRowsAPersonKept_SoTheyStopBeingKept()
    {
        var entity = Guid.NewGuid();
        var kept = await InsertKeptAsync(entity, ReviewTrigger.MetadataConflict);
        var other = await InsertKeptAsync(Guid.NewGuid(), ReviewTrigger.MetadataConflict);

        await _repo.ResolveAllByEntityAsync(entity, "user:editor");

        var closed = (await _repo.GetByIdAsync(kept))!;
        Assert.Equal(ReviewStatus.Resolved, closed.Status);
        Assert.Equal("user:editor", closed.ResolvedBy);
        // Another entity's kept row is untouched.
        Assert.Equal(ReviewStatus.KeptByUser, (await _repo.GetByIdAsync(other))!.ResolvedBy);
        // A closed row can no longer be put back.
        Assert.False(await _repo.ReopenKeptAsync(kept));
    }

    [Fact]
    public async Task ResolvingByTrigger_ClosesOnlyTheKeptRowsOfThoseTriggers()
    {
        var entity = Guid.NewGuid();
        var solved = await InsertKeptAsync(entity, ReviewTrigger.LowConfidence);
        var untouched = await InsertKeptAsync(entity, ReviewTrigger.MetadataConflict);

        await _repo.ResolvePendingByEntityAndTriggersAsync(entity, [ReviewTrigger.LowConfidence], "system:test");
        await _repo.ResolvePendingByEntitiesAndTriggersAsync([entity], [ReviewTrigger.LowConfidence], "system:test");

        Assert.Equal(ReviewStatus.Resolved, (await _repo.GetByIdAsync(solved))!.Status);
        var stillKept = (await _repo.GetByIdAsync(untouched))!;
        Assert.Equal(ReviewStatus.Dismissed, stillKept.Status);
        Assert.Equal(ReviewStatus.KeptByUser, stillKept.ResolvedBy);
    }

    [Fact]
    public async Task ARowTheEngineDismissed_IsNotTouchedWhenTheEntityIsResolved()
    {
        var entity = Guid.NewGuid();
        var byEngine = await InsertPendingAsync(entity, ReviewTrigger.MetadataConflict);
        await _repo.UpdateStatusAsync(byEngine, ReviewStatus.Dismissed, "reconciliation");

        await _repo.ResolveAllByEntityAsync(entity, "user:editor");

        var row = (await _repo.GetByIdAsync(byEngine))!;
        Assert.Equal(ReviewStatus.Dismissed, row.Status);
        Assert.Equal("reconciliation", row.ResolvedBy);
    }

    [Fact]
    public async Task PuttingAKeptRowBack_WhenTheEngineRaisedTheSameQuestionMeanwhile_AnswersFalseAndLeavesItKept()
    {
        var entity = Guid.NewGuid();
        var kept = await InsertKeptAsync(entity, ReviewTrigger.MetadataConflict);
        // The unique index allows one open copy of a question per entity: this is the race with the Engine raising it again.
        await InsertPendingAsync(entity, ReviewTrigger.MetadataConflict);

        Assert.False(await _repo.ReopenKeptAsync(kept));

        var row = (await _repo.GetByIdAsync(kept))!;
        Assert.Equal(ReviewStatus.Dismissed, row.Status);
        Assert.Equal(ReviewStatus.KeptByUser, row.ResolvedBy);
    }

    [Fact]
    public async Task PuttingAKeptRowBack_ReopensItWhenNothingElseIsOpen()
    {
        var kept = await InsertKeptAsync(Guid.NewGuid(), ReviewTrigger.MetadataConflict);

        Assert.True(await _repo.ReopenKeptAsync(kept));

        var row = (await _repo.GetByIdAsync(kept))!;
        Assert.Equal(ReviewStatus.Pending, row.Status);
        Assert.Null(row.ResolvedBy);
    }

    private async Task<Guid> InsertKeptAsync(Guid entityId, string trigger)
    {
        var id = await InsertPendingAsync(entityId, trigger);
        await _repo.UpdateStatusAsync(id, ReviewStatus.Dismissed, ReviewStatus.KeptByUser);
        return id;
    }

    private Task<Guid> InsertPendingAsync(Guid entityId, string trigger) => _repo.InsertAsync(new ReviewQueueEntry
    {
        Id = Guid.NewGuid(),
        EntityId = entityId,
        EntityType = "MediaAsset",
        Trigger = trigger,
        Status = ReviewStatus.Pending,
        Detail = "needs a look",
        CreatedAt = DateTimeOffset.UtcNow,
        ReviewReadyAt = DateTimeOffset.UtcNow,
        AutomationCompletedAt = DateTimeOffset.UtcNow,
    });
}
