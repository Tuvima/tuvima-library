using MediaEngine.Api.Services.Plugins;
using MediaEngine.Api.Services.ScheduledTasks;
using MediaEngine.Domain.Configuration;
using MediaEngine.Plugins;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Api.Tests;

/// <summary>
/// Plugin recurring jobs: they run through the durable run record writer so they appear in Scheduled tasks, they never
/// overlap, at most two run at once, and a job that runs too long is stopped, recorded as failed and eventually paused.
/// </summary>
public sealed class PluginJobSchedulerTests : IDisposable
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan JustOver = TimeSpan.FromMinutes(31);

    private readonly List<IDisposable> _disposables = [];

    private HostServicesHarness NewHost(PluginHostLimits? limits = null, params HostServicesTestPlugin[] plugins)
    {
        var host = new HostServicesHarness(limits, plugins);
        _disposables.Add(host);
        return host;
    }

    private static string KeyOf(HostServicesTestPlugin plugin, TestScheduledJob job) =>
        PluginJobScheduler.TaskKey(plugin.Manifest.Id, job.Key);

    [Fact]
    public async Task ADueJob_RunsThroughTheRunRecordWriter_AndAppearsInScheduledTasksWithThePluginId()
    {
        var plugin = new HostServicesTestPlugin(name: "Reading list");
        var job = plugin.AddJob("refresh", displayName: "Refresh the list");
        var host = NewHost(plugins: plugin);
        host.Enable();
        var scheduler = host.NewScheduler();

        await scheduler.RunDueJobsAsync(CancellationToken.None);
        Assert.Equal(0, job.Runs);

        host.Clock.Advance(JustOver);
        await scheduler.RunDueJobsAsync(CancellationToken.None);
        await scheduler.WhenIdleAsync();

        Assert.Equal(1, job.Runs);
        var runs = await host.Runs.ListRecentAsync(KeyOf(plugin, job), 10);
        var run = Assert.Single(runs);
        Assert.Equal(ScheduledTaskOutcomes.Succeeded, run.Outcome);
        Assert.Equal(plugin.Manifest.Id, run.PluginId);

        var registry = new ScheduledTaskRegistry();
        registry.AddSource(scheduler);
        var list = await ScheduledTaskListBuilder.BuildAsync(registry, host.Runs, host.Clock.GetUtcNow(), CancellationToken.None);
        var task = Assert.Single(list.Tasks);
        Assert.Equal("plugin:test.host-services:refresh", task.Key);
        Assert.Equal("Reading list: Refresh the list", task.Name);
        Assert.Equal("Plugin", task.Owner);
        Assert.Equal(plugin.Manifest.Id, task.PluginId);
        Assert.Equal("every 30 minutes", task.Schedule);
        Assert.Equal(ScheduledTaskOutcomes.Succeeded, task.LastRun!.Outcome);
        Assert.Equal(host.Clock.GetUtcNow() + Interval, task.NextRunAt);
    }

    [Fact]
    public async Task AfterARestart_AnIntervalJobCountsFromItsLastRunRecord_NotFromTheRestart()
    {
        var plugin = new HostServicesTestPlugin();
        var job = plugin.AddJob("refresh");
        var host = NewHost(plugins: plugin);
        host.Enable();
        var scheduler = host.NewScheduler();
        await scheduler.RunDueJobsAsync(CancellationToken.None);
        host.Clock.Advance(JustOver);
        await scheduler.RunDueJobsAsync(CancellationToken.None);
        await scheduler.WhenIdleAsync();

        host.Restart();
        host.Enable();
        var afterRestart = host.NewScheduler();
        await afterRestart.RunDueJobsAsync(CancellationToken.None);

        // The stored run is more than an interval old by the (moved) clock, so the job is due at once. Without the stored
        // record it would have waited a whole interval from the restart.
        await afterRestart.WhenIdleAsync();
        Assert.Equal(2, job.Runs);
        Assert.Equal(2, (await host.Runs.ListRecentAsync(KeyOf(plugin, job), 10)).Count);
    }

    [Fact]
    public async Task ARunStillGoing_IsNeverStartedASecondTime()
    {
        var plugin = new HostServicesTestPlugin();
        var gate = new TaskCompletionSource();
        var job = plugin.AddJob("refresh", body: (_, _) => gate.Task);
        var host = NewHost(plugins: plugin);
        host.Enable();
        var scheduler = host.NewScheduler();
        await scheduler.RunDueJobsAsync(CancellationToken.None);
        host.Clock.Advance(JustOver);
        await scheduler.RunDueJobsAsync(CancellationToken.None);
        await HostServicesHarness.EventuallyAsync(() => job.Runs == 1, "the first run to start");

        host.Clock.Advance(JustOver);
        await scheduler.RunDueJobsAsync(CancellationToken.None);
        host.Clock.Advance(JustOver);
        await scheduler.RunDueJobsAsync(CancellationToken.None);

        Assert.Equal(1, job.Runs);
        Assert.Equal("running", host.Statuses.Get(plugin.Manifest.Id).Describe().Jobs.Single().State);

        gate.SetResult();
        await scheduler.WhenIdleAsync();
        host.Clock.Advance(JustOver);
        await scheduler.RunDueJobsAsync(CancellationToken.None);
        await scheduler.WhenIdleAsync();
        Assert.Equal(2, job.Runs);
        Assert.Equal(1, job.MaxConcurrent);
    }

    [Fact]
    public async Task AtMostTwoPluginJobsRunAtOnce_AndTheThirdWaitsForRoom()
    {
        var plugin = new HostServicesTestPlugin();
        var gate = new TaskCompletionSource();
        var jobs = new[] { "a", "b", "c" }.Select(key => plugin.AddJob(key, body: (_, _) => gate.Task)).ToList();
        var host = NewHost(plugins: plugin);
        host.Enable();
        var scheduler = host.NewScheduler();
        await scheduler.RunDueJobsAsync(CancellationToken.None);
        host.Clock.Advance(JustOver);

        await scheduler.RunDueJobsAsync(CancellationToken.None);
        await HostServicesHarness.EventuallyAsync(() => jobs.Sum(job => job.Runs) == 2, "two jobs to start");
        await Task.Delay(100);
        Assert.Equal(2, jobs.Sum(job => job.Runs));

        gate.SetResult();
        await scheduler.WhenIdleAsync();
        await scheduler.RunDueJobsAsync(CancellationToken.None);
        await scheduler.WhenIdleAsync();
        Assert.All(jobs, job => Assert.Equal(1, job.Runs));
    }

    [Fact]
    public async Task ASlowJob_IsCancelledAtTheLimit_AndRecordedFailedWithAPlainMessage()
    {
        var limits = PluginHostLimits.Default with { JobMaxRunTime = TimeSpan.FromMilliseconds(300), JobCancelGrace = TimeSpan.FromMilliseconds(300) };
        var plugin = new HostServicesTestPlugin();
        CancellationToken seen = default;
        var job = plugin.AddJob("slow", body: async (_, token) =>
        {
            seen = token;
            await Task.Delay(Timeout.Infinite, token);
        });
        var host = NewHost(limits, plugin);
        host.Enable();
        var scheduler = host.NewScheduler();
        await scheduler.RunDueJobsAsync(CancellationToken.None);
        host.Clock.Advance(JustOver);

        await scheduler.RunDueJobsAsync(CancellationToken.None);
        await scheduler.WhenIdleAsync();

        var run = Assert.Single(await host.Runs.ListRecentAsync(KeyOf(plugin, job), 10));
        Assert.Equal(ScheduledTaskOutcomes.Failed, run.Outcome);
        Assert.Equal(PluginHostMessages.JobTooLong(limits), run.Message);
        Assert.True(seen.IsCancellationRequested);
        Assert.Equal("working", host.Statuses.Get(plugin.Manifest.Id).Describe().State);
    }

    [Fact]
    public void WithTheDefaultLimits_ATooLongRunSaysItRanLongerThanTenMinutes()
    {
        Assert.Equal("Stopped: ran longer than 10 minutes", PluginHostMessages.JobTooLong(PluginHostLimits.Default));
        Assert.Equal(2, PluginHostLimits.Default.MaxConcurrentJobs);
        Assert.Equal(3, PluginHostLimits.Default.JobLimitStopsBeforePause);
    }

    [Fact]
    public async Task ThreeTimeLimitStopsInARow_PauseTheJob_TheCardSaysSo_AndSwitchingOffAndOnStartsItAgain()
    {
        var limits = PluginHostLimits.Default with { JobMaxRunTime = TimeSpan.FromMilliseconds(150), JobCancelGrace = TimeSpan.FromMilliseconds(150) };
        var plugin = new HostServicesTestPlugin();
        var job = plugin.AddJob("slow", displayName: "Slow job", body: (_, token) => Task.Delay(Timeout.Infinite, token));
        var host = NewHost(limits, plugin);
        host.Enable();
        var scheduler = host.NewScheduler();
        await scheduler.RunDueJobsAsync(CancellationToken.None);

        for (var i = 0; i < 3; i++)
        {
            host.Clock.Advance(JustOver);
            await scheduler.RunDueJobsAsync(CancellationToken.None);
            await scheduler.WhenIdleAsync();
        }

        Assert.Equal(3, job.Runs);
        var description = host.Statuses.Get(plugin.Manifest.Id).Describe();
        Assert.Equal("needs_attention", description.State);
        Assert.Equal("Slow job paused after running too long 3 times", description.Problem);

        host.Clock.Advance(JustOver);
        await scheduler.RunDueJobsAsync(CancellationToken.None);
        await scheduler.WhenIdleAsync();
        Assert.Equal(3, job.Runs);
        Assert.Equal("paused", host.Statuses.Get(plugin.Manifest.Id).Describe().Jobs.Single().State);

        host.Disable();
        host.Enable();
        Assert.Equal("working", host.Statuses.Get(plugin.Manifest.Id).Describe().State);
        await scheduler.RunDueJobsAsync(CancellationToken.None);
        await scheduler.WhenIdleAsync();
        Assert.Equal(4, job.Runs);
    }

    [Fact]
    public async Task AJobThatFinishesInTimeBetweenLimitStops_StartsTheCountOverAgain()
    {
        var limits = PluginHostLimits.Default with { JobMaxRunTime = TimeSpan.FromMilliseconds(150), JobCancelGrace = TimeSpan.FromMilliseconds(150) };
        var plugin = new HostServicesTestPlugin();
        var shouldHang = true;
        var job = plugin.AddJob("sometimes-slow", displayName: "Sometimes slow", body: (_, token) =>
            shouldHang ? Task.Delay(Timeout.Infinite, token) : Task.CompletedTask);
        var host = NewHost(limits, plugin);
        host.Enable();
        var scheduler = host.NewScheduler();
        await scheduler.RunDueJobsAsync(CancellationToken.None);

        foreach (var hang in new[] { true, true, false, true, true })
        {
            shouldHang = hang;
            host.Clock.Advance(JustOver);
            await scheduler.RunDueJobsAsync(CancellationToken.None);
            await scheduler.WhenIdleAsync();
        }

        Assert.Equal(5, job.Runs);
        Assert.Equal("working", host.Statuses.Get(plugin.Manifest.Id).Describe().State);
    }

    [Fact]
    public async Task AJobThatThrows_IsRecordedFailedWithItsMessage_AndTheNextRunStillHappens()
    {
        var plugin = new HostServicesTestPlugin();
        var fail = true;
        var job = plugin.AddJob("flaky", displayName: "Flaky job", body: (_, _) =>
            fail ? throw new InvalidOperationException("The list site is down") : Task.CompletedTask);
        var host = NewHost(plugins: plugin);
        host.Enable();
        var scheduler = host.NewScheduler();
        await scheduler.RunDueJobsAsync(CancellationToken.None);

        host.Clock.Advance(JustOver);
        await scheduler.RunDueJobsAsync(CancellationToken.None);
        await scheduler.WhenIdleAsync();
        fail = false;
        host.Clock.Advance(JustOver);
        await scheduler.RunDueJobsAsync(CancellationToken.None);
        await scheduler.WhenIdleAsync();

        var runs = await host.Runs.ListRecentAsync(KeyOf(plugin, job), 10);
        Assert.Equal([ScheduledTaskOutcomes.Succeeded, ScheduledTaskOutcomes.Failed], runs.Select(run => run.Outcome));
        Assert.Equal("The list site is down", runs[1].Message);
        Assert.Equal("Flaky job failed: The list site is down", host.Statuses.Get(plugin.Manifest.Id).Describe().LastProblem);
    }

    [Fact]
    public async Task APluginWithoutScheduledWork_NeverRunsAndIsNotListed()
    {
        var plugin = new HostServicesTestPlugin(permissions: [PluginPermissionIds.PluginRecords]);
        var job = plugin.AddJob("sneaky");
        var host = NewHost(plugins: plugin);
        host.Enable();
        var scheduler = host.NewScheduler();
        await scheduler.RunDueJobsAsync(CancellationToken.None);

        host.Clock.Advance(TimeSpan.FromDays(2));
        await scheduler.RunDueJobsAsync(CancellationToken.None);
        await scheduler.WhenIdleAsync();

        Assert.Equal(0, job.Runs);
        Assert.Empty(scheduler.ListScheduledTasks());
    }

    [Fact]
    public async Task ASwitchedOffPlugin_NeverRunsAndIsNotListed()
    {
        var plugin = new HostServicesTestPlugin();
        var job = plugin.AddJob("refresh");
        var host = NewHost(plugins: plugin);
        var scheduler = host.NewScheduler();

        await scheduler.RunDueJobsAsync(CancellationToken.None);
        host.Clock.Advance(TimeSpan.FromDays(2));
        await scheduler.RunDueJobsAsync(CancellationToken.None);
        await scheduler.WhenIdleAsync();

        Assert.Equal(0, job.Runs);
        Assert.Empty(scheduler.ListScheduledTasks());
    }

    [Fact]
    public async Task ACronJob_IsListedWithItsCronText_AndItsNextRunComesFromTheCron()
    {
        var plugin = new HostServicesTestPlugin();
        plugin.AddJob("nightly", PluginSchedule.OnCron("0 3 * * *"), displayName: "Nightly");
        var host = NewHost(plugins: plugin);
        host.Enable();
        var scheduler = host.NewScheduler();
        var registry = new ScheduledTaskRegistry();
        registry.AddSource(scheduler);

        var list = await ScheduledTaskListBuilder.BuildAsync(registry, host.Runs, host.Clock.GetUtcNow(), CancellationToken.None);

        var task = Assert.Single(list.Tasks);
        Assert.Equal("0 3 * * *", task.Schedule);
        Assert.NotNull(task.NextRunAt);
        Assert.True(task.NextRunAt > host.Clock.GetUtcNow());
    }

    [Fact]
    public async Task AnOverrideInThePluginsConfigFile_ChangesWhenTheJobRuns_AndABadOneIsIgnored()
    {
        var plugin = new HostServicesTestPlugin();
        var job = plugin.AddJob("refresh");
        var host = NewHost(plugins: plugin);
        var settings = new PluginSettingsService(host.Loader);
        var config = settings.Load(plugin.Manifest);
        config.Enabled = true;
        config.Schedules["refresh"] = "10m";
        settings.Save(config);
        host.Catalog.Reload();
        var scheduler = host.NewScheduler();
        await scheduler.RunDueJobsAsync(CancellationToken.None);

        host.Clock.Advance(TimeSpan.FromMinutes(11));
        await scheduler.RunDueJobsAsync(CancellationToken.None);
        await scheduler.WhenIdleAsync();
        Assert.Equal(1, job.Runs);
        Assert.Contains("\"schedules\"", File.ReadAllText(Path.Combine(host.LibraryRoot, ".data", "plugin-config", plugin.Manifest.Id + ".json")), StringComparison.Ordinal);

        var ignored = PluginJobSchedules.Effective(job, new Dictionary<string, string> { ["refresh"] = "1m" }, out var problem);
        Assert.Equal(job.DefaultSchedule, ignored);
        Assert.Contains("ignored", problem, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("30m", 30)]
    [InlineData("2h", 120)]
    [InlineData("every 45 minutes", 45)]
    [InlineData("1d", 1440)]
    public void ScheduleText_ReadsIntervals(string text, int minutes)
    {
        Assert.True(PluginSchedule.TryParse(text, out var schedule));
        Assert.Equal(TimeSpan.FromMinutes(minutes), schedule!.Interval);
    }

    [Theory]
    [InlineData("4m")]
    [InlineData("0m")]
    [InlineData("soon")]
    [InlineData("")]
    [InlineData("* * *")]
    public void ScheduleText_RefusesAnIntervalUnderFiveMinutesAndNonsense(string text)
    {
        Assert.False(PluginSchedule.TryParse(text, out _));
    }

    [Fact]
    public void ScheduleText_KeepsACronExpression_AndAnIntervalUnderFiveMinutesCannotBeBuilt()
    {
        Assert.True(PluginSchedule.TryParse("0 3 * * *", out var cron));
        Assert.Equal("0 3 * * *", cron!.Cron);
        Assert.Throws<ArgumentOutOfRangeException>(() => PluginSchedule.Every(TimeSpan.FromMinutes(4)));
    }

    private static HostServicesTestPlugin HeavyPlugin() =>
        new(declaration: new PluginDeclaration { WeightClass = "heavy" });

    [Fact]
    public async Task AHeavyJob_WaitsWhileIngestionIsWorking_AndRunsOnceItHasFinished()
    {
        var plugin = HeavyPlugin();
        var job = plugin.AddJob("scan");
        var host = NewHost(plugins: plugin);
        host.Enable();
        var ingestion = new FixedIngestion("working");
        var scheduler = host.NewScheduler(ingestion);
        await scheduler.RunDueJobsAsync(CancellationToken.None);
        host.Clock.Advance(JustOver);

        await scheduler.RunDueJobsAsync(CancellationToken.None);
        await scheduler.WhenIdleAsync();
        Assert.Equal(0, job.Runs);

        ingestion.State = "idle";
        await scheduler.RunDueJobsAsync(CancellationToken.None);
        await scheduler.WhenIdleAsync();
        Assert.Equal(1, job.Runs);
    }

    [Fact]
    public async Task ALightJob_IsNotHeldBackByIngestion()
    {
        var plugin = new HostServicesTestPlugin();
        var job = plugin.AddJob("refresh");
        var host = NewHost(plugins: plugin);
        host.Enable();
        var scheduler = host.NewScheduler(new FixedIngestion("working"));
        await scheduler.RunDueJobsAsync(CancellationToken.None);
        host.Clock.Advance(JustOver);

        await scheduler.RunDueJobsAsync(CancellationToken.None);
        await scheduler.WhenIdleAsync();

        Assert.Equal(1, job.Runs);
    }

    [Fact]
    public async Task HeavyJobs_RunOneAtATime_AndTheOtherGoesNextWhenTheFirstIsDone()
    {
        var plugin = HeavyPlugin();
        var gate = new TaskCompletionSource();
        var jobs = new[] { "a", "b" }.Select(key => plugin.AddJob(key, body: (_, _) => gate.Task)).ToList();
        var host = NewHost(plugins: plugin);
        host.Enable();
        var scheduler = host.NewScheduler(new FixedIngestion("idle"));
        await scheduler.RunDueJobsAsync(CancellationToken.None);
        host.Clock.Advance(JustOver);

        await scheduler.RunDueJobsAsync(CancellationToken.None);
        await HostServicesHarness.EventuallyAsync(() => jobs.Sum(job => job.Runs) == 1, "one heavy job to start");
        await Task.Delay(100);
        Assert.Equal(1, jobs.Sum(job => job.Runs));

        gate.SetResult();
        await scheduler.WhenIdleAsync();
        await scheduler.RunDueJobsAsync(CancellationToken.None);
        await scheduler.WhenIdleAsync();
        Assert.All(jobs, job => Assert.Equal(1, job.Runs));
    }

    [Fact]
    public async Task AJobWhoseRequiredPluginIsOff_WaitsInsteadOfRunning_AndRunsOnceThatPluginIsOn()
    {
        var needed = new HostServicesTestPlugin(id: "test.needed", name: "Needed");
        var plugin = new HostServicesTestPlugin(
            declaration: new PluginDeclaration { Requires = [new PluginRequirement { Kind = "plugin", Id = "test.needed" }] });
        var job = plugin.AddJob("refresh");
        var host = NewHost(plugins: [needed, plugin]);
        host.Enable(plugin.Manifest.Id);
        var scheduler = host.NewScheduler();
        await scheduler.RunDueJobsAsync(CancellationToken.None);
        host.Clock.Advance(JustOver);

        await scheduler.RunDueJobsAsync(CancellationToken.None);
        await scheduler.WhenIdleAsync();
        Assert.Equal(0, job.Runs);
        Assert.Equal("Waiting for Needed", host.Catalog.WaitingFor(plugin.Manifest.Id));

        host.Enable("test.needed");
        await scheduler.RunDueJobsAsync(CancellationToken.None);
        host.Clock.Advance(JustOver);
        await scheduler.RunDueJobsAsync(CancellationToken.None);
        await scheduler.WhenIdleAsync();
        Assert.Equal(1, job.Runs);
    }

    private sealed class FixedIngestion(string state) : MediaEngine.Api.Services.IIngestionQueueProgressService
    {
        public string State { get; set; } = state;

        public MediaEngine.Contracts.Ingestion.IngestionQueueProgressDto Current => new() { State = State };
    }

    public void Dispose()
    {
        foreach (var disposable in _disposables)
        {
            disposable.Dispose();
        }
    }
}
