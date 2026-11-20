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
    private readonly List<UpdateNotificationSettingsRequest> _saved = [];
    private readonly List<string> _tested = [];
    private readonly List<string> _enabled = [];
    private bool _markedRead;
    private bool _saveFails;
    private bool _testSucceeds = true;
    private string? _addProblem;
    private readonly List<SaveNotificationChannelRequest> _added = [];
    private NotificationListResponse _list = new();
    private NotificationSettingsResponse _settings = Settings();

    public NotificationsPanelTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        Services.AddLocalization();
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
                if (_saveFails)
                {
                    return Task.FromResult<NotificationSettingsResponse?>(null);
                }

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

                foreach (var (channelId, carries) in request.ChannelDetails)
                {
                    _settings.Channels.Single(channel => channel.Id == channelId).IncludeDetails = carries;
                }

                return Task.FromResult<NotificationSettingsResponse?>(_settings);
            });
            stub.SetHandler(nameof(IEngineApiClient.SendNotificationTestAsync), args =>
            {
                _tested.Add((string)args![0]!);
                return Task.FromResult<NotificationTestResponse?>(_testSucceeds
                    ? new NotificationTestResponse { Success = true, Message = "A test message was sent to me@example.com." }
                    : new NotificationTestResponse { Success = false, Message = "The mail server refused the message." });
            });
            stub.SetHandler(nameof(IEngineApiClient.GetNotificationChannelTypesAsync), _ => Task.FromResult<NotificationChannelTypesResponse?>(new NotificationChannelTypesResponse
            {
                Types =
                [
                    new NotificationChannelTypeDto
                    {
                        Id = "discord", Name = "Discord", Summary = "Free · paste a link", Help = "Paste the Discord webhook link.", WhatLeaves = "The alert text goes to Discord.",
                        Fields = [new NotificationChannelFieldDto { Key = "url", Label = "Webhook link", Kind = "url", Secret = true, Required = true }],
                    },
                ],
            }));
            stub.SetHandler(nameof(IEngineApiClient.AddNotificationChannelAsync), args =>
            {
                var request = (SaveNotificationChannelRequest)args![0]!;
                if (_addProblem is not null)
                {
                    return Task.FromResult(new NotificationChannelSaveResult(null, _addProblem));
                }

                _added.Add(request);
                _settings.Channels.Add(new NotificationChannelDto { Id = "channel:new", Name = "Discord", Type = "discord", IsExternal = true, IsConfigured = true, CanEdit = true, StateLabel = "Working" });
                return Task.FromResult(new NotificationChannelSaveResult(new NotificationChannelDetailDto { Id = "channel:new", Type = "discord", Name = "Discord" }, null));
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
    public void TheChannelsAndWhatGoesWhereComeFirst_AndTheMessagesListIsBelowThem()
    {
        var cut = Render<NotificationsPanel>();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid='notifications-channels']")));
        var markup = cut.Markup;
        var channels = markup.IndexOf("data-testid=\"notifications-channels\"", StringComparison.Ordinal);
        var matrix = markup.IndexOf("data-testid=\"notifications-matrix\"", StringComparison.Ordinal);
        var messages = markup.IndexOf("data-testid=\"notifications-unread\"", StringComparison.Ordinal);
        Assert.True(channels >= 0 && matrix > channels, "What goes where follows Channels.");
        Assert.True(messages > matrix, "The messages strip sits below Channels and What goes where.");
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
    public void SendTest_AsksTheEngine_AndShowsItsAnswerOnThatChannel()
    {
        var cut = Render<NotificationsPanel>();
        cut.WaitForAssertion(() => cut.Find("[data-channel='email']"));

        cut.Find("[data-channel='email']").QuerySelectorAll("button").Single(button => button.TextContent.Contains("Send test", StringComparison.Ordinal)).Click();

        cut.WaitForAssertion(() => Assert.Equal(["email"], _tested));
        cut.WaitForAssertion(() => Assert.Contains("me@example.com", cut.Find("[data-channel='email'] [data-channel-test]").TextContent, StringComparison.Ordinal));
    }

    [Fact]
    public void SendTest_ThatFails_ShowsTheEnginesReasonAsAnErrorOnThatChannel()
    {
        _testSucceeds = false;
        var cut = Render<NotificationsPanel>();
        cut.WaitForAssertion(() => cut.Find("[data-channel='email']"));

        cut.Find("[data-channel='email']").QuerySelectorAll("button").Single(button => button.TextContent.Contains("Send test", StringComparison.Ordinal)).Click();

        cut.WaitForAssertion(() => Assert.Contains("tl-alert-error", cut.Find("[data-channel='email'] [data-channel-test]").ClassName));
        Assert.Contains("The mail server refused the message.", cut.Find("[data-channel='email'] [data-channel-test]").TextContent);
    }

    [Fact]
    public void ARouteThatCannotBeSaved_ShowsAnAlertAtTheTop_AndAToastNever()
    {
        _saveFails = true;
        var cut = Render<NotificationsPanel>();
        cut.WaitForAssertion(() => cut.Find("[data-event='backup.failed']"));

        cut.Find("[data-event='backup.failed']").QuerySelectorAll("input[type='checkbox']")[1].Change(true);

        cut.WaitForAssertion(() => Assert.Equal("alert", cut.Find("[data-notifications-failure]").GetAttribute("role")));
    }

    [Fact]
    public void ARouteThatIsSaved_ShowsNoNoticeAtAll_BecauseTheTickAlreadyShowsIt()
    {
        var cut = Render<NotificationsPanel>();
        cut.WaitForAssertion(() => cut.Find("[data-event='backup.failed']"));

        cut.Find("[data-event='backup.failed']").QuerySelectorAll("input[type='checkbox']")[1].Change(true);

        cut.WaitForAssertion(() => Assert.Single(_saved));
        Assert.Empty(cut.FindAll("[data-notifications-failure]"));
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

        // Switches in order: the email channel's details, then quiet hours, then the events-to-webhooks switch.
        var switches = cut.FindAll("input[role='switch']");
        switches[1].Change(true);
        cut.WaitForAssertion(() => Assert.True(_saved.Last().QuietHours!.Enabled));

        cut.FindAll("input[role='switch']")[2].Change(true);
        cut.WaitForAssertion(() => Assert.True(_saved.Last().IncludeDetails));
    }

    [Fact]
    public void IncludeDetails_IsPerChannel_AndSavesJustThatChannel()
    {
        var cut = Render<NotificationsPanel>();
        cut.WaitForAssertion(() => cut.Find("[data-channel='email']"));

        cut.Find("[data-channel='email'] input[role='switch']").Change(true);

        cut.WaitForAssertion(() => Assert.True(_saved.Last().ChannelDetails["email"]));
    }

    [Fact]
    public void AddAChannel_ShowsTheKindsAsTiles_ThenTheFormForTheOnePicked_AndTestsTheNewChannel()
    {
        var cut = Render<NotificationsPanel>();
        cut.WaitForElement("[data-testid='channel-add']").Click();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid='notification-channel-picker']")));
        cut.Find("[data-channel-type='discord']").Click();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid='notification-channel-form']")));
        Assert.Contains("What leaves your computer", cut.Find("[data-testid='notification-form-what-leaves']").TextContent, StringComparison.Ordinal);
        cut.Find("[data-testid='notification-form-url']").Input("https://discord.com/api/webhooks/1/abc");
        cut.Find("[data-testid='notification-form-save']").Click();

        cut.WaitForAssertion(() => Assert.Single(_added));
        Assert.Equal("https://discord.com/api/webhooks/1/abc", _added.Single().Values["url"]);
        cut.WaitForAssertion(() => Assert.Equal(["channel:new"], _tested));
        Assert.Empty(cut.FindAll("[data-testid='notification-channel-form']"));
    }

    [Fact]
    public void AFormTheEngineRefuses_StaysOpenWithItsPlainSentence()
    {
        _addProblem = "Webhook link should be the link discord.com gave you.";
        var cut = Render<NotificationsPanel>();
        cut.WaitForElement("[data-testid='channel-add']").Click();
        cut.WaitForElement("[data-channel-type='discord']").Click();
        cut.WaitForElement("[data-testid='notification-form-url']").Input("https://example.com/x");

        cut.Find("[data-testid='notification-form-save']").Click();

        cut.WaitForAssertion(() => Assert.Contains("should be the link", cut.Find("[data-testid='notification-form-problem']").TextContent, StringComparison.Ordinal));
        Assert.NotEmpty(cut.FindAll("[data-testid='notification-channel-form']"));
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
