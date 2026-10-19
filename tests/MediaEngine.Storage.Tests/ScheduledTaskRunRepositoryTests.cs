using Dapper;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Storage.Tests;

public sealed class ScheduledTaskRunRepositoryTests : IDisposable
{
    private readonly string _dbPath;
    private readonly DatabaseConnection _db;
    private readonly ScheduledTaskRunRepository _runs;

    public ScheduledTaskRunRepositoryTests()
    {
        DapperConfiguration.Configure();
        _dbPath = Path.Combine(Path.GetTempPath(), $"tuvima_task_runs_{Guid.NewGuid():N}.db");
        _db = new DatabaseConnection(_dbPath);
        _db.InitializeSchema();
        _db.RunStartupChecks();
        _runs = new ScheduledTaskRunRepository(_db);
    }

    public void Dispose()
    {
        _db.Dispose();
        File.Delete(_dbPath);
    }

    [Fact]
    public async Task StartThenFinish_RoundTripsTheRunWithItsOutcomeAndCount()
    {
        var started = new DateTimeOffset(2026, 10, 19, 3, 0, 0, TimeSpan.Zero);
        var finished = started.AddMinutes(4);

        var id = await _runs.StartAsync("vibe_tags", null, started);
        var whileRunning = await _runs.GetAsync(id);
        await _runs.FinishAsync(id, ScheduledTaskOutcomes.Succeeded, finished, 12, "Tagged 12 items.");
        var after = await _runs.GetAsync(id);

        Assert.NotNull(whileRunning);
        Assert.Equal(ScheduledTaskOutcomes.Running, whileRunning.Outcome);
        Assert.Null(whileRunning.FinishedAt);
        Assert.NotNull(after);
        Assert.Equal("vibe_tags", after.TaskKey);
        Assert.Null(after.PluginId);
        Assert.Equal(started, after.StartedAt);
        Assert.Equal(finished, after.FinishedAt);
        Assert.Equal(ScheduledTaskOutcomes.Succeeded, after.Outcome);
        Assert.Equal(12, after.ItemsProcessed);
        Assert.Equal("Tagged 12 items.", after.Message);
    }

    [Fact]
    public async Task GetLatestPerTask_ReturnsTheNewestRunOfEachTask()
    {
        var start = new DateTimeOffset(2026, 10, 19, 3, 0, 0, TimeSpan.Zero);
        var oldVibe = await _runs.StartAsync("vibe_tags", null, start);
        await _runs.FinishAsync(oldVibe, ScheduledTaskOutcomes.Failed, start.AddMinutes(1), null, "Model missing.");
        var newVibe = await _runs.StartAsync("vibe_tags", null, start.AddDays(1));
        await _runs.FinishAsync(newVibe, ScheduledTaskOutcomes.Succeeded, start.AddDays(1).AddMinutes(1), 3, null);
        var series = await _runs.StartAsync("series_order", null, start.AddHours(5));
        await _runs.FinishAsync(series, ScheduledTaskOutcomes.Skipped, start.AddHours(5), null, "Series order is off");

        var latest = await _runs.GetLatestPerTaskAsync();

        Assert.Equal(2, latest.Count);
        Assert.Equal(newVibe, latest["vibe_tags"].Id);
        Assert.Equal(series, latest["series_order"].Id);
        Assert.Equal("Series order is off", latest["series_order"].Message);
    }

    [Fact]
    public async Task ListRecent_PagesNewestFirst_AndCanBeLimitedToOnePlugin()
    {
        var start = new DateTimeOffset(2026, 10, 19, 2, 0, 0, TimeSpan.Zero);
        var first = await _runs.StartAsync("plugin_segment_detection", "intro-skip", start);
        var second = await _runs.StartAsync("plugin_segment_detection", "commercial-skip", start.AddMinutes(1));
        var third = await _runs.StartAsync("plugin_segment_detection", "intro-skip", start.AddMinutes(2));
        await _runs.StartAsync("vibe_tags", null, start.AddMinutes(3));

        var all = await _runs.ListRecentAsync("plugin_segment_detection", limit: 2);
        var nextPage = await _runs.ListRecentAsync("plugin_segment_detection", limit: 2, skip: 2);
        var introOnly = await _runs.ListRecentAsync("plugin_segment_detection", limit: 10, pluginId: "intro-skip");

        Assert.Equal([third, second], all.Select(run => run.Id));
        Assert.Equal([first], nextPage.Select(run => run.Id));
        Assert.Equal([third, first], introOnly.Select(run => run.Id));
    }

