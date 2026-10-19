using MediaEngine.Api.Services.ScheduledTasks;

namespace MediaEngine.Api.Tests;

/// <summary>
/// The Scheduled tasks list: every recurring service reports into the registry, which remembers each task's last run
/// (since the Engine started) and next run, and can later be fed durable run records.
/// </summary>
public sealed class ScheduledTaskRegistryTests
{
    private static readonly ScheduledTaskDefinition Backup = new("backup", "Back up", "Copies the data store.", "Every day at 2:00");
    private static readonly ScheduledTaskDefinition Tidy = new("tidy", "Tidy up", "Removes old files.", "Every day at 3:00");

    [Fact]
    public void ARegisteredTask_WithNoReports_HasNoLastRunAndNoNextRun()
    {
        var registry = NewRegistry(out _);
        registry.Register(Backup);

        var snapshot = Assert.Single(registry.GetSnapshot());

        Assert.Equal(Backup, snapshot.Definition);
        Assert.Null(snapshot.LastRun);
        Assert.Null(snapshot.NextRun);
    }

    [Fact]
    public void ADoneRun_IsRecordedWithTheTimeItFinished_AndTheNextRunIsKept()
    {
        var registry = NewRegistry(out var clock);
        var task = registry.Register(Backup);
        clock.SetLocalNow(new DateTimeOffset(2026, 10, 19, 2, 0, 5, TimeSpan.Zero));

        task.Done();
        task.NextRunAt(new DateTimeOffset(2026, 10, 20, 2, 0, 0, TimeSpan.Zero));

        var snapshot = Assert.Single(registry.GetSnapshot());
        Assert.Equal(ScheduledTaskOutcome.Done, snapshot.LastRun!.Outcome);
        Assert.Equal(new DateTimeOffset(2026, 10, 19, 2, 0, 5, TimeSpan.Zero), snapshot.LastRun.At);
        Assert.Null(snapshot.LastRun.Reason);
        Assert.Equal(new DateTimeOffset(2026, 10, 20, 2, 0, 0, TimeSpan.Zero), snapshot.NextRun);
    }

    [Fact]
    public void AFailedRun_SaysFailed_WithAShortReasonFromTheFirstLineOfTheError()
    {
        var registry = NewRegistry(out _);
        var task = registry.Register(Backup);

        task.Failed(new IOException("The disk is full.\nSecond line that should not show."));

        var run = Assert.Single(registry.GetSnapshot()).LastRun!;
        Assert.Equal(ScheduledTaskOutcome.Failed, run.Outcome);
        Assert.Equal("The disk is full.", run.Reason);
    }

    [Fact]
    public void ALongFailureReason_IsCutToOneShortLine()
    {
        var registry = NewRegistry(out _);
        var task = registry.Register(Backup);

        task.Failed(new InvalidOperationException(new string('x', 500)));

        var reason = Assert.Single(registry.GetSnapshot()).LastRun!.Reason!;
        Assert.True(reason.Length <= 140, $"The reason was {reason.Length} characters.");
    }

    [Fact]
    public void ALaterRun_ReplacesTheEarlierOne_SoAFailureIsClearedByTheNextSuccess()
    {
        var registry = NewRegistry(out _);
        var task = registry.Register(Backup);

        task.Failed(new IOException("Busy"));
        task.Done();

        Assert.Equal(ScheduledTaskOutcome.Done, Assert.Single(registry.GetSnapshot()).LastRun!.Outcome);
    }

    [Fact]
    public void RegisteringAgain_RefreshesTheWords_ButKeepsWhatHappenedAlready()
    {
        var registry = NewRegistry(out _);
        var task = registry.Register(Backup);
        task.Done();

        registry.Register(Backup with { Schedule = "Every day at 4:00" });

        var snapshot = Assert.Single(registry.GetSnapshot());
        Assert.Equal("Every day at 4:00", snapshot.Definition.Schedule);
        Assert.NotNull(snapshot.LastRun);
    }

    [Fact]
    public void Tasks_AreListedByName()
    {
        var registry = NewRegistry(out _);
        registry.Register(Tidy);
        registry.Register(Backup);

        Assert.Equal(["Back up", "Tidy up"], registry.GetSnapshot().Select(snapshot => snapshot.Definition.Name));
    }

