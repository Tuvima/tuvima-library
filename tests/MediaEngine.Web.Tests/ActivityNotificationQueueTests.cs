using MediaEngine.Contracts.Ingestion;
using MediaEngine.Web.Services.Integration;

namespace MediaEngine.Web.Tests;

public sealed class ActivityNotificationQueueTests
{
    [Fact]
    public void QuietPlaybackCoalescesStartAndCompletionWithoutReplayingHistory()
    {
        var queue = new ActivityNotificationQueue();
        var id = Guid.NewGuid();
        queue.Observe([]);
        queue.Observe([new() { BatchId = id, Status = "running" }]);
        Assert.Null(queue.Take(true, DateTimeOffset.UtcNow));
        queue.Observe([new() { BatchId = id, Status = "completed", ReviewCount = 2 }]);
        var delivered = queue.Take(false, DateTimeOffset.UtcNow);
        Assert.Equal("Library update complete; some items need review", delivered?.Message);
        Assert.Null(queue.Take(false, DateTimeOffset.UtcNow.AddMinutes(1)));
        queue.Observe([new() { BatchId = id, Status = "completed", ReviewCount = 2 }]);
        Assert.Null(queue.Take(false, DateTimeOffset.UtcNow.AddMinutes(2)));
    }

    [Fact]
    public void OutstandingWorkPreventsPrematureCompletionAndInitialHistoryIsSilent()
    {
        var queue = new ActivityNotificationQueue();
        var id = Guid.NewGuid();
        queue.Observe([new() { BatchId = id, Status = "completed", OutstandingOperations = 1 }]);
        Assert.Null(queue.Take(false, DateTimeOffset.UtcNow));
        queue.Observe([new() { BatchId = id, Status = "completed" }]);
        Assert.Equal("Library update complete", queue.Take(false, DateTimeOffset.UtcNow)?.Message);
    }
}
