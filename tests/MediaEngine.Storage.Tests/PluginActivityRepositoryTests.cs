using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Entities;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Storage.Tests;

/// <summary>A plugin's Activity list and the separate retention of the people-action record, against a real temporary data store.</summary>
public sealed class PluginActivityRepositoryTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);

    private readonly string _dbPath;
    private readonly DatabaseConnection _db;
    private readonly PluginActivityRepository _repo;

    public PluginActivityRepositoryTests()
    {
        DapperConfiguration.Configure();
        _dbPath = Path.Combine(Path.GetTempPath(), $"tuvima_plugin_activity_{Guid.NewGuid():N}.db");
        _db = new DatabaseConnection(_dbPath);
        _db.InitializeSchema();
        _db.RunStartupChecks();
        _repo = new PluginActivityRepository(_db);
    }

    public void Dispose()
    {
        try { _db.Dispose(); } catch (Exception) { /* temp data store cleanup is best effort */ }
        try { File.Delete(_dbPath); } catch (IOException) { /* temp data store cleanup is best effort */ }
    }

    private static StoredPluginActivity Entry(string plugin, PluginActivityKind kind, string summary, DateTimeOffset? at = null, string? reason = null, Guid? item = null) =>
        new(0, at ?? Now, plugin, "1.0.0", kind, reason, item, kind == PluginActivityKind.Run ? 3 : 0, summary, null);

    [Fact]
    public async Task AddedLines_ComeBackNewestFirst_WithTheirCauseAndItem()
    {
        var item = Guid.NewGuid();
        await _repo.AddAsync(Entry("tuvima.skip-markers", PluginActivityKind.Run, "Found markers"), 100);
        await _repo.AddAsync(Entry("tuvima.skip-markers", PluginActivityKind.Failure, "FFmpeg is missing", reason: "tool_missing", item: item), 100);

        var rows = await _repo.GetRecentAsync("tuvima.skip-markers", 10, failuresOnly: false);

        Assert.Equal(["FFmpeg is missing", "Found markers"], rows.Select(row => row.Summary));
        Assert.Equal("tool_missing", rows[0].Reason);
        Assert.Equal(item, rows[0].ItemId);
        Assert.Equal(3, rows[1].ItemsChanged);
        Assert.Equal(Now, rows[0].OccurredAt);
    }

    [Fact]
    public async Task EachPluginSeesOnlyItsOwnLines_AndFailuresCanBeListedAlone()
    {
        await _repo.AddAsync(Entry("a", PluginActivityKind.Run, "a ran"), 100);
        await _repo.AddAsync(Entry("a", PluginActivityKind.Failure, "a failed", reason: "plugin_crashed"), 100);
        await _repo.AddAsync(Entry("b", PluginActivityKind.Failure, "b failed", reason: "key_rejected"), 100);

        Assert.Equal(2, (await _repo.GetRecentAsync("a", 10, false)).Count);
        Assert.Equal(["a failed"], (await _repo.GetRecentAsync("a", 10, true)).Select(row => row.Summary));
        Assert.Equal(1, await _repo.CountFailuresSinceAsync("b", Now.AddHours(-1)));
        Assert.Equal(0, await _repo.CountFailuresSinceAsync("b", Now.AddHours(1)));
    }

    [Fact]
    public async Task OnlyTheNewestLinesPerPluginAreKept()
    {
        for (var number = 1; number <= 5; number++)
        {
            await _repo.AddAsync(Entry("a", PluginActivityKind.Note, $"line {number}"), keepPerPlugin: 3);
        }

        await _repo.AddAsync(Entry("b", PluginActivityKind.Note, "other"), keepPerPlugin: 3);

        Assert.Equal(["line 5", "line 4", "line 3"], (await _repo.GetRecentAsync("a", 10, false)).Select(row => row.Summary));
        Assert.Single(await _repo.GetRecentAsync("b", 10, false));
    }

    [Fact]
    public async Task RemovingAPluginRemovesOnlyItsLines_AndPruningDropsOldOnes()
    {
        await _repo.AddAsync(Entry("a", PluginActivityKind.Note, "old", at: Now.AddDays(-40)), 100);
        await _repo.AddAsync(Entry("a", PluginActivityKind.Note, "new"), 100);
        await _repo.AddAsync(Entry("b", PluginActivityKind.Note, "b"), 100);

        Assert.Equal(1, await _repo.PruneAsync(Now.AddDays(-30)));
        Assert.Equal(1, await _repo.DeleteForPluginAsync("a"));
        Assert.Empty(await _repo.GetRecentAsync("a", 10, false));
        Assert.Single(await _repo.GetRecentAsync("b", 10, false));
    }

    [Fact]
    public async Task PeopleActions_AreLeftOutOfTheGeneralPruning_AndPrunedByTheirOwnRetention()
    {
        var activity = new SystemActivityRepository(_db);
        var old = DateTimeOffset.UtcNow.AddDays(-200);
        await activity.LogAsync(new SystemActivityEntry { OccurredAt = old, ActionType = SystemActionType.PluginPeopleAction, Detail = "Chose lyrics" });
        await activity.LogAsync(new SystemActivityEntry { OccurredAt = old, ActionType = SystemActionType.MediaAdded, Detail = "Dune" });

        Assert.Equal(1, await activity.PruneOlderThanAsync(90));
        Assert.Equal(1, await activity.CountAsync());

        Assert.Equal(0, await activity.PruneActionTypeOlderThanAsync(SystemActionType.PluginPeopleAction, 365));
        Assert.Equal(1, await activity.PruneActionTypeOlderThanAsync(SystemActionType.PluginPeopleAction, 100));
        Assert.Equal(0, await activity.CountAsync());
    }
}
