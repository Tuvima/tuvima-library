using MediaEngine.Contracts.Ingestion;
using MediaEngine.Web.Services.Integration;

namespace MediaEngine.Web.Tests;

/// <summary>
/// The Dashboard keeps the newest ingestion snapshot the Engine produced, whichever way it arrived (pushed or fetched).
/// </summary>
public sealed class IngestionQueueSnapshotOrderingTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void OlderSnapshotArrivingLater_DoesNotReplaceTheNewerOneAlreadyHeld()
    {
        var state = new UniverseStateContainer();
        state.PushIngestionQueueProgress(Snapshot("working", filesDone: 50, T0.AddSeconds(10)));
        var raised = 0;
        state.IngestionQueueProgressReceived += _ => raised++;

        state.PushIngestionQueueProgress(Snapshot("working", filesDone: 10, T0));

        Assert.Equal(50, state.IngestionQueueProgress!.FilesDone);
        Assert.Equal(0, raised);
    }

    [Fact]
    public void NewerSnapshot_ReplacesTheHeldOneAndIsAnnounced()
    {
        var state = new UniverseStateContainer();
        state.PushIngestionQueueProgress(Snapshot("working", filesDone: 10, T0));
        var raised = 0;
        state.IngestionQueueProgressReceived += _ => raised++;

        state.PushIngestionQueueProgress(Snapshot("idle", filesDone: 60, T0.AddSeconds(5)));

        Assert.Equal("idle", state.IngestionQueueProgress!.State);
        Assert.Equal(1, raised);
    }

    [Fact]
    public void OlderSnapshot_DoesNotRefreshTheReceiveTimeEither()
    {
        var state = new UniverseStateContainer();
        var firstReceivedAt = DateTimeOffset.UtcNow.AddSeconds(-30);
        state.PushIngestionQueueProgress(Snapshot("working", filesDone: 50, T0.AddSeconds(10)), firstReceivedAt);

        state.PushIngestionQueueProgress(Snapshot("working", filesDone: 10, T0), DateTimeOffset.UtcNow);

        Assert.Equal(firstReceivedAt, state.IngestionQueueProgressReceivedAt);
    }

    [Fact]
    public void FirstSnapshot_IsAlwaysKeptAndStampedWithItsReceiveTime()
    {
        var state = new UniverseStateContainer();
        Assert.Null(state.IngestionQueueProgressReceivedAt);

        var before = DateTimeOffset.UtcNow;
        state.PushIngestionQueueProgress(Snapshot("idle", filesDone: 0, T0));

        Assert.NotNull(state.IngestionQueueProgress);
        Assert.True(state.IngestionQueueProgressReceivedAt >= before);
    }

    private static IngestionQueueProgressDto Snapshot(string stateName, int filesDone, DateTimeOffset updatedAt) => new()
    {
        State = stateName,
        FilesFound = 100,
        FilesDone = filesDone,
        Progress = filesDone / 100d,
        UpdatedAt = updatedAt,
    };
}
