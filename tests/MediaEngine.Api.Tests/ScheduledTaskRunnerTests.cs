using MediaEngine.Api.Services.ScheduledTasks;
using MediaEngine.Domain.Contracts;
using MediaEngine.Storage.Contracts;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Api.Tests;

public sealed class ScheduledTaskRunnerTests
{
    [Fact]
    public async Task RunAsync_WhenTheBodySucceeds_RecordsASucceededRunWithItsCountAndMessage()
    {
        var runs = new SpyRunRepository();
        var runner = new ScheduledTaskRunner(runs, NullLogger<ScheduledTaskRunner>.Instance);

        var run = await runner.RunAsync(
            "vibe_tags",
            _ => Task.FromResult(TaskRunResult.Succeeded(7, "Tagged 7 items.")),
            CancellationToken.None);

        var stored = Assert.Single(runs.Finished);
        Assert.Equal("vibe_tags", stored.TaskKey);
        Assert.Equal(ScheduledTaskOutcomes.Succeeded, stored.Outcome);
        Assert.Equal(7, stored.ItemsProcessed);
        Assert.Equal("Tagged 7 items.", stored.Message);
        Assert.NotNull(stored.FinishedAt);
        Assert.Equal(stored.Id, run.Id);
        Assert.Equal(ScheduledTaskOutcomes.Succeeded, run.Outcome);
        Assert.Empty(runs.StillRunning());
    }

    [Fact]
    public async Task RecordAsync_ForWorkOutsideTheEngineProject_RecordsASucceededRunWithItsItemCount()
    {
        var runs = new SpyRunRepository();
        IRecurringTaskRecorder recorder = new ScheduledTaskRunner(runs, NullLogger<ScheduledTaskRunner>.Instance);

        await recorder.RecordAsync("pass2_nightly_sweep", _ => Task.FromResult<int?>(3), CancellationToken.None);

        var stored = Assert.Single(runs.Finished);
        Assert.Equal("pass2_nightly_sweep", stored.TaskKey);
        Assert.Equal(ScheduledTaskOutcomes.Succeeded, stored.Outcome);
        Assert.Equal(3, stored.ItemsProcessed);
    }

    [Fact]
    public async Task RunAsync_WhenTheBodySkips_RecordsTheReasonAsASkippedRunNotAFailure()
    {
        var runs = new SpyRunRepository();
        var runner = new ScheduledTaskRunner(runs, NullLogger<ScheduledTaskRunner>.Instance);

        await runner.RunAsync(
            "vibe_tags",
            _ => Task.FromResult(TaskRunResult.Skipped("Vibe tags is off")),
            CancellationToken.None);

        var stored = Assert.Single(runs.Finished);
        Assert.Equal(ScheduledTaskOutcomes.Skipped, stored.Outcome);
        Assert.Equal("Vibe tags is off", stored.Message);
        Assert.Null(stored.ItemsProcessed);
    }

    [Fact]
    public async Task RunAsync_WhenTheBodyThrows_RecordsAFailureWithTheMessage_AndRethrowsForTheLoopToLog()
    {
        var runs = new SpyRunRepository();
        var runner = new ScheduledTaskRunner(runs, NullLogger<ScheduledTaskRunner>.Instance);

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync(
            "storage_maintenance",
            _ => throw new InvalidOperationException("The disk is full."),
            CancellationToken.None));

