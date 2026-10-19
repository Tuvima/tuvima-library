using MediaEngine.Api.Services;
using MediaEngine.Contracts.Ingestion;
using MediaEngine.Domain;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Ingestion;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Api.Tests;

public sealed class IngestionQueueProgressServiceTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task NothingHappening_PublishesNothing()
    {
        var harness = new Harness();

        await harness.RefreshAsync();
        await harness.RefreshAsync();

        Assert.Empty(harness.Publisher.Published);
        Assert.Equal("idle", harness.Service.Current.State);
    }

    [Fact]
    public async Task ChangedNumbers_ArePublishedOnce_AndUnchangedNumbersAreNot()
    {
        var harness = new Harness();
        harness.Repository.Rows = [Working("a", MediaOperationStage.Hashing)];

        await harness.RefreshAsync();
        harness.Advance(1);
        await harness.RefreshAsync();

        var first = Assert.Single(harness.Publisher.Published);
        Assert.Equal(SignalREvents.IngestionQueueProgress, first.EventName);
        var dto = Assert.IsType<IngestionQueueProgressDto>(first.Payload);
        Assert.Equal("working", dto.State);
        Assert.Equal(1, dto.FilesFound);

        harness.Repository.Rows = [Working("a", MediaOperationStage.Scoring)];
        harness.Advance(1);
        await harness.RefreshAsync();

        Assert.Equal(2, harness.Publisher.Published.Count);
    }

    [Fact]
    public async Task WhenTheLastFileFinishes_ThePublishHappensOnceForTheTransitionToIdle()
    {
        var harness = new Harness();
        harness.Repository.Rows = [Working("a", MediaOperationStage.Hashing)];
        await harness.RefreshAsync();

        harness.Repository.Rows = [Finished("a")];
        harness.Advance(1);
        await harness.RefreshAsync();
        harness.Advance(5);
        await harness.RefreshAsync();
        harness.Advance(5);
        await harness.RefreshAsync();

        Assert.Equal(2, harness.Publisher.Published.Count);
        var idle = Assert.IsType<IngestionQueueProgressDto>(harness.Publisher.Published[1].Payload);
        Assert.Equal("idle", idle.State);
        Assert.Equal(1d, idle.Progress);
        Assert.Equal(1, idle.FilesFound);
        Assert.Equal(1, idle.Outcomes.Added);
    }

    [Fact]
    public async Task TheFirstSnapshotOfAPeriodAlreadyCountsFilesThatFinishedBeforeItWasSeen()
    {
        var harness = new Harness();
        harness.Repository.Rows = [Finished("done-1"), Finished("done-2"), Working("busy", MediaOperationStage.Hashing)];

        await harness.RefreshAsync();

        var dto = Assert.IsType<IngestionQueueProgressDto>(Assert.Single(harness.Publisher.Published).Payload);
        Assert.Equal(3, dto.FilesFound);
        Assert.Equal(2, dto.FilesDone);
        Assert.Equal(0, dto.FilesMoreFound);
        Assert.Equal(2, harness.Repository.Reads.Count);
        Assert.Null(harness.Repository.Reads[0]);
        Assert.NotNull(harness.Repository.Reads[1]);
    }

    [Fact]
    public async Task LooksEverySecondWhileBusyAndLessOftenWhileIdle()
    {
        var harness = new Harness();

        var idleDelay = await harness.RefreshAsync();
        harness.Repository.Rows = [Working("a", MediaOperationStage.Hashing)];
        var busyDelay = await harness.RefreshAsync();

        Assert.Equal(TimeSpan.FromSeconds(5), idleDelay);
        Assert.Equal(TimeSpan.FromSeconds(1), busyDelay);
    }

    [Fact]
    public async Task CurrentIsTheNewestSnapshotInTheWireShape()
    {
        var harness = new Harness();
        harness.Repository.Rows = [Working("a", MediaOperationStage.Scoring), Working("b", MediaOperationStage.Scoring)];

        await harness.RefreshAsync();

        var current = harness.Service.Current;
        Assert.Equal("working", current.State);
        Assert.Equal(2, current.FilesFound);
        Assert.Equal(2L * 9, current.StepsTotal);
        Assert.Equal(2L * 3, current.StepsCompleted);
    }

    [Fact]
    public async Task APublishThatFails_IsRetriedOnTheNextLook()
    {
        var harness = new Harness();
        harness.Repository.Rows = [Working("a", MediaOperationStage.Hashing)];
        harness.Publisher.FailTimes = 1;

        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.RefreshAsync());
        harness.Advance(1);
        await harness.RefreshAsync();

        Assert.Single(harness.Publisher.Published);
    }

    [Fact]
    public async Task StoppingTheService_EndsTheLoopCleanly()
    {
        var harness = new Harness();
        using var cts = new CancellationTokenSource();

        await harness.Service.StartAsync(cts.Token);
        await cts.CancelAsync();
        await harness.Service.StopAsync(CancellationToken.None);

        Assert.True(harness.Service.ExecuteTask is null or { IsCompleted: true });
    }

    private static IngestionQueueFileRow Working(string key, string stage) => Row(key, stage, MediaOperationStatus.Running);

    private static IngestionQueueFileRow Finished(string key) =>
        Row(key, MediaOperationStage.Completed, MediaOperationStatus.Succeeded);

    private static IngestionQueueFileRow Row(string key, string stage, string status) => new(
        Guid.Parse(CreateGuid(key)),
        Start,
        Start,
        stage,
        status,
        null,
        null,
        null,
        null,
        "Movies",
        null,
        false);

    private static string CreateGuid(string key)
    {
        var bytes = new byte[16];
        System.Text.Encoding.UTF8.GetBytes(key).AsSpan(0, Math.Min(key.Length, 16)).CopyTo(bytes);
        return new Guid(bytes).ToString();
    }

    private sealed class Harness
    {
        private readonly ManualTime _time = new(Start);

        public Harness()
        {
            Service = new IngestionQueueProgressService(
                Repository,
                Publisher,
                _time,
                NullLogger<IngestionQueueProgressService>.Instance);
        }

        public IngestionQueueProgressService Service { get; }

        public SpyRepository Repository { get; } = new();

        public RecordingPublisher Publisher { get; } = new();

        public Task<TimeSpan> RefreshAsync() => Service.RefreshAsync(CancellationToken.None);

        public void Advance(int seconds) => _time.Advance(TimeSpan.FromSeconds(seconds));
    }

    private sealed class SpyRepository : IIngestionQueueProgressRepository
    {
        public IReadOnlyList<IngestionQueueFileRow> Rows { get; set; } = [];

        public List<DateTimeOffset?> Reads { get; } = [];

        public Task<IReadOnlyList<IngestionQueueFileRow>> ReadAsync(DateTimeOffset? since, CancellationToken ct = default)
        {
            Reads.Add(since);
            return Task.FromResult(Rows);
        }
    }

    private sealed class RecordingPublisher : IEventPublisher
    {
        public List<(string EventName, object Payload)> Published { get; } = [];

        public int FailTimes { get; set; }

        public Task PublishAsync<TPayload>(string eventName, TPayload payload, CancellationToken ct = default)
            where TPayload : notnull
        {
            if (FailTimes > 0)
            {
                FailTimes--;
                throw new InvalidOperationException("The hub is not reachable.");
            }

            Published.Add((eventName, payload));
            return Task.CompletedTask;
        }
    }

    private sealed class ManualTime(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
