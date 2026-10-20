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

    private static IngestionQueueProgressDto Snapshot(string state, int needsReview = 0, int failed = 0) => new()
    {
        State = state,
        Outcomes = new IngestionQueueOutcomesDto { NeedsReview = needsReview, Failed = failed },
    };
}