        Assert.Equal("The disk is full.", thrown.Message);
        var stored = Assert.Single(runs.Finished);
        Assert.Equal(ScheduledTaskOutcomes.Failed, stored.Outcome);
        Assert.Equal("The disk is full.", stored.Message);
        Assert.Empty(runs.StillRunning());
    }

    [Fact]
    public async Task RunAsync_WhenTheEngineIsShuttingDown_RecordsTheRunAsInterruptedAndRethrows()
    {
        var runs = new SpyRunRepository();
        var runner = new ScheduledTaskRunner(runs, NullLogger<ScheduledTaskRunner>.Instance);
        using var shutdown = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.RunAsync(
            "series_order",
            async ct =>
            {
                await shutdown.CancelAsync();
                ct.ThrowIfCancellationRequested();
                return TaskRunResult.Succeeded();
            },
            shutdown.Token));

        var stored = Assert.Single(runs.Finished);
        Assert.Equal(ScheduledTaskOutcomes.Interrupted, stored.Outcome);
    }

    [Fact]
    public async Task RunAsync_WhenTheBodyHandlesItsOwnError_RecordsItAsFailedWithoutThrowing()
    {
        var runs = new SpyRunRepository();
        var runner = new ScheduledTaskRunner(runs, NullLogger<ScheduledTaskRunner>.Instance);

        var run = await runner.RunAsync(
            "plugin_segment_detection",
            _ => Task.FromResult(TaskRunResult.Failed("ffmpeg is missing", 3)),
            CancellationToken.None,
            pluginId: "tuvima.intro-skip");

        var stored = Assert.Single(runs.Finished);
        Assert.Equal(ScheduledTaskOutcomes.Failed, stored.Outcome);
        Assert.Equal("tuvima.intro-skip", stored.PluginId);
        Assert.Equal(3, stored.ItemsProcessed);
        Assert.Equal("tuvima.intro-skip", run.PluginId);
    }

    [Fact]
    public async Task RunAsync_WhenTheRunRecordCannotBeWritten_StillRunsTheBodyAndReturnsItsResult()
    {
        var runner = new ScheduledTaskRunner(new BrokenRunRepository(), NullLogger<ScheduledTaskRunner>.Instance);
        var ran = false;

        var run = await runner.RunAsync(
            "activity_pruning",
            _ =>
            {
                ran = true;
                return Task.FromResult(TaskRunResult.Succeeded(2));
            },
            CancellationToken.None);

        Assert.True(ran);
        Assert.Equal(ScheduledTaskOutcomes.Succeeded, run.Outcome);
        Assert.Equal(2, run.ItemsProcessed);
    }

    /// <summary>Keeps runs in memory the way the data store would.</summary>
    private sealed class SpyRunRepository : IScheduledTaskRunRepository
    {
        private readonly List<ScheduledTaskRun> _runs = [];

        public IReadOnlyList<ScheduledTaskRun> Finished => _runs.Where(run => run.FinishedAt is not null).ToList();

        public IReadOnlyList<ScheduledTaskRun> StillRunning() =>
            _runs.Where(run => run.Outcome == ScheduledTaskOutcomes.Running).ToList();

        public Task<Guid> StartAsync(string taskKey, string? pluginId, DateTimeOffset startedAt, CancellationToken ct = default)
        {
            var run = new ScheduledTaskRun(Guid.NewGuid(), taskKey, pluginId, startedAt, null, ScheduledTaskOutcomes.Running, null, null);
            _runs.Add(run);
            return Task.FromResult(run.Id);
        }

        public Task FinishAsync(
            Guid runId, string outcome, DateTimeOffset finishedAt, int? itemsProcessed, string? message, CancellationToken ct = default)
        {
            var index = _runs.FindIndex(run => run.Id == runId);
            _runs[index] = _runs[index] with
            {
                Outcome = outcome,
                FinishedAt = finishedAt,
                ItemsProcessed = itemsProcessed,
                Message = message,
            };
            return Task.CompletedTask;
        }

        public Task<ScheduledTaskRun?> GetAsync(Guid runId, CancellationToken ct = default) =>
            Task.FromResult(_runs.FirstOrDefault(run => run.Id == runId));

        public Task<IReadOnlyDictionary<string, ScheduledTaskRun>> GetLatestPerTaskAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<string, ScheduledTaskRun>>(
                _runs.GroupBy(run => run.TaskKey).ToDictionary(group => group.Key, group => group.Last()));

        public Task<IReadOnlyList<ScheduledTaskRun>> ListRecentAsync(
            string taskKey, int limit, int skip = 0, string? pluginId = null, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ScheduledTaskRun>>(
                _runs.Where(run => run.TaskKey == taskKey).Reverse().Skip(skip).Take(limit).ToList());

        public Task<int> MarkRunningAsInterruptedAsync(DateTimeOffset now, CancellationToken ct = default) => Task.FromResult(0);

        public Task PruneAsync(string taskKey, DateTimeOffset now, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class BrokenRunRepository : IScheduledTaskRunRepository
    {
        public Task<Guid> StartAsync(string taskKey, string? pluginId, DateTimeOffset startedAt, CancellationToken ct = default) =>
            throw new InvalidOperationException("The data store is unavailable.");

        public Task FinishAsync(
            Guid runId, string outcome, DateTimeOffset finishedAt, int? itemsProcessed, string? message, CancellationToken ct = default) =>
            throw new InvalidOperationException("The data store is unavailable.");

        public Task<ScheduledTaskRun?> GetAsync(Guid runId, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<IReadOnlyDictionary<string, ScheduledTaskRun>> GetLatestPerTaskAsync(CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<ScheduledTaskRun>> ListRecentAsync(
            string taskKey, int limit, int skip = 0, string? pluginId = null, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<int> MarkRunningAsInterruptedAsync(DateTimeOffset now, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task PruneAsync(string taskKey, DateTimeOffset now, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
