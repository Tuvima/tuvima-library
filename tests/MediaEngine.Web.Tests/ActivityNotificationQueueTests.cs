using MediaEngine.Contracts.Ingestion;
using MediaEngine.Web.Services.Integration;

namespace MediaEngine.Web.Tests;

public sealed class ActivityNotificationQueueTests
{
    [Fact]
    public void QuietPlaybackCoalescesStartAndCompletionWithoutReplayingHistory()
    {
        var queue = new ActivityNotificationQueue();
        queue.Observe(Snapshot("idle"));
        queue.Observe(Snapshot("working"));
        Assert.Null(queue.Take(true, DateTimeOffset.UtcNow));
        queue.Observe(Snapshot("idle", needsReview: 2));
        var delivered = queue.Take(false, DateTimeOffset.UtcNow);
        Assert.Equal("Library update complete; some items need review", delivered?.Message);
        Assert.Null(queue.Take(false, DateTimeOffset.UtcNow.AddMinutes(1)));
        queue.Observe(Snapshot("idle", needsReview: 2));
        Assert.Null(queue.Take(false, DateTimeOffset.UtcNow.AddMinutes(2)));
    }

    [Fact]
    public void WorkAlreadyUnderWayWhenThePageLoadsIsSilentUntilItFinishes()
    {
        var queue = new ActivityNotificationQueue();
        queue.Observe(Snapshot("working"));
        Assert.Null(queue.Take(false, DateTimeOffset.UtcNow));
        queue.Observe(Snapshot("working"));
        Assert.Null(queue.Take(false, DateTimeOffset.UtcNow));
        queue.Observe(Snapshot("idle"));
        Assert.Equal("Library update complete", queue.Take(false, DateTimeOffset.UtcNow)?.Message);
    }

    [Fact]
    public void AStartIsAnnouncedOnceAndAFailureNeedsAttention()
    {
        var queue = new ActivityNotificationQueue();
        queue.Observe(Snapshot("idle"));
        queue.Observe(Snapshot("working"));
        var start = queue.Take(false, DateTimeOffset.UtcNow);
        Assert.Equal("Library update started", start?.Message);
        Assert.False(start?.NeedsAttention);
        queue.Observe(Snapshot("working"));
        Assert.Null(queue.Take(false, DateTimeOffset.UtcNow.AddMinutes(1)));
        queue.Observe(Snapshot("idle", failed: 1));
        var finish = queue.Take(false, DateTimeOffset.UtcNow.AddMinutes(2));
        Assert.Equal("Library update needs attention", finish?.Message);
        Assert.True(finish?.NeedsAttention);
    }

    [Fact]
    public void AnUndeliveredStartIsReplacedByTheCompletionThatFollowsIt()
    {
        var queue = new ActivityNotificationQueue();
        queue.Observe(Snapshot("idle"));
        queue.Observe(Snapshot("working"));
        queue.Observe(Snapshot("idle"));
        Assert.Equal("Library update complete", queue.Take(false, DateTimeOffset.UtcNow)?.Message);
        Assert.Equal(2, queue.History.Count);
    }

    [Fact]
    public void ARunThatStartsAndFinishesBetweenTwoLooks_StillGetsItsCompletion()
    {
        var queue = new ActivityNotificationQueue();
        var earlier = DateTimeOffset.UtcNow.AddHours(-1);
        queue.Observe(Snapshot("idle", startedAt: earlier));
        queue.Observe(Snapshot("idle", startedAt: earlier.AddMinutes(55)));
        Assert.Equal("Library update complete", queue.Take(false, DateTimeOffset.UtcNow)?.Message);
        queue.Observe(Snapshot("idle", startedAt: earlier.AddMinutes(55)));
        Assert.Null(queue.Take(false, DateTimeOffset.UtcNow.AddMinutes(1)));
    }

    [Fact]
    public void WaitingForAToolNeitherCompletesARunNorStartsANewOne()
    {
        var queue = new ActivityNotificationQueue();
        queue.Observe(Snapshot("idle"));
        queue.Observe(Snapshot("working"));
        Assert.Equal("Library update started", queue.Take(false, DateTimeOffset.UtcNow)?.Message);
        queue.Observe(Snapshot("waiting"));
        queue.Observe(Snapshot("working"));
        Assert.Null(queue.Take(false, DateTimeOffset.UtcNow.AddMinutes(1)));
        queue.Observe(Snapshot("waiting"));
        queue.Observe(Snapshot("idle"));
        Assert.Equal("Library update complete", queue.Take(false, DateTimeOffset.UtcNow.AddMinutes(2))?.Message);
    }

    [Fact]
    public void ToolsMissingWithNothingToDo_IsSilent()
    {
        var queue = new ActivityNotificationQueue();
        queue.Observe(Snapshot("idle"));
        queue.Observe(Snapshot("waiting"));
        queue.Observe(Snapshot("idle"));
        Assert.Null(queue.Take(false, DateTimeOffset.UtcNow));
        Assert.Empty(queue.History);
    }

    private static IngestionQueueProgressDto Snapshot(string state, int needsReview = 0, int failed = 0, DateTimeOffset? startedAt = null) => new()
    {
        State = state,
        StartedAt = startedAt,
        Outcomes = new IngestionQueueOutcomesDto { NeedsReview = needsReview, Failed = failed },
    };
}
