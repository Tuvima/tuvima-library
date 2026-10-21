using Bunit;
using MediaEngine.Contracts.Notifications;
using MediaEngine.Web.Components.Settings;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Ui;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>
/// Settings > Notifications (Developer tools spec WP23): the inbox, channel health with Send test, the routing table, quiet hours
/// and the privacy switch, all in the Engine's words.
/// </summary>
public sealed class NotificationsPanelTests : AsyncBunitContext
{
    private readonly AppToastService _toasts = new();
    private readonly List<UpdateNotificationSettingsRequest> _saved = [];
    private readonly List<string> _tested = [];
    private readonly List<string> _enabled = [];
    private bool _markedRead;
    private NotificationListResponse _list = new();
    private NotificationSettingsResponse _settings = Settings();

    public NotificationsPanelTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        Services.AddLocalization();
        Services.AddSingleton<IAppToastService>(_toasts);
        Services.AddSingleton(EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetNotificationsAsync), _ => Task.FromResult<NotificationListResponse?>(_list));
            stub.SetHandler(nameof(IEngineApiClient.GetNotificationSettingsAsync), _ => Task.FromResult<NotificationSettingsResponse?>(_settings));
            stub.SetHandler(nameof(IEngineApiClient.MarkNotificationsReadAsync), _ =>
            {
                _markedRead = true;
                return Task.FromResult(true);
            });
            stub.SetHandler(nameof(IEngineApiClient.UpdateNotificationSettingsAsync), args =>
            {
                var request = (UpdateNotificationSettingsRequest)args![0]!;
                _saved.Add(request);
                foreach (var (eventId, channels) in request.Routes)
                {
                    _settings.Events.Single(item => item.Id == eventId).Channels = channels;
                }

                if (request.QuietHours is { } quiet)
                {
                    _settings.QuietHours = quiet;
                }

                if (request.IncludeDetails is { } include)
                {
                    _settings.IncludeDetails = include;
                }

                return Task.FromResult<NotificationSettingsResponse?>(_settings);
            });
            stub.SetHandler(nameof(IEngineApiClient.SendNotificationTestAsync), args =>
            {
                _tested.Add((string)args![0]!);
                return Task.FromResult<NotificationTestResponse?>(new NotificationTestResponse { Success = true, Message = "A test message was sent to me@example.com." });
            });
            stub.SetHandler(nameof(IEngineApiClient.EnableNotificationChannelAsync), args =>
            {
                _enabled.Add((string)args![0]!);
                return Task.FromResult(true);
            });
        }));
    }

    [Fact]
    public void TheInbox_ShowsUnreadCountAndFoldedRepeats_AndMarkAllReadClearsIt()
    {
        _list = new NotificationListResponse
        {
            UnreadCount = 2,
            Items =
            [
                new NotificationItemDto { Id = Guid.NewGuid(), EventId = "backup.failed", Title = "A backup did not finish", Body = "The drive is full.", OccurrenceCount = 3, UpdatedAt = DateTimeOffset.UtcNow },
                new NotificationItemDto { Id = Guid.NewGuid(), EventId = "tool.missing", Title = "A tool the Library needs is missing", Body = "FFmpeg", UpdatedAt = DateTimeOffset.UtcNow },
            ],
        };

        var cut = Render<NotificationsPanel>();

        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[data-testid='notifications-list'] li").Count));
        Assert.Contains("2 unread messages", cut.Find("[data-testid='notifications-unread']").TextContent, StringComparison.Ordinal);
        Assert.Contains("3 times", cut.Find("[data-testid='notifications-list']").TextContent, StringComparison.Ordinal);

        cut.FindAll("button").Single(button => button.TextContent.Contains("Mark all read", StringComparison.Ordinal)).Click();

        cut.WaitForAssertion(() => Assert.Contains("All caught up", cut.Find("[data-testid='notifications-unread']").TextContent, StringComparison.Ordinal));
        Assert.True(_markedRead);
    }

    [Fact]
    public void AnEmptyInbox_SaysWhatWillAppearThere()
    {
        var cut = Render<NotificationsPanel>();

        cut.WaitForAssertion(() => Assert.Contains("Nothing yet", cut.Markup, StringComparison.Ordinal));
    }

    [Fact]
    public void ASwitchedOffChannel_ShowsNeedsAttentionWording_AndCanBeTurnedBackOn()
    {
        _settings.Channels[1].State = NotificationChannelStates.Off;
        _settings.Channels[1].StateLabel = "Switched off after repeated failures. Turn it back on once it is fixed.";

        var cut = Render<NotificationsPanel>();

        cut.WaitForAssertion(() => Assert.Contains("Switched off after repeated failures", cut.Find("[data-channel='email']").TextContent, StringComparison.Ordinal));
        cut.Find("[data-channel='email']").QuerySelectorAll("button").Single(button => button.TextContent.Contains("Turn back on", StringComparison.Ordinal)).Click();

        cut.WaitForAssertion(() => Assert.Equal(["email"], _enabled));
    }

    [Fact]
    public void SendTest_AsksTheEngine_AndShowsItsAnswer()
    {
        var cut = Render<NotificationsPanel>();
        cut.WaitForAssertion(() => cut.Find("[data-channel='email']"));

        cut.Find("[data-channel='email']").QuerySelectorAll("button").Single(button => button.TextContent.Contains("Send test", StringComparison.Ordinal)).Click();

        cut.WaitForAssertion(() => Assert.Equal(["email"], _tested));
        Assert.Contains(_toasts.Toasts, toast => toast.Message.Contains("me@example.com", StringComparison.Ordinal));
    }

    [Fact]
    public void ChangingARoute_SavesJustThatEventWithItsNewChannels()
    {
        var cut = Render<NotificationsPanel>();
        cut.WaitForAssertion(() => cut.Find("[data-event='backup.failed']"));

        // Backup failures go to the inbox only by default in this test; tick the email box.
        var boxes = cut.Find("[data-event='backup.failed']").QuerySelectorAll("input[type='checkbox']");
        boxes[1].Change(true);

        cut.WaitForAssertion(() => Assert.Single(_saved));
        Assert.Equal(["dashboard", "email"], _saved[0].Routes["backup.failed"].OrderBy(id => id).ToArray());
    }

    [Fact]
    public void QuietHoursAndThePrivacySwitch_SaveStraightAway()
    {
        var cut = Render<NotificationsPanel>();
        cut.WaitForAssertion(() => cut.Find("[data-testid='notifications-page']"));

        var switches = cut.FindAll("input[role='switch']");
        switches[0].Change(true);
        cut.WaitForAssertion(() => Assert.True(_saved.Last().QuietHours!.Enabled));

        cut.FindAll("input[role='switch']")[1].Change(true);
        cut.WaitForAssertion(() => Assert.True(_saved.Last().IncludeDetails));
    }

    private static NotificationSettingsResponse Settings() => new()
    {
        Events =
        [
            new NotificationEventDto { Id = "backup.failed", Title = "A backup did not finish", Description = "A backup could not be written.", Channels = ["dashboard"] },
            new NotificationEventDto { Id = "tool.missing", Title = "A tool the Library needs is missing", Description = "A required tool is not ready.", Channels = ["dashboard", "email"] },
        ],
        Channels =
        [
            new NotificationChannelDto { Id = "dashboard", Name = "Dashboard inbox", IsConfigured = true, StateLabel = "Working" },
            new NotificationChannelDto { Id = "email", Name = "Email", IsExternal = true, IsConfigured = true, StateLabel = "Working" },
        ],
    };
}
