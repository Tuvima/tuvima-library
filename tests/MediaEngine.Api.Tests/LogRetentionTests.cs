using MediaEngine.Api.Services;
using MediaEngine.Api.Services.EngineLogs;
using MediaEngine.Contracts.System;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Entities;
using MediaEngine.Storage;
using MediaEngine.TestSupport;

namespace MediaEngine.Api.Tests;

/// <summary>
/// The Logs page retention rule: system activity goes by days first and then by the event cap, Engine log files go by
/// days first and then by total size, and the newest log file (the one being written) is never removed.
/// </summary>
public sealed class LogRetentionTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"tuvima-log-retention-{Guid.NewGuid():N}.db");
    private readonly string _logDirectory = Path.Combine(Path.GetTempPath(), $"tuvima-log-files-{Guid.NewGuid():N}");
    private readonly DatabaseConnection _database;
    private readonly SystemActivityRepository _activity;

    public LogRetentionTests()
    {
        DapperConfiguration.Configure();
        _database = new DatabaseConnection(_databasePath);
        _database.InitializeSchema();
        _activity = new SystemActivityRepository(_database);
        Directory.CreateDirectory(_logDirectory);
    }

    [Fact]
    public void NewInstalls_KeepEventsForNinetyDays_AtMostHalfAMillion_AndLogFilesNinetyDaysUpToAGigabyte()
    {
        var settings = new MaintenanceSettings();

        Assert.Equal(90, settings.ActivityRetentionDays);
        Assert.Equal(500_000, settings.ActivityMaxEvents);
        Assert.Equal(90, settings.LogRetentionDays);
        Assert.Equal(1024, settings.LogMaxTotalMegabytes);
    }

    [Fact]
    public async Task ActivityPrune_RemovesEventsOlderThanTheDayLimit_AndLeavesRecentOnes()
    {
        await LogEventsAsync(old: 3, recent: 4);
        var pruner = new ActivityRetentionPruner(_activity, _activity);

        var result = await pruner.PruneAsync(new MaintenanceSettings { ActivityRetentionDays = 30, ActivityMaxEvents = 1_000_000 });

        Assert.Equal(3, result.DeletedForAge);
        Assert.Equal(0, result.DeletedForCap);
        Assert.Equal(4, await _activity.CountAsync());
    }

    [Fact]
    public async Task ActivityPrune_WhenStillOverTheEventCapAfterTheDayLimit_RemovesTheOldestEventsFirst()
    {
        await LogEventsAsync(old: 2, recent: 6);
        var pruner = new ActivityRetentionPruner(_activity, _activity);

        var result = await pruner.PruneAsync(new MaintenanceSettings { ActivityRetentionDays = 30, ActivityMaxEvents = 4 });

        // The two old ones go for age; of the six recent ones, the two oldest go for the cap.
        Assert.Equal(2, result.DeletedForAge);
        Assert.Equal(2, result.DeletedForCap);
        Assert.Equal(["recent-3", "recent-4", "recent-5", "recent-6"], RemainingDetails());
    }

    [Fact]
    public async Task ActivityPrune_AppliesTheDayLimitBeforeTheCap_SoTheCapOnlyTrimsWhatTheDaysKept()
    {
        await LogEventsAsync(old: 5, recent: 3);
        var pruner = new ActivityRetentionPruner(_activity, _activity);

        // The cap alone (4) would have kept two old events; the day limit runs first and removes all five.
        var result = await pruner.PruneAsync(new MaintenanceSettings { ActivityRetentionDays = 30, ActivityMaxEvents = 4 });

        Assert.Equal(5, result.DeletedForAge);
        Assert.Equal(0, result.DeletedForCap);
        Assert.Equal(["recent-1", "recent-2", "recent-3"], RemainingDetails());
    }

    [Fact]
    public async Task ChangingTheLimits_ChangesWhatTheNextPruneRemoves()
    {
        await LogEventsAsync(old: 0, recent: 6);
        var pruner = new ActivityRetentionPruner(_activity, _activity);

        var relaxed = await pruner.PruneAsync(new MaintenanceSettings { ActivityRetentionDays = 30, ActivityMaxEvents = 10 });
        var tightened = await pruner.PruneAsync(new MaintenanceSettings { ActivityRetentionDays = 30, ActivityMaxEvents = 2 });

        Assert.Equal(0, relaxed.Total);
        Assert.Equal(4, tightened.DeletedForCap);
        Assert.Equal(2, await _activity.CountAsync());
    }

    [Fact]
    public async Task ActivityPrune_NeverRemovesAnythingWhenWithinBothLimits()
    {
        await LogEventsAsync(old: 0, recent: 3);
        var pruner = new ActivityRetentionPruner(_activity, _activity);

        var result = await pruner.PruneAsync(new MaintenanceSettings());

        Assert.Equal(0, result.Total);
        Assert.Equal(3, await _activity.CountAsync());
    }

    [Fact]
    public async Task EventCapBelowOne_IsRefused()
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => _activity.PruneToMaxEventsAsync(0));
    }

    [Fact]
    public void LogFilePrune_RemovesFilesOlderThanTheDayLimit_AndKeepsTheRest()
    {
        var now = new DateTimeOffset(2026, 10, 19, 3, 0, 0, TimeSpan.Zero);
        var ancient = WriteLog("tuvima-20260101.log", 10, now.AddDays(-200));
        var old = WriteLog("tuvima-20260601.log", 10, now.AddDays(-100));
        var recent = WriteLog("tuvima-20261010.log", 10, now.AddDays(-9));
        var result = LogFilePruner.Prune(_logDirectory, retentionDays: 90, maxTotalBytes: long.MaxValue, now);

        Assert.Equal(2, result.DeletedForAge);
        Assert.False(File.Exists(ancient));
        Assert.False(File.Exists(old));
        Assert.True(File.Exists(recent));
    }

    [Fact]
    public void LogFilePrune_NeverRemovesTheOnlyFile_EvenWhenItIsOldAndOverTheSizeLimit()
    {
        var now = new DateTimeOffset(2026, 10, 19, 3, 0, 0, TimeSpan.Zero);
        var only = WriteLog("tuvima-20260101.log", 5_000, now.AddDays(-300));

        var result = LogFilePruner.Prune(_logDirectory, retentionDays: 90, maxTotalBytes: 100, now);

        Assert.Equal(0, result.DeletedForAge + result.DeletedForSize);
        Assert.True(File.Exists(only));
    }

    [Fact]
    public void LogFilePrune_WhenOverTheTotalSize_RemovesTheOldestFilesFirstUntilUnderTheLimit()
    {
        var now = new DateTimeOffset(2026, 10, 19, 3, 0, 0, TimeSpan.Zero);
        var oldest = WriteLog("tuvima-20261001.log", 400, now.AddDays(-18));
        var middle = WriteLog("tuvima-20261005.log", 400, now.AddDays(-14));
        var recent = WriteLog("tuvima-20261012.log", 400, now.AddDays(-7));
        var active = WriteLog("tuvima-20261019.log", 400, now);

        // 1,600 bytes against a 1,000 byte limit: the two oldest go (leaving 800), the next oldest stays.
        var result = LogFilePruner.Prune(_logDirectory, retentionDays: 90, maxTotalBytes: 1_000, now);

        Assert.Equal(0, result.DeletedForAge);
        Assert.Equal(2, result.DeletedForSize);
        Assert.False(File.Exists(oldest));
        Assert.False(File.Exists(middle));
        Assert.True(File.Exists(recent));
        Assert.True(File.Exists(active));
    }

    [Fact]
    public void LogFilePrune_AppliesDaysBeforeSize_SoOnlyWhatTheDaysKeptCountsTowardTheSizeLimit()
    {
        var now = new DateTimeOffset(2026, 10, 19, 3, 0, 0, TimeSpan.Zero);
        var tooOld = WriteLog("tuvima-20260101.log", 900, now.AddDays(-200));
        var kept = WriteLog("tuvima-20261012.log", 300, now.AddDays(-7));
        var active = WriteLog("tuvima-20261019.log", 300, now);

        // After the day limit removes the 900 byte file, 600 bytes remain, under the 700 byte limit.
        var result = LogFilePruner.Prune(_logDirectory, retentionDays: 90, maxTotalBytes: 700, now);

        Assert.Equal(1, result.DeletedForAge);
        Assert.Equal(0, result.DeletedForSize);
        Assert.False(File.Exists(tooOld));
        Assert.True(File.Exists(kept));
        Assert.True(File.Exists(active));
    }

    [Fact]
    public void LogFilePrune_IgnoresFilesThatAreNotEngineLogs()
    {
        var now = new DateTimeOffset(2026, 10, 19, 3, 0, 0, TimeSpan.Zero);
        var other = Path.Combine(_logDirectory, "notes.txt");
        File.WriteAllText(other, "keep me");
        File.SetLastWriteTimeUtc(other, now.AddDays(-400).UtcDateTime);
        WriteLog("tuvima-20261019.log", 10, now);

        LogFilePruner.Prune(_logDirectory, retentionDays: 1, maxTotalBytes: 1, now);

        Assert.True(File.Exists(other));
    }

    [Fact]
    public void LogFilePrune_WhenTheFolderDoesNotExist_DoesNothing()
    {
        var result = LogFilePruner.Prune(Path.Combine(_logDirectory, "missing"), 90, 1024, DateTimeOffset.UtcNow);

        Assert.Equal(new LogFilePruneResult(0, 0, 0), result);
    }

    [Fact]
    public void LogFilePrune_WhenAnOldFileIsStillOpen_SkipsItAndKeepsGoing()
    {
        if (!OperatingSystem.IsWindows())
        {
            // Only Windows refuses to delete a file another process holds open without share-delete.
            return;
        }

        var now = new DateTimeOffset(2026, 10, 19, 3, 0, 0, TimeSpan.Zero);
        var held = WriteLog("tuvima-20260101.log", 10, now.AddDays(-200));
        var free = WriteLog("tuvima-20260201.log", 10, now.AddDays(-150));
        WriteLog("tuvima-20261019.log", 10, now);

        using (new FileStream(held, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var result = LogFilePruner.Prune(_logDirectory, retentionDays: 90, maxTotalBytes: long.MaxValue, now);

            Assert.Equal(1, result.Skipped);
            Assert.Equal(1, result.DeletedForAge);
        }

        Assert.True(File.Exists(held));
        Assert.False(File.Exists(free));
    }

    [Theory]
    [InlineData(0, 500_000, 90, 1024)]
    [InlineData(90, 10, 90, 1024)]
    [InlineData(90, 500_000, 0, 1024)]
    [InlineData(90, 500_000, 90, 10)]
    [InlineData(4000, 500_000, 90, 1024)]
    [InlineData(90, 500_000, 90, 999_999)]
    public void RetentionLimits_OutOfRange_AreRefusedWithASentence(int days, int events, int logDays, int megabytes)
    {
        Assert.False(string.IsNullOrWhiteSpace(LogRetentionLimits.Validate(days, events, logDays, megabytes)));
    }

    [Fact]
    public void RetentionLimits_TheDefaults_AreAccepted()
    {
        var settings = new MaintenanceSettings();

        Assert.Null(LogRetentionLimits.Validate(
            settings.ActivityRetentionDays, settings.ActivityMaxEvents, settings.LogRetentionDays, settings.LogMaxTotalMegabytes));
    }

    private async Task LogEventsAsync(int old, int recent)
    {
        for (var i = 1; i <= old; i++)
        {
            await _activity.LogAsync(new SystemActivityEntry
            {
                ActionType = "Test",
                Detail = $"old-{i}",
                OccurredAt = DateTimeOffset.UtcNow.AddDays(-100 - i),
            });
        }

        for (var i = 1; i <= recent; i++)
        {
            await _activity.LogAsync(new SystemActivityEntry
            {
                ActionType = "Test",
                Detail = $"recent-{i}",
                OccurredAt = DateTimeOffset.UtcNow.AddMinutes(-60 + i),
            });
        }
    }

    private List<string> RemainingDetails()
    {
        using var connection = _database.CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT detail FROM system_activity ORDER BY id;";
        using var reader = command.ExecuteReader();
        var details = new List<string>();
        while (reader.Read())
        {
            details.Add(reader.GetString(0));
        }

        return details;
    }

    private string WriteLog(string name, int bytes, DateTimeOffset lastWrite)
    {
        var path = Path.Combine(_logDirectory, name);
        File.WriteAllBytes(path, new byte[bytes]);
        File.SetLastWriteTimeUtc(path, lastWrite.UtcDateTime);
        return path;
    }

    public void Dispose()
    {
        _database.Dispose();
        TestTemp.DeleteDatabase(_databasePath);
        TestTemp.DeleteDirectory(_logDirectory);
    }
}
