using MediaEngine.Storage.Contracts;

namespace MediaEngine.Storage.Tests;

/// <summary>The notification tables added by upgrade step 0027, over a real temporary SQLite file.</summary>
public sealed class NotificationRepositoryTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 9, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(10);

    private readonly string _dbPath;
    private readonly DatabaseConnection _db;
    private readonly NotificationRepository _notifications;

    public NotificationRepositoryTests()
    {
        DapperConfiguration.Configure();
        _dbPath = Path.Combine(Path.GetTempPath(), $"tuvima_notifications_{Guid.NewGuid():N}.db");
        _db = new DatabaseConnection(_dbPath);
        _db.InitializeSchema();
        _db.RunStartupChecks();
        _notifications = new NotificationRepository(_db);
    }

    public void Dispose()
    {
        _db.Dispose();
        File.Delete(_dbPath);
    }

    [Fact]
    public async Task ARepeatInsideTheWindow_IsFoldedIntoOneUnreadMessageWithACount()
    {
        var first = await _notifications.AddOrGroupAsync("backup.failed", "A backup did not finish", "first", "backup.failed|manual", Now, Window);
        await _notifications.MarkReadAsync([first.Id], Now);

        var second = await _notifications.AddOrGroupAsync("backup.failed", "A backup did not finish", "second", "backup.failed|manual", Now.AddMinutes(3), Window);

        var all = await _notifications.ListAsync(10);
        var only = Assert.Single(all);
        Assert.Equal(first.Id, second.Id);
        Assert.Equal(2, only.OccurrenceCount);
        Assert.Equal("second", only.Body);
        Assert.Null(only.ReadAt);
    }

    [Fact]
    public async Task ARepeatAfterTheWindow_IsANewMessage()
    {
        await _notifications.AddOrGroupAsync("backup.failed", "t", "b", "backup.failed|manual", Now, Window);
        await _notifications.AddOrGroupAsync("backup.failed", "t", "b", "backup.failed|manual", Now.AddMinutes(30), Window);

        Assert.Equal(2, (await _notifications.ListAsync(10)).Count);
    }

    [Fact]
    public async Task MarkingRead_WithNoIds_ClearsEveryUnreadMessage_AndPruneKeepsTheNewest()
    {
        await _notifications.AddOrGroupAsync("a", "t", "b", "a|1", Now, Window);
        await _notifications.AddOrGroupAsync("b", "t", "b", "b|1", Now.AddMinutes(1), Window);
        await _notifications.AddOrGroupAsync("c", "t", "b", "c|1", Now.AddMinutes(2), Window);
        Assert.Equal(3, await _notifications.CountUnreadAsync());

        await _notifications.MarkReadAsync([], Now.AddMinutes(5));
        await _notifications.PruneAsync(2);

        Assert.Equal(0, await _notifications.CountUnreadAsync());
        Assert.Equal(["c", "b"], (await _notifications.ListAsync(10)).Select(item => item.EventId).ToArray());
    }

    [Fact]
    public async Task AChannelFailingFiveTimesInARow_SwitchesOff_AndASuccessBeforeThatStartsTheCountAgain()
    {
        for (var i = 0; i < 4; i++)
        {
            await _notifications.RecordChannelResultAsync("email", false, "no route", Now.AddMinutes(i), 5);
        }

        var afterFour = await _notifications.GetChannelStateAsync("email");
        Assert.Equal("needs_attention", afterFour!.State);
        Assert.Equal(4, afterFour.ConsecutiveFailures);

        var recovered = await _notifications.RecordChannelResultAsync("email", true, null, Now.AddMinutes(5), 5);
        Assert.Equal("healthy", recovered.State);
        Assert.Equal(0, recovered.ConsecutiveFailures);

        StoredChannelState last = recovered;
        for (var i = 0; i < 5; i++)
        {
            last = await _notifications.RecordChannelResultAsync("email", false, "no route", Now.AddMinutes(10 + i), 5);
        }

        Assert.Equal("off", last.State);
        Assert.NotNull(last.SwitchedOffAt);

        await _notifications.ResetChannelAsync("email");
        Assert.Equal("healthy", (await _notifications.GetChannelStateAsync("email"))!.State);
    }

    [Fact]
    public async Task TheSendRateClaim_AllowsOnePerInterval_AndReportsHowManyWereHeldBack()
    {
        var interval = TimeSpan.FromMinutes(1);

        var first = await _notifications.TryClaimSendAsync("email", "backup.failed", Now, interval);
        var second = await _notifications.TryClaimSendAsync("email", "backup.failed", Now.AddSeconds(10), interval);
        var third = await _notifications.TryClaimSendAsync("email", "backup.failed", Now.AddSeconds(20), interval);
        var otherEvent = await _notifications.TryClaimSendAsync("email", "tool.missing", Now.AddSeconds(20), interval);
        var later = await _notifications.TryClaimSendAsync("email", "backup.failed", Now.AddSeconds(90), interval);

        Assert.True(first.Allowed);
        Assert.False(second.Allowed);
        Assert.False(third.Allowed);
        Assert.True(otherEvent.Allowed);
        Assert.True(later.Allowed);
        Assert.Equal(2, later.HeldBack);
    }

    [Fact]
    public async Task Settings_AreStoredAndReplacedByKey()
    {
        Assert.Null(await _notifications.GetSettingAsync("settings"));

        await _notifications.SetSettingAsync("settings", "{\"a\":1}", Now);
        await _notifications.SetSettingAsync("settings", "{\"a\":2}", Now.AddMinutes(1));

        Assert.Equal("{\"a\":2}", await _notifications.GetSettingAsync("settings"));
    }
}