    [Fact]
    public async Task MarkRunningAsInterrupted_ClosesEveryRunStillRunning_AndLeavesFinishedRunsAlone()
    {
        var start = new DateTimeOffset(2026, 10, 19, 3, 0, 0, TimeSpan.Zero);
        var stillRunning = await _runs.StartAsync("vibe_tags", null, start);
        var alsoRunning = await _runs.StartAsync("storage_maintenance", null, start);
        var done = await _runs.StartAsync("series_order", null, start);
        await _runs.FinishAsync(done, ScheduledTaskOutcomes.Succeeded, start.AddMinutes(1), 1, null);
        var restart = start.AddHours(1);

        var changed = await _runs.MarkRunningAsInterruptedAsync(restart);

        Assert.Equal(2, changed);
        foreach (var id in new[] { stillRunning, alsoRunning })
        {
            var run = await _runs.GetAsync(id);
            Assert.NotNull(run);
            Assert.Equal(ScheduledTaskOutcomes.Interrupted, run.Outcome);
            Assert.Equal(restart, run.FinishedAt);
            Assert.False(string.IsNullOrWhiteSpace(run.Message));
        }

        var finished = await _runs.GetAsync(done);
        Assert.NotNull(finished);
        Assert.Equal(ScheduledTaskOutcomes.Succeeded, finished.Outcome);
    }

    [Fact]
    public async Task Finish_DropsThatTasksRunsOlderThanNinetyDays_ButKeepsOtherTasksAndRunningRuns()
    {
        var now = new DateTimeOffset(2026, 10, 19, 3, 0, 0, TimeSpan.Zero);
        var tooOld = await _runs.StartAsync("vibe_tags", null, now.AddDays(-91));
        await _runs.FinishAsync(tooOld, ScheduledTaskOutcomes.Succeeded, now.AddDays(-91).AddMinutes(1), 1, null);
        var oldButOtherTask = await _runs.StartAsync("series_order", null, now.AddDays(-120));
        await _runs.FinishAsync(oldButOtherTask, ScheduledTaskOutcomes.Succeeded, now.AddDays(-120), 1, null);
        var recent = await _runs.StartAsync("vibe_tags", null, now.AddDays(-89));
        await _runs.FinishAsync(recent, ScheduledTaskOutcomes.Succeeded, now.AddDays(-89), 1, null);
        var stuck = await _runs.StartAsync("vibe_tags", null, now.AddDays(-200));

        var current = await _runs.StartAsync("vibe_tags", null, now);
        await _runs.FinishAsync(current, ScheduledTaskOutcomes.Succeeded, now, 2, null);

        Assert.Null(await _runs.GetAsync(tooOld));
        Assert.NotNull(await _runs.GetAsync(oldButOtherTask));
        Assert.NotNull(await _runs.GetAsync(recent));
        Assert.NotNull(await _runs.GetAsync(stuck));
        Assert.NotNull(await _runs.GetAsync(current));
    }

    [Fact]
    public async Task Finish_KeepsAtMostFiveHundredRunsPerTask_AndTheNewestSurvive()
    {
        var start = new DateTimeOffset(2026, 10, 19, 0, 0, 0, TimeSpan.Zero);
        using (var conn = _db.CreateConnection())
        {
            using var tx = conn.BeginTransaction();
            for (var i = 0; i < 520; i++)
            {
                conn.Execute(
                    """
                    INSERT INTO scheduled_task_runs (id, task_key, started_at, finished_at, outcome)
                    VALUES (@id, 'description_insights', @at, @at, 'succeeded');
                    """,
                    new { id = Guid.NewGuid(), at = start.AddMinutes(i).UtcDateTime.ToString("O") },
                    tx);
            }

            tx.Commit();
        }

        var newest = await _runs.StartAsync("description_insights", null, start.AddMinutes(520));
        await _runs.FinishAsync(newest, ScheduledTaskOutcomes.Succeeded, start.AddMinutes(520), 0, null);

        using var check = _db.CreateConnection();
        Assert.Equal(500, check.ExecuteScalar<int>("SELECT COUNT(*) FROM scheduled_task_runs WHERE task_key = 'description_insights';"));
        var recent = await _runs.ListRecentAsync("description_insights", limit: 500);
        Assert.Equal(newest, recent[0].Id);
        Assert.Equal(start.AddMinutes(21), recent[^1].StartedAt);
    }

    [Fact]
    public async Task History_SurvivesANewRepositoryOnTheSameDataStore()
    {
        var start = new DateTimeOffset(2026, 10, 19, 2, 0, 0, TimeSpan.Zero);
        var id = await _runs.StartAsync("plugin_segment_detection", "intro-skip", start);
        await _runs.FinishAsync(id, ScheduledTaskOutcomes.Succeeded, start.AddMinutes(9), 25, "No playback segments detected.");

        _db.Dispose();
        using var reopened = new DatabaseConnection(_dbPath);
        reopened.InitializeSchema();
        reopened.RunStartupChecks();
        var afterRestart = new ScheduledTaskRunRepository(reopened);
        var run = await afterRestart.GetAsync(id);
        var recent = await afterRestart.ListRecentAsync("plugin_segment_detection", 10, pluginId: "intro-skip");

        Assert.NotNull(run);
        Assert.Equal("intro-skip", run.PluginId);
        Assert.Equal(25, run.ItemsProcessed);
        Assert.Equal([id], recent.Select(r => r.Id));
    }
}
