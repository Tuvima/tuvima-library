using MediaEngine.Api.Services.Notifications;
using MediaEngine.Api.Services.Plugins;
using MediaEngine.Api.Services.Tools;
using MediaEngine.Contracts.Notifications;
using MediaEngine.Contracts.Settings;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Api.Tests;

/// <summary>
/// The notification service (Developer tools spec WP23): who hears about an event and by which channel, quiet hours, the
/// one-a-minute limit, a failing channel switching itself off, and the three sources that raise events.
/// </summary>
public sealed class NotificationServiceTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"tuvima_notification_service_{Guid.NewGuid():N}.db");
    private readonly DatabaseConnection _db;
    private readonly NotificationRepository _repository;
    private readonly AccountRepository _accounts;
    private Guid? _household;
    private readonly MutableClock _clock = new(Start);
    private readonly SpyEvents _events = new();
    private readonly SpyChannel _email = new(NotificationChannelIds.Email, "Email");
    private readonly NotificationService _service;
    private readonly NotificationSettingsAccessor _settings;

    public NotificationServiceTests()
    {
        DapperConfiguration.Configure();
        _db = new DatabaseConnection(_dbPath);
        _db.InitializeSchema();
        _db.RunStartupChecks();
        _repository = new NotificationRepository(_db);
        _accounts = new AccountRepository(_db);
        _settings = new NotificationSettingsAccessor(_repository, _clock);
        _service = new NotificationService(_repository, _settings, [_email], [], _accounts, _events, _clock, NullLogger<NotificationService>.Instance);
    }

    public void Dispose()
    {
        _db.Dispose();
        foreach (var path in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public async Task AFailedBackup_ReachesAnAdministratorByEmail_AndNeverANonAdministrator()
    {
        await AddAccountAsync("admin@example.com", administrator: true);
        await AddAccountAsync("viewer@example.com", administrator: false);

        await _service.DispatchAsync(new NotificationRequest(NotificationEventIds.BackupFailed, "manual", "The drive is full."), CancellationToken.None);

        var sent = Assert.Single(_email.Sent);
        Assert.Equal(["admin@example.com"], sent.Recipients);
        var inbox = Assert.Single(await _repository.ListAsync(10));
        Assert.Equal("The drive is full.", inbox.Body);
    }

    [Fact]
    public async Task ADisabledAdministratorOrOneWithoutAnEmail_IsNotWrittenTo()
    {
        await AddAccountAsync("off@example.com", administrator: true, enabled: false);
        await AddAccountAsync(null, administrator: true);

        await _service.DispatchAsync(new NotificationRequest(NotificationEventIds.BackupFailed, "manual", "Detail."), CancellationToken.None);

        Assert.Empty(_email.Sent);
        Assert.Single(await _repository.ListAsync(10));
    }

    [Fact]
    public async Task ByDefault_OnlyAGenericLineLeavesTheComputer_AndDetailsGoOutOnlyWhenChosen()
    {
        await AddAccountAsync("admin@example.com", administrator: true);
        await _service.DispatchAsync(new NotificationRequest(NotificationEventIds.PluginBroken, "p1", "Fandom Lore stopped working: it crashed."), CancellationToken.None);

        var generic = Assert.Single(_email.Sent);
        Assert.Equal("A plugin stopped working", generic.Message.Title);
        Assert.DoesNotContain("Fandom Lore", generic.Message.Body, StringComparison.Ordinal);

        _clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Null(await _service.UpdateSettingsAsync(new UpdateNotificationSettingsRequest { IncludeDetails = true }, CancellationToken.None));
        await _service.DispatchAsync(new NotificationRequest(NotificationEventIds.PluginBroken, "p1", "Fandom Lore stopped working: it crashed."), CancellationToken.None);

        Assert.Contains("Fandom Lore", _email.Sent[1].Message.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task QuietHours_HoldBackOutsideMessages_ButTheInboxStillFills()
    {
        await AddAccountAsync("admin@example.com", administrator: true);
        // The test clock reads noon, local time is UTC, so a window across noon is quiet.
        await _service.UpdateSettingsAsync(new UpdateNotificationSettingsRequest
        {
            QuietHours = new NotificationQuietHoursDto { Enabled = true, Start = "11:00", End = "13:00" },
        }, CancellationToken.None);

        await _service.DispatchAsync(new NotificationRequest(NotificationEventIds.BackupFailed, "manual", "x"), CancellationToken.None);

        Assert.Empty(_email.Sent);
        Assert.Single(await _repository.ListAsync(10));
    }

    [Fact]
    public async Task AQuietWindowThatCrossesMidnight_IsQuietAtNightAndNotByDay()
    {
        var settings = new NotificationSettingsDocument { QuietHoursEnabled = true, QuietStart = "22:00", QuietEnd = "07:00" };

        Assert.True(settings.IsQuiet(new DateTimeOffset(2026, 10, 21, 23, 30, 0, TimeSpan.Zero)));
        Assert.True(settings.IsQuiet(new DateTimeOffset(2026, 10, 21, 6, 59, 0, TimeSpan.Zero)));
        Assert.False(settings.IsQuiet(new DateTimeOffset(2026, 10, 21, 7, 0, 0, TimeSpan.Zero)));
        Assert.False(settings.IsQuiet(new DateTimeOffset(2026, 10, 21, 12, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public async Task OutsideMessages_AreLimitedToOneAMinutePerEvent_AndTheNextOneSaysHowManyWereHeldBack()
    {
        await AddAccountAsync("admin@example.com", administrator: true);
        var request = new NotificationRequest(NotificationEventIds.BackupFailed, "manual", "Detail.");

        await _service.DispatchAsync(request, CancellationToken.None);
        await _service.DispatchAsync(request, CancellationToken.None);
        await _service.DispatchAsync(request, CancellationToken.None);
        _clock.Advance(TimeSpan.FromSeconds(61));
        await _service.DispatchAsync(request, CancellationToken.None);

        Assert.Equal(2, _email.Sent.Count);
        Assert.Contains("2 more like this were held back", _email.Sent[1].Message.Body, StringComparison.Ordinal);
        var inbox = Assert.Single(await _repository.ListAsync(10));
        Assert.Equal(4, inbox.OccurrenceCount);
    }

    [Fact]
    public async Task AChannelThatKeepsFailing_SwitchesItselfOff_ShowsNeedsAttention_AndStopsBeingTried()
    {
        await AddAccountAsync("admin@example.com", administrator: true);
        _email.Fail = true;

        for (var i = 0; i < NotificationService.FailuresBeforeOff + 2; i++)
        {
            await _service.DispatchAsync(new NotificationRequest(NotificationEventIds.BackupFailed, "manual", "x"), CancellationToken.None);
            _clock.Advance(TimeSpan.FromMinutes(2));
        }

        Assert.Equal(NotificationService.FailuresBeforeOff, _email.Attempts);
        var settings = await _service.GetSettingsAsync(CancellationToken.None);
        var channel = Assert.Single(settings.Channels, candidate => candidate.Id == NotificationChannelIds.Email);
        Assert.Equal(NotificationChannelStates.Off, channel.State);
        Assert.Contains("Switched off", channel.StateLabel, StringComparison.Ordinal);

        _email.Fail = false;
        await _service.EnableChannelAsync(NotificationChannelIds.Email, CancellationToken.None);
        await _service.DispatchAsync(new NotificationRequest(NotificationEventIds.BackupFailed, "manual", "x"), CancellationToken.None);
        Assert.Equal(NotificationService.FailuresBeforeOff + 1, _email.Attempts);
    }

    [Fact]
    public async Task AChannelThatIsNotSetUp_IsSkippedAndNeverCountedAsFailing()
    {
        await AddAccountAsync("admin@example.com", administrator: true);
        _email.Configured = false;

        await _service.DispatchAsync(new NotificationRequest(NotificationEventIds.BackupFailed, "manual", "x"), CancellationToken.None);

        Assert.Equal(0, _email.Attempts);
        Assert.Null(await _repository.GetChannelStateAsync(NotificationChannelIds.Email));
    }

    [Fact]
    public async Task SendTest_GoesStraightToTheChannel_AndAWorkingTestTurnsASwitchedOffChannelBackOn()
    {
        _email.Fail = true;
        await _repository.RecordChannelResultAsync(NotificationChannelIds.Email, false, "x", Start, 1);
        _email.Fail = false;

        var result = await _service.SendTestAsync(NotificationChannelIds.Email, new NotificationRecipient(Guid.NewGuid(), "me@example.com"), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(["me@example.com"], Assert.Single(_email.Sent).Recipients);
        Assert.Equal(NotificationChannelStates.Healthy, (await _repository.GetChannelStateAsync(NotificationChannelIds.Email))!.State);
    }

    [Fact]
    public async Task SettingsThatNameAnUnknownEventOrChannel_AreRefusedAndNothingIsSaved()
    {
        var unknownEvent = await _service.UpdateSettingsAsync(new UpdateNotificationSettingsRequest { Routes = { ["nope"] = [NotificationChannelIds.Email] } }, CancellationToken.None);
        var unknownChannel = await _service.UpdateSettingsAsync(new UpdateNotificationSettingsRequest { Routes = { [NotificationEventIds.BackupFailed] = ["pager"] } }, CancellationToken.None);
        var badTime = await _service.UpdateSettingsAsync(new UpdateNotificationSettingsRequest { QuietHours = new NotificationQuietHoursDto { Enabled = true, Start = "late", End = "07:00" } }, CancellationToken.None);

        Assert.NotNull(unknownEvent);
        Assert.NotNull(unknownChannel);
        Assert.NotNull(badTime);
        Assert.Null(await _repository.GetSettingAsync("settings"));
    }

    [Fact]
    public async Task AnEventRoutedNowhere_LeavesNoTrace()
    {
        await _service.DispatchAsync(new NotificationRequest(NotificationEventIds.IngestionCompleted, "", "x"), CancellationToken.None);

        Assert.Empty(await _repository.ListAsync(10));
        Assert.Empty(_email.Sent);
    }

    [Fact]
    public async Task TheStartupToolsCheck_RaisesOneEventPerMissingRequiredTool_AndNoneForOptionalOnes()
    {
        var publisher = new SpyPublisher();
        var tools = new FixedTools(new ToolRequirementsDto(ToolInstallKinds.Source, Start, true,
        [
            new ToolRequirementDto("ffmpeg", "FFmpeg", "tool", ToolRequirementLevels.Required, ToolRequirementStates.Missing, "Video cannot be prepared.", null, "Install FFmpeg.", null, 3, true, false, false, null),
            new ToolRequirementDto("tesseract", "Tesseract", "tool", ToolRequirementLevels.Optional, ToolRequirementStates.Missing, "Scanned pages are not read.", null, "Install it.", null, 0, true, false, false, null),
        ]));
        var service = new ToolRequirementsStartupService(tools, new NoAutoInstaller(), publisher, NullLogger<ToolRequirementsStartupService>.Instance);

        await service.StartAsync(CancellationToken.None);
        await Task.Delay(300);
        await service.StopAsync(CancellationToken.None);

        var raised = Assert.Single(publisher.Requests);
        Assert.Equal(NotificationEventIds.ToolMissing, raised.EventId);
        Assert.Equal("ffmpeg", raised.Subject);
        Assert.Contains("Video cannot be prepared.", raised.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void ABrokenPluginProblem_IsReportedOnce_AndNeedsAttentionIsNotReportedAtAll()
    {
        var reports = new List<string>();
        var status = new PluginHostStatus(TimeProvider.System, reports.Add);

        status.SetProblem("storage", PluginHostSeverity.NeedsAttention, "Almost full");
        status.SetProblem("events", PluginHostSeverity.Broken, "Stopped receiving events");
        status.SetProblem("events", PluginHostSeverity.Broken, "Stopped receiving events");
        status.SetProblem("events", PluginHostSeverity.Broken, "Stopped after crashing");

        Assert.Equal(["Stopped receiving events", "Stopped after crashing"], reports);
    }

    [Fact]
    public async Task ThePublisher_NeverBlocks_AndDropsTheOldestWhenAFloodFillsTheLine()
    {
        var queue = new NotificationQueue();

        for (var i = 0; i < 1000; i++)
        {
            queue.Publish(new NotificationRequest(NotificationEventIds.BackupFailed, i.ToString()));
        }

        var kept = new List<string>();
        while (queue.Reader.TryRead(out var request))
        {
            kept.Add(request.Subject);
        }

        Assert.Equal(256, kept.Count);
        Assert.Equal("999", kept[^1]);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task ANotification_IsAlsoOfferedToWebhooksAndPlugins_WithOnlyTheGenericTitle()
    {
        await _service.DispatchAsync(new NotificationRequest(NotificationEventIds.BackupFailed, "manual", "The drive at D: is full."), CancellationToken.None);

        var published = Assert.Single(_events.Drafts);
        Assert.Equal(NotificationEventIds.BackupFailed, published.EventType);
        var payload = published.Payload.GetRawText();
        Assert.Contains("A backup did not finish", payload);
        Assert.DoesNotContain("D:", payload);
    }

    [Fact]
    public async Task WithIncludeDetailsOn_TheEventCarriesTheDetailToo()
    {
        var settings = await _settings.LoadAsync(CancellationToken.None);
        settings.IncludeDetails = true;
        await _settings.SaveAsync(settings, CancellationToken.None);

        await _service.DispatchAsync(new NotificationRequest(NotificationEventIds.BackupFailed, "manual", "The drive at D: is full."), CancellationToken.None);

        Assert.Contains("D:", Assert.Single(_events.Drafts).Payload.GetRawText());
    }

    [Fact]
    public void EveryNotificationEvent_IsInTheOneEventListThatWebhooksAndPluginsUse()
    {
        foreach (var definition in NotificationEventCatalogue.All)
        {
            Assert.True(MediaEngine.Api.Services.Events.ApplicationEventRegistry.IsKnownType(definition.Id), definition.Id);
        }

        // An event that is only a notification today is still there for webhooks: the list is one list.
        Assert.True(MediaEngine.Api.Services.Events.ApplicationEventRegistry.IsKnownType(NotificationEventIds.DiskLow));
    }

    private sealed class SpyEvents : MediaEngine.Domain.Events.IApplicationEventProducer
    {
        public List<MediaEngine.Domain.Events.ApplicationEventDraft> Drafts { get; } = [];

        public Task PublishAsync(MediaEngine.Domain.Events.ApplicationEventDraft value, CancellationToken ct = default)
        {
            Drafts.Add(value);
            return Task.CompletedTask;
        }
    }

    private async Task AddAccountAsync(string? email, bool administrator, bool enabled = true)
    {
        // The administrator role is the profile's, so each sign-in opens a profile of its own.
        var accountId = Guid.NewGuid();
        var profile = new MediaEngine.Domain.Aggregates.Profile
        {
            Id = Guid.NewGuid(),
            DisplayName = email ?? "Administrator",
            Role = MediaEngine.Domain.Enums.ProfileRole.StandardUser,
            CreatedAt = Start,
        };
        await _accounts.CreateAccountAsync(
            new Account
            {
                Id = accountId,
                Email = email,
                NormalizedEmail = email?.ToUpperInvariant(),
                IsEnabled = enabled,
                CreatedAt = Start,
                UpdatedAt = Start,
            },
            new TestGrant(accountId, profile.Id, IsDefault: true, AdminEnabled: administrator),
            newProfile: profile,
            // Every server administrator lives in the Library's own household.
            householdId: administrator ? _household : null);
        if (administrator)
        {
            _household ??= await _accounts.GetHouseholdIdAsync(_db, accountId);
        }
    }

    private sealed record SentMessage(OutgoingNotification Message, string[] Recipients);

    private sealed class SpyChannel(string id, string name) : INotificationChannel
    {
        public List<SentMessage> Sent { get; } = [];

        public int Attempts { get; private set; }

        public bool Fail { get; set; }

        public bool Configured { get; set; } = true;

        public string Id { get; } = id;

        public string Name { get; } = name;

        public bool IsConfigured => Configured;

        public Task<ChannelSendResult> SendAsync(OutgoingNotification message, IReadOnlyList<NotificationRecipient> recipients, CancellationToken ct)
        {
            Attempts++;
            if (Fail)
            {
                return Task.FromResult(new ChannelSendResult(false, "The mail server could not be reached."));
            }

            Sent.Add(new SentMessage(message, [.. recipients.Select(recipient => recipient.Email)]));
            return Task.FromResult(ChannelSendResult.Sent);
        }
    }

    private sealed class NoAutoInstaller : IToolAutoInstaller
    {
        public Task<ToolRequirementsDto> StartMissingRequiredAsync(ToolRequirementsDto tools, CancellationToken ct) => Task.FromResult(tools);
    }

    private sealed class SpyPublisher : INotificationPublisher
    {
        public List<NotificationRequest> Requests { get; } = [];

        public void Publish(NotificationRequest request) => Requests.Add(request);
    }

    private sealed class FixedTools(ToolRequirementsDto result) : IToolRequirementsService
    {
        public Task<ToolRequirementsDto> GetAsync(CancellationToken ct) => Task.FromResult(result);

        public Task<ToolRequirementsDto> CheckAsync(CancellationToken ct) => Task.FromResult(result);
    }
}