    [Fact]
    public void ReportingForATaskThatNeverRegistered_IsIgnoredRatherThanFailingTheCaller()
    {
        var registry = NewRegistry(out _);

        registry.RecordRun("ghost", ScheduledTaskOutcome.Done);
        registry.SetNextRun("ghost", DateTimeOffset.Now);

        Assert.Empty(registry.GetSnapshot());
    }

    [Fact]
    public void TheEngineStartTime_IsWhenTheRegistryWasMade()
    {
        var clock = new TestClock(new DateTimeOffset(2026, 10, 19, 1, 0, 0, TimeSpan.Zero));

        var registry = new ScheduledTaskRegistry([], clock);

        Assert.Equal(new DateTimeOffset(2026, 10, 19, 1, 0, 0, TimeSpan.Zero), registry.StartedAt);
    }

    [Fact]
    public void ADurableRunRecord_ShowsInsteadOfNothing_AndInsteadOfAnOlderRunSinceStart()
    {
        var recorded = new ScheduledTaskRun(new DateTimeOffset(2026, 10, 18, 2, 0, 0, TimeSpan.Zero), ScheduledTaskOutcome.Done, null);
        var clock = new TestClock(new DateTimeOffset(2026, 10, 19, 1, 0, 0, TimeSpan.Zero));
        var registry = new ScheduledTaskRegistry([new FixedHistory("backup", recorded)], clock);
        registry.Register(Backup);
        registry.Register(Tidy);

        // Backup has a record from before this start and nothing since: the record shows.
        Assert.Equal(recorded, registry.GetSnapshot().Single(s => s.Definition.Id == "backup").LastRun);

        // A run since the start is newer than the record, so it wins.
        clock.SetLocalNow(new DateTimeOffset(2026, 10, 19, 2, 0, 0, TimeSpan.Zero));
        registry.RecordRun("backup", ScheduledTaskOutcome.Failed, "Disk full");
        var latest = registry.GetSnapshot().Single(s => s.Definition.Id == "backup").LastRun!;
        Assert.Equal(ScheduledTaskOutcome.Failed, latest.Outcome);

        // A task the history knows nothing about stays empty.
        Assert.Null(registry.GetSnapshot().Single(s => s.Definition.Id == "tidy").LastRun);
    }

    [Theory]
    [InlineData("0 3 * * *", "Every day at 3:00")]
    [InlineData("30 3 * * *", "Every day at 3:30")]
    [InlineData("0 14 * * *", "Every day at 14:00")]
    [InlineData("0 4 * * 0", "Every Sunday at 4:00")]
    [InlineData("0 5 * * 1", "Every Monday at 5:00")]
    [InlineData("*/15 * * * *", "Every 15 minutes")]
    [InlineData("*/1 * * * *", "Every minute")]
    [InlineData("0 */6 * * *", "Every 6 hours")]
    [InlineData("0 0 1 * *", "On a custom schedule")]
    [InlineData("", "On a custom schedule")]
    [InlineData(null, "On a custom schedule")]
    public void CronSchedules_AreDescribedInPlainWords(string? cron, string expected)
    {
        Assert.Equal(expected, ScheduleDescriber.FromCron(cron));
    }

    [Theory]
    [InlineData(1, "Every minute")]
    [InlineData(2, "Every 2 minutes")]
    [InlineData(60, "Every hour")]
    [InlineData(360, "Every 6 hours")]
    [InlineData(1440, "Every day")]
    public void Intervals_AreDescribedInPlainWords(int minutes, string expected)
    {
        Assert.Equal(expected, ScheduleDescriber.Every(TimeSpan.FromMinutes(minutes)));
    }

    private static ScheduledTaskRegistry NewRegistry(out TestClock clock)
    {
        clock = new TestClock(new DateTimeOffset(2026, 10, 19, 1, 0, 0, TimeSpan.Zero));
        return new ScheduledTaskRegistry([], clock);
    }

    private sealed class FixedHistory(string taskId, ScheduledTaskRun run) : IScheduledTaskRunHistory
    {
        public ScheduledTaskRun? GetLastRun(string id) => id == taskId ? run : null;
    }

    private sealed class TestClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public void SetLocalNow(DateTimeOffset value) => _now = value;

        public override DateTimeOffset GetUtcNow() => _now.ToUniversalTime();

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }
}
