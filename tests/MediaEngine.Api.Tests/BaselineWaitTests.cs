using MediaEngine.Api.DevSupport;

namespace MediaEngine.Api.Tests;

public sealed class BaselineWaitTests
{
    private static IngestionSnapshot Snapshot(int assets = 10, int claims = 100, int derived = 500, int jobs = 0, int operations = 0, int viewFiles = 0, string states = "Ready:10") =>
        new(assets, viewFiles, Works: assets, ResolvedWorks: assets, PendingWorks: 0, claims, Derived: derived, jobs, operations, ReviewItems: 0, states);

    [Fact]
    public void Observe_SettlesAfterThreeIdenticalSnapshotsWithNoActiveWork()
    {
        var tracker = new IngestionSettleTracker(expectedAssets: 10);

        Assert.False(tracker.Observe(Snapshot()));
        Assert.False(tracker.Observe(Snapshot()));
        Assert.True(tracker.Observe(Snapshot()));
    }

    [Fact]
    public void Observe_StartsCountingAgainWhenAnythingMoves()
    {
        var tracker = new IngestionSettleTracker(expectedAssets: 10);
        tracker.Observe(Snapshot());
        tracker.Observe(Snapshot());

        Assert.False(tracker.Observe(Snapshot(claims: 101)));
        Assert.False(tracker.Observe(Snapshot(claims: 101)));
        Assert.True(tracker.Observe(Snapshot(claims: 101)));
    }

    [Fact]
    public void Observe_KeepsWaitingWhileDerivedWorkLikePeopleAndArtworkIsStillGrowing()
    {
        var tracker = new IngestionSettleTracker(expectedAssets: 10);

        for (var derived = 500; derived < 520; derived++)
        {
            Assert.False(tracker.Observe(Snapshot(derived: derived)));
        }

        Assert.False(tracker.Observe(Snapshot(derived: 520)));
        Assert.False(tracker.Observe(Snapshot(derived: 520)));
        Assert.True(tracker.Observe(Snapshot(derived: 520)));
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(0, 1)]
    public void Observe_NeverSettlesWhileAnIdentityJobOrOperationIsStillRunning(int jobs, int operations)
    {
        var tracker = new IngestionSettleTracker(expectedAssets: 10);

        for (var i = 0; i < 50; i++)
        {
            Assert.False(tracker.Observe(Snapshot(jobs: jobs, operations: operations)));
        }
    }

    [Fact]
    public void Observe_DoesNotSettleOnAnEmptyLibraryBeforeAnyFileIsRegistered()
    {
        var tracker = new IngestionSettleTracker(expectedAssets: 0);

        for (var i = 0; i < 10; i++)
        {
            Assert.False(tracker.Observe(Snapshot(assets: 0, viewFiles: 0, states: string.Empty)));
        }
    }

    [Fact]
    public void Observe_WaitsForTheExpectedFilesUnlessNothingHasMovedForAWhile()
    {
        var tracker = new IngestionSettleTracker(expectedAssets: 12);

        for (var i = 1; i < IngestionSettleTracker.ExpectationGiveUpSnapshots; i++)
        {
            Assert.False(tracker.Observe(Snapshot(assets: 10)));
        }

        // Two files the Engine never registers (duplicates, unsupported) must not hold a run forever.
        Assert.True(tracker.Observe(Snapshot(assets: 10)));
    }

    [Fact]
    public void Observe_CountsViewOnlyLibrariesAsRegistered()
    {
        var tracker = new IngestionSettleTracker(expectedAssets: 0);

        tracker.Observe(Snapshot(assets: 0, viewFiles: 14));
        tracker.Observe(Snapshot(assets: 0, viewFiles: 14));

        Assert.True(tracker.Observe(Snapshot(assets: 0, viewFiles: 14)));
    }

    [Fact]
    public void SettleQueries_TreatReviewAndNoMatchStatesAsFinishedButNotAnInFlightStage()
    {
        Assert.Contains("'QidNeedsReview'", IngestionSettleQueries.ActiveJobsThroughStage3, StringComparison.Ordinal);
        Assert.Contains("'RetailNoMatch'", IngestionSettleQueries.ActiveJobsThroughStage3, StringComparison.Ordinal);
        Assert.DoesNotContain("'Hydrating'", IngestionSettleQueries.ActiveJobsThroughStage3, StringComparison.Ordinal);
        Assert.DoesNotContain("'RetailMatchedNeedsReview'", IngestionSettleQueries.ActiveJobsThroughStage3, StringComparison.Ordinal);
    }
}
