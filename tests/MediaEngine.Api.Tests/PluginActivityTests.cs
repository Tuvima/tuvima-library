using MediaEngine.Api.Services.Plugins;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Plugins;
using MediaEngine.Storage.Contracts;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Api.Tests;

/// <summary>Activity, failure reasons, the automatic pause, redaction and the people-action log (Plugin architecture spec D10, C10, C17, C25).</summary>
public sealed class PluginActivityTests
{
    private sealed class FakeClock(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class SpyRepository : IPluginActivityRepository
    {
        public List<StoredPluginActivity> Rows { get; } = [];

        public bool Throws { get; set; }

        public Task AddAsync(StoredPluginActivity entry, int keepPerPlugin, CancellationToken ct = default)
        {
            if (Throws)
            {
                throw new InvalidOperationException("disk full");
            }

            Rows.Add(entry);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<StoredPluginActivity>> GetRecentAsync(string pluginId, int limit, bool failuresOnly, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<StoredPluginActivity>>(Rows.Where(row => row.PluginId == pluginId && (!failuresOnly || row.Kind == PluginActivityKind.Failure)).Reverse().ToList());

        public Task<int> CountFailuresSinceAsync(string pluginId, DateTimeOffset since, CancellationToken ct = default) => Task.FromResult(0);

        public Task<int> DeleteForPluginAsync(string pluginId, CancellationToken ct = default) =>
            Task.FromResult(Rows.RemoveAll(row => row.PluginId == pluginId));

        public Task<int> PruneAsync(DateTimeOffset olderThan, CancellationToken ct = default) => Task.FromResult(0);
    }

    private static readonly DateTimeOffset Start = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);
    private static readonly PluginActivityContext Skip = new("tuvima.skip-markers", "1.2.0");

    private static (PluginActivityService Service, SpyRepository Repo, FakeClock Clock, PluginCircuitBreaker Breaker) Create()
    {
        var clock = new FakeClock(Start);
        var repo = new SpyRepository();
        var breaker = new PluginCircuitBreaker(clock);
        return (new PluginActivityService(repo, breaker, clock, NullLogger<PluginActivityService>.Instance), repo, clock, breaker);
    }

    [Fact]
    public async Task AFailure_IsRecordedWithItsCauseAPlainSentenceAndARedactedDetail()
    {
        var (service, repo, _, _) = Create();

        await service.RecordFailureAsync(Skip, PluginFailureReason.ToolMissing,
            exception: new FileNotFoundException(@"Could not find C:\Users\Dana\tools\ffmpeg.exe with token=abc123456"));

        var row = Assert.Single(repo.Rows);
        Assert.Equal(PluginActivityKind.Failure, row.Kind);
        Assert.Equal("tool_missing", row.Reason);
        Assert.Equal("A tool it needs is missing", row.Summary);
        Assert.Equal("1.2.0", row.PluginVersion);
        Assert.DoesNotContain("Dana", row.Detail);
        Assert.DoesNotContain("abc123456", row.Detail);
    }

    [Fact]
    public async Task AVaultItemIsNeverRecordedWithItsId()
    {
        var (service, repo, _, _) = Create();
        var item = Guid.NewGuid();

        await service.RecordFailureAsync(Skip, PluginFailureReason.ItemUnreadable, itemId: item, itemInVault: true);
        await service.RecordSkippedAsync(Skip, PluginFailureReason.NotAllowedVault, itemId: item, itemInVault: true);
        await service.RecordFailureAsync(Skip, PluginFailureReason.ItemUnreadable, itemId: item);

        Assert.All(repo.Rows.Take(2), row => Assert.Null(row.ItemId));
        Assert.Equal(item, repo.Rows[2].ItemId);
    }

    [Fact]
    public async Task FiveFailuresInARow_PauseThePlugin_AndSaySo()
    {
        var (service, repo, _, _) = Create();

        for (var count = 1; count < PluginCircuitBreaker.FailuresBeforeOpen; count++)
        {
            Assert.False(await service.RecordFailureAsync(Skip, PluginFailureReason.ServiceUnreachable));
            Assert.True(service.Allow(Skip.PluginId));
        }

        Assert.True(await service.RecordFailureAsync(Skip, PluginFailureReason.ServiceUnreachable));

        Assert.False(service.Allow(Skip.PluginId));
        Assert.NotNull(service.PausedUntil(Skip.PluginId));
        Assert.Contains(repo.Rows, row => row.Kind == PluginActivityKind.Note && row.Summary.StartsWith("Paused after", StringComparison.Ordinal));
    }

    [Fact]
    public async Task APausedPlugin_IsTriedAgainAfterTheWait_AndASuccessClosesTheBreaker()
    {
        var (service, _, clock, _) = Create();
        for (var count = 0; count < PluginCircuitBreaker.FailuresBeforeOpen; count++)
        {
            await service.RecordFailureAsync(Skip, PluginFailureReason.ServiceRefused);
        }

        clock.Now = Start + PluginCircuitBreaker.FirstWait + TimeSpan.FromSeconds(1);
        Assert.True(service.Allow(Skip.PluginId));

        await service.RecordRunAsync(Skip, 4, "Found 4 markers");
        Assert.Null(service.PausedUntil(Skip.PluginId));
        Assert.True(service.Allow(Skip.PluginId));
    }

    [Fact]
    public async Task ATrialThatFailsAgain_PausesLongerEachTime_UpToAnHour()
    {
        var (service, _, clock, breaker) = Create();
        var waits = new List<TimeSpan>();
        for (var count = 0; count < PluginCircuitBreaker.FailuresBeforeOpen; count++)
        {
            await service.RecordFailureAsync(Skip, PluginFailureReason.ServiceRefused);
        }

        for (var round = 0; round < 9; round++)
        {
            var until = breaker.PausedUntil(Skip.PluginId)!.Value;
            waits.Add(until - clock.Now);
            clock.Now = until + TimeSpan.FromSeconds(1);
            await service.RecordFailureAsync(Skip, PluginFailureReason.ServiceRefused);
        }

        Assert.Equal(TimeSpan.FromMinutes(1), waits[0]);
        Assert.Equal(TimeSpan.FromMinutes(2), waits[1]);
        Assert.Equal(TimeSpan.FromMinutes(4), waits[2]);
        Assert.All(waits, wait => Assert.True(wait <= PluginCircuitBreaker.LongestWait));
        Assert.Equal(PluginCircuitBreaker.LongestWait, waits[^1]);
    }

    [Fact]
    public async Task ARefusedCaller_IsNotThePluginFailing_AndNeverPausesIt()
    {
        var (service, _, _, _) = Create();

        for (var count = 0; count < 20; count++)
        {
            await service.RecordFailureAsync(Skip, PluginFailureReason.NotAllowedAudience);
            await service.RecordFailureAsync(Skip, PluginFailureReason.DependencyOff);
        }

        Assert.True(service.Allow(Skip.PluginId));
    }

    [Fact]
    public async Task APluginsPauseDoesNotAffectAnotherPlugin()
    {
        var (service, _, _, _) = Create();
        for (var count = 0; count < PluginCircuitBreaker.FailuresBeforeOpen; count++)
        {
            await service.RecordFailureAsync(Skip, PluginFailureReason.PluginCrashed);
        }

        Assert.True(service.Allow("lrclib"));
    }

    [Fact]
    public async Task RecordingNeverThrows_SoAWorkingLibraryIsNotBlockedByAFullDisk()
    {
        var (service, repo, _, _) = Create();
        repo.Throws = true;

        await service.RecordFailureAsync(Skip, PluginFailureReason.PluginCrashed);
        await service.RecordRunAsync(Skip, 1, "ran");
        await service.RecordNoteAsync(Skip, "note");
    }

    [Fact]
    public async Task TheActivityList_ShowsCauseGroupsAndWords()
    {
        var (service, _, _, _) = Create();
        await service.RecordFailureAsync(Skip, PluginFailureReason.LocalAiOverAllowance);

        var response = await service.GetAsync(Skip.PluginId, 10, failuresOnly: true);

        var entry = Assert.Single(response.Entries);
        Assert.Equal("local_ai", entry.ReasonGroup);
        Assert.Equal("Local AI is over its hourly allowance", entry.ReasonWords);
        Assert.Equal("failure", entry.Kind);
    }

    [Fact]
    public void EveryReason_HasAUniqueWordAGroupAndASentence()
    {
        var all = Enum.GetValues<PluginFailureReason>();

        Assert.Equal(all.Length, all.Select(PluginFailureReasons.Word).Distinct().Count());
        Assert.All(all, reason =>
        {
            Assert.True(PluginFailureReasons.TryParse(PluginFailureReasons.Word(reason), out var parsed));
            Assert.Equal(reason, parsed);
            Assert.False(string.IsNullOrWhiteSpace(PluginFailureReasons.Words(reason)));
            Assert.Contains(PluginFailureReasons.Group(reason), new[] { "plugin", "outside_service", "missing_tool", "local_ai", "item", "limit", "not_allowed", "dependency_off", "library" });
        });
    }

    [Theory]
    [InlineData("failed with api_key=SECRET12345 on request", "SECRET12345")]
    [InlineData("Authorization: Bearer " + "not-a-real-token", "not-a-real-token")]
    [InlineData(@"cannot open C:\Users\Dana\Videos\movie.mkv", "Dana")]
    [InlineData("cannot open /home/dana/media/movie.mkv", "dana")]
    [InlineData("password: hunter2hunter2", "hunter2hunter2")]
    public void TheDetailLine_LosesKeysAndPersonalPaths(string text, string mustBeGone)
    {
        var result = PluginActivityRedactor.Redact(text);

        Assert.NotNull(result);
        Assert.DoesNotContain(mustBeGone, result);
    }

    [Fact]
    public void AKeyThePluginWasGiven_IsRemovedWhereverItAppears_AndLongLinesAreCut()
    {
        var result = PluginActivityRedactor.Redact("the key 9f8e7d6c5b was rejected " + new string('x', 2000), ["9f8e7d6c5b"]);

        Assert.DoesNotContain("9f8e7d6c5b", result);
        Assert.True(result!.Length <= PluginActivityRedactor.MaxDetailLength + 3);
        Assert.Null(PluginActivityRedactor.Redact("   "));
    }

    private sealed class SpyActivity : ISystemActivityRepository
    {
        public List<SystemActivityEntry> Logged { get; } = [];

        public Task LogAsync(SystemActivityEntry entry, CancellationToken ct = default)
        {
            Logged.Add(entry);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<SystemActivityEntry>> GetRecentByTypesAsync(IReadOnlyList<string> actionTypes, int limit = 50, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<SystemActivityEntry>>(Logged.Where(entry => actionTypes.Contains(entry.ActionType)).Take(limit).ToList());

        public Task<IReadOnlyList<SystemActivityEntry>> GetRecentAsync(int limit = 50, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<int> PruneOlderThanAsync(int retentionDays, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<long> CountAsync(CancellationToken ct = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<SystemActivityEntry>> GetRecentByProfileAsync(Guid profileId, int limit = 50, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<(IReadOnlyList<SystemActivityEntry> Items, int Total)> QueryAsync(IReadOnlyList<string> actionTypes, string? search, int offset, int limit, string? mediaType = null, CancellationToken ct = default) => throw new NotSupportedException();
    }

    [Fact]
    public async Task APeopleAction_GoesToTheLedgerWithWhoWhatWhichPluginBeforeAndAfter()
    {
        var spy = new SpyActivity();
        var log = new PluginPeopleActionLog(spy, NullLogger<PluginPeopleActionLog>.Instance);
        var profile = Guid.NewGuid();
        var song = Guid.NewGuid();

        await log.RecordAsync(new PluginPeopleAction(profile, "lrclib", "chose-lyrics", "Chose other lyrics", song, "Synced (LRCLIB)", "Plain (LRCLIB)", "Desktop"));
        await log.RecordAsync(new PluginPeopleAction(Guid.NewGuid(), "subdl", "marked-subtitles-wrong", "Marked subtitles wrong"));

        var entry = spy.Logged[0];
        Assert.Equal(SystemActionType.PluginPeopleAction, entry.ActionType);
        Assert.Equal(profile, entry.ProfileId);
        Assert.Equal(song, entry.EntityId);

        var mine = await log.GetAsync("lrclib", null, null, 10);
        var action = Assert.Single(mine.Actions);
        Assert.Equal("chose-lyrics", action.Action);
        Assert.Equal("Synced (LRCLIB)", action.Before);
        Assert.Equal("Plain (LRCLIB)", action.After);
        Assert.Equal("Desktop", action.Device);
        Assert.Equal(profile, action.ProfileId);

        Assert.Equal(2, (await log.GetAsync(null, null, null, 10)).Actions.Count);
        Assert.Single((await log.GetAsync(null, profile, null, 10)).Actions);
        Assert.Single((await log.GetAsync(null, null, "marked-subtitles-wrong", 10)).Actions);
    }
}
