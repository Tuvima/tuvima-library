using Bunit;
using MediaEngine.Contracts.Settings;
using MediaEngine.Contracts.System;
using MediaEngine.Web.Components.Settings;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Web.Tests;

/// <summary>
/// System > Logs (technical log and retention) and the Scheduled tasks card on Tools and requirements.
/// </summary>
public sealed class LogsPageTests : AsyncBunitContext
{
    private static readonly DateTimeOffset Now = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);

    private readonly List<TechnicalLogRequest> _logRequests = [];
    private readonly List<LogRetentionSettingsDto> _saves = [];
    private Func<TechnicalLogRequest, TechnicalLogPageDto?> _onLog = _ => new TechnicalLogPageDto();
    private Func<ScheduledTasksResponse?> _onTasks = () => null;
    private Func<LogRetentionStatusDto?> _onRetention = () => Status();
    private Func<LogRetentionSettingsDto, LogRetentionStatusDto?> _onSave = settings => Status(settings);

    public LogsPageTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        Services.AddLocalization();
        var api = EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetTechnicalLogAsync), args =>
            {
                var request = (TechnicalLogRequest)args![0]!;
                _logRequests.Add(request);
                return Task.FromResult(_onLog(request));
            });
            stub.SetHandler(nameof(IEngineApiClient.GetScheduledTasksAsync), _ => Task.FromResult(_onTasks()));
            stub.SetHandler(nameof(IEngineApiClient.GetLogRetentionAsync), _ => Task.FromResult(_onRetention()));
            stub.SetHandler(nameof(IEngineApiClient.UpdateLogRetentionAsync), args =>
            {
                var settings = (LogRetentionSettingsDto)args![0]!;
                _saves.Add(settings);
                return Task.FromResult(_onSave(settings));
            });
            stub.SetHandler(nameof(IEngineApiClient.GetToolRequirementsAsync),
                _ => Task.FromResult<ToolRequirementsDto?>(new ToolRequirementsDto("native", DateTimeOffset.UtcNow, false, [])));
        });
        Services.AddSingleton(api);
        Services.AddSingleton(serviceProvider => new UIOrchestratorService(
            api,
            new UniverseStateContainer(),
            new ActiveProfileSessionService(serviceProvider.GetRequiredService<Microsoft.JSInterop.IJSRuntime>(), api),
            new ConfigurationBuilder().Build(),
            NullLogger<UIOrchestratorService>.Instance));
        Services.AddSingleton(new DeviceContextService(api));
    }

    // ── Menu ─────────────────────────────────────────────────────────────────

    [Fact]
    public void System_ListsLogsBetweenToolsAndBackup_WithItsOwnAddress()
    {
        var system = SettingsNav.MenuGroups.SelectMany(group => group.Nodes).Single(node => node.Label == "System");

        Assert.Equal(["Tools and requirements", "Logs", "Backup and recovery"], system.Children.Select(child => child.Label));
        var logs = system.Children.Single(child => child.Label == "Logs");
        Assert.Equal(SettingsSection.AdminOverview, logs.Section);
        Assert.Equal("logs", logs.Subsection);
        Assert.Equal("/settings/system/logs", SettingsNav.RouteFor(SettingsSection.AdminOverview, "logs"));
    }

    [Fact]
    public void Logs_IsAnAdministratorPage_NotOfferedOnAPhone()
    {
        Assert.False(SettingsNav.IsMobileRouteAvailable(SettingsSection.AdminOverview, "logs"));
        Assert.True(SettingsNav.IsMobileRouteAvailable(SettingsSection.AdminOverview));
        Assert.DoesNotContain(
            SettingsNav.FilteredMenuGroups(administrationAllowed: false).SelectMany(group => SettingsNav.Flatten(group.Nodes)),
            node => node.Label == "Logs");
    }

    [Fact]
    public void BreadcrumbAndTitle_ForLogs_ReadSystemThenLogs()
    {
        var location = SettingsNav.FindMenuLocation(SettingsSection.AdminOverview, "logs");

        Assert.NotNull(location);
        Assert.Equal(["System", "Logs"], location.Trail.Select(node => node.Label));
    }

    // ── Logs page ────────────────────────────────────────────────────────────

    [Fact]
    public void LogsPage_OpensOnTheTechnicalLog_WithRetentionAsTheOtherTab()
    {
        var cut = Render<LogsPage>();

        var tabs = cut.FindAll("[role=tab]").Select(tab => tab.TextContent.Trim()).ToArray();
        Assert.Equal(["Technical log", "Retention"], tabs);
        Assert.Contains("Technical log", cut.Find("[aria-selected=true]").TextContent, StringComparison.Ordinal);
        Assert.NotEmpty(_logRequests);
    }

    [Fact]
    public void LogsPage_WithTheRetentionTabRequested_OpensOnRetention()
    {
        var cut = Render<LogsPage>(parameters => parameters.Add(p => p.Tab, "retention"));

        Assert.Contains("Retention", cut.Find("[aria-selected=true]").TextContent, StringComparison.Ordinal);
        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid=retention-note]")));
    }

    [Fact]
    public void LogsPage_PickingTheRetentionTab_ShowsTheLimits()
    {
        var cut = Render<LogsPage>();

        cut.FindAll("[role=tab]").Single(tab => tab.TextContent.Contains("Retention", StringComparison.Ordinal)).Click();

        cut.WaitForAssertion(() => Assert.Equal("90", cut.Find("[data-testid=retention-activity-days]").GetAttribute("value")));
    }

    // ── Technical log ────────────────────────────────────────────────────────

    [Fact]
    public void TechnicalLog_ShowsEachLineWithTimeLevelAreaAndMessage_NewestFirst()
    {
        _onLog = _ => new TechnicalLogPageDto
        {
            Lines =
            [
                Line("Sign-in failed", "error", "Security", new DateTimeOffset(2026, 10, 19, 9, 10, 0, TimeSpan.Zero)),
                Line("Scan started", "information", "Ingestion", new DateTimeOffset(2026, 10, 19, 9, 0, 0, TimeSpan.Zero)),
            ],
        };

        var cut = Render<TechnicalLogPanel>();

        var lines = cut.FindAll("[data-testid=technical-log-line]");
        Assert.Equal(2, lines.Count);
        Assert.Contains("Sign-in failed", lines[0].TextContent, StringComparison.Ordinal);
        Assert.Contains("Error", lines[0].TextContent, StringComparison.Ordinal);
        Assert.Contains("Security", lines[0].TextContent, StringComparison.Ordinal);
        Assert.Contains("Scan started", lines[1].TextContent, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("[data-testid=technical-log-error]"));
    }

    [Fact]
    public void TechnicalLog_AnErrorsDetailLines_AreKeptBehindShowDetails()
    {
        _onLog = _ => new TechnicalLogPageDto
        {
            Lines = [Line("Could not read file\nSystem.IO.IOException: busy\n   at Foo()", "error", "Ingestion", Now)],
        };

        var cut = Render<TechnicalLogPanel>();

        var line = cut.Find("[data-testid=technical-log-line]");
        Assert.Contains("Could not read file", line.TextContent, StringComparison.Ordinal);
        Assert.Contains("Show details", line.TextContent, StringComparison.Ordinal);
        Assert.Contains("System.IO.IOException: busy", cut.Find("details pre").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void TechnicalLog_FirstAsksForTheLastDay_AtEveryLevelAndArea_AHundredLinesAtATime()
    {
        Render<TechnicalLogPanel>();

        var request = Assert.Single(_logRequests);
        Assert.NotNull(request.From);
        Assert.InRange((DateTimeOffset.Now - request.From!.Value).TotalHours, 23.9, 24.1);
        Assert.Null(request.Level);
        Assert.Null(request.Area);
        Assert.Null(request.Search);
        Assert.Null(request.Cursor);
        Assert.Equal(100, request.Limit);
    }

    [Fact]
    public void TechnicalLog_LoadOlderLines_AsksAfterTheCursor_AndAddsBelowWhatIsShown()
    {
        _onLog = request => request.Cursor is null
            ? new TechnicalLogPageDto { Lines = [Line("newer", "information", "Other", Now)], NextCursor = "next-page" }
            : new TechnicalLogPageDto { Lines = [Line("older", "information", "Other", Now.AddHours(-1))] };
        var cut = Render<TechnicalLogPanel>();

        cut.FindAll("button").Single(button => button.TextContent.Contains("Load older lines", StringComparison.Ordinal)).Click();

        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[data-testid=technical-log-line]").Count));
        Assert.Equal("next-page", _logRequests[^1].Cursor);
        var lines = cut.FindAll("[data-testid=technical-log-line]");
        Assert.Contains("newer", lines[0].TextContent, StringComparison.Ordinal);
        Assert.Contains("older", lines[1].TextContent, StringComparison.Ordinal);
        Assert.DoesNotContain(cut.FindAll("button"), button => button.TextContent.Contains("Load older lines", StringComparison.Ordinal));
    }

    [Fact]
    public void TechnicalLog_Searching_AsksAgainFromTheStartWithTheText()
    {
        var cut = Render<TechnicalLogPanel>();
        _logRequests.Clear();

        cut.Find("input[placeholder^='Search']").Input("dune");

        cut.WaitForAssertion(() => Assert.Equal("dune", Assert.Single(_logRequests).Search), TimeSpan.FromSeconds(3));
        Assert.Null(_logRequests[0].Cursor);
    }

    [Fact]
    public void TechnicalLog_WithNoMatchingLines_SaysSo()
    {
        var cut = Render<TechnicalLogPanel>();

        Assert.Contains("No log lines match", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void TechnicalLog_WhenTheEngineCannotBeAsked_SaysItCouldNotBeReadAndOffersTryAgain()
    {
        _onLog = _ => null;
        var cut = Render<TechnicalLogPanel>();

        Assert.Contains("could not be read", cut.Find("[data-testid=technical-log-error]").TextContent, StringComparison.Ordinal);
        _onLog = _ => new TechnicalLogPageDto { Lines = [Line("back", "information", "Other", Now)] };
        cut.Find("[data-testid=technical-log-error] button").Click();

        cut.WaitForAssertion(() => Assert.Contains("back", cut.Find("[data-testid=technical-log-line]").TextContent, StringComparison.Ordinal));
    }

    // ── Retention ────────────────────────────────────────────────────────────

    [Fact]
    public void Retention_ShowsTheFourLimits_WhatIsStored_AndThatPruningIsQuiet()
    {
        _onRetention = () => Status(activityEvents: 412_345, logFiles: 12, logBytes: 340L * 1024 * 1024);

        var cut = Render<LogRetentionPanel>();

        Assert.Equal("90", cut.Find("[data-testid=retention-activity-days]").GetAttribute("value"));
        Assert.Equal("500000", cut.Find("[data-testid=retention-activity-max]").GetAttribute("value"));
        Assert.Equal("90", cut.Find("[data-testid=retention-log-days]").GetAttribute("value"));
        Assert.Equal("1024", cut.Find("[data-testid=retention-log-mb]").GetAttribute("value"));
        Assert.Contains("412,345", cut.Find("[data-testid=retention-activity-usage]").TextContent, StringComparison.Ordinal);
        Assert.Contains("12 files", cut.Find("[data-testid=retention-log-usage]").TextContent, StringComparison.Ordinal);
        Assert.Contains("removed quietly every night", cut.Find("[data-testid=retention-note]").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void Retention_Saving_SendsAllFourLimits_AndConfirmsTheNextCleanUpUsesThem()
    {
        var cut = Render<LogRetentionPanel>();
        cut.Find("[data-testid=retention-activity-days]").Input("30");
        cut.Find("[data-testid=retention-activity-max]").Input("250000");
        cut.Find("[data-testid=retention-log-days]").Input("14");
        cut.Find("[data-testid=retention-log-mb]").Input("512");

        cut.Find("[data-testid=retention-save]").Click();

        cut.WaitForAssertion(() => Assert.Contains("next nightly clean-up", cut.Find("[data-testid=retention-saved]").TextContent, StringComparison.Ordinal));
        var saved = Assert.Single(_saves);
        Assert.Equal((30, 250_000, 14, 512), (saved.ActivityRetentionDays, saved.ActivityMaxEvents, saved.LogRetentionDays, saved.LogMaxTotalMegabytes));
    }

    [Theory]
    [InlineData("retention-activity-days", "0")]
    [InlineData("retention-activity-days", "9999")]
    [InlineData("retention-activity-max", "10")]
    [InlineData("retention-log-days", "0")]
    [InlineData("retention-log-mb", "5")]
    public void Retention_ALimitOutOfRange_IsExplainedAndNothingIsSent(string field, string value)
    {
        var cut = Render<LogRetentionPanel>();
        cut.Find($"[data-testid={field}]").Input(value);

        cut.Find("[data-testid=retention-save]").Click();

        Assert.False(string.IsNullOrWhiteSpace(cut.Find("[data-testid=retention-error]").TextContent));
        Assert.Empty(_saves);
        Assert.Empty(cut.FindAll("[data-testid=retention-saved]"));
    }

    [Fact]
    public void Retention_ANonNumberInAField_IsExplainedAndNothingIsSent()
    {
        var cut = Render<LogRetentionPanel>();
        cut.Find("[data-testid=retention-log-days]").Input("soon");

        cut.Find("[data-testid=retention-save]").Click();

        Assert.Contains("whole number", cut.Find("[data-testid=retention-error]").TextContent, StringComparison.Ordinal);
        Assert.Empty(_saves);
    }

    [Fact]
    public void Retention_EditingAfterAnError_ClearsTheError()
    {
        var cut = Render<LogRetentionPanel>();
        cut.Find("[data-testid=retention-log-days]").Input("0");
        cut.Find("[data-testid=retention-save]").Click();
        Assert.NotEmpty(cut.FindAll("[data-testid=retention-error]"));

        cut.Find("[data-testid=retention-log-days]").Input("30");

        Assert.Empty(cut.FindAll("[data-testid=retention-error]"));
    }

    [Fact]
    public void Retention_WhenTheEngineRefusesTheSave_SaysSoAndShowsNoConfirmation()
    {
        _onSave = _ => null;
        var cut = Render<LogRetentionPanel>();

        cut.Find("[data-testid=retention-save]").Click();

        cut.WaitForAssertion(() => Assert.Contains("could not be saved", cut.Find("[data-testid=retention-error]").TextContent, StringComparison.Ordinal));
        Assert.Empty(cut.FindAll("[data-testid=retention-saved]"));
    }

    [Fact]
    public void Retention_WhenTheEngineCannotBeAsked_ShowsAnErrorStateWithRetry()
    {
        _onRetention = () => null;

        var cut = Render<LogRetentionPanel>();

        Assert.Contains("Could not load the retention settings", cut.Markup, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("[data-testid=retention-save]"));
    }

    [Fact]
    public void RetentionRanges_TheFormUses_AreTheSameOnesTheEngineEnforces()
    {
        // The panel validates with LogRetentionLimits, the type the Engine action and configuration validator also use.
        Assert.Null(LogRetentionLimits.Validate(30, 250_000, 14, 512));
        Assert.NotNull(LogRetentionLimits.Validate(0, 250_000, 14, 512));
    }

    // ── Scheduled tasks ──────────────────────────────────────────────────────

    [Fact]
    public void ScheduledTasks_ListsEveryTaskWithWhatItDoesWhenItRunsAndItsLastAndNextRun()
    {
        _onTasks = () => new ScheduledTasksResponse
        {
            EngineStartedAt = Now.AddHours(-8),
            Tasks =
            [
                Scheduled("backup", "Back up", "Copies the data store.", "Every day at 2:00", lastRun: Now.AddHours(-2), result: "done", next: Now.AddHours(22)),
                Scheduled("tidy", "Tidy up", "Removes old files.", "Every day at 3:00"),
            ],
        };

        var cut = Render<ScheduledTasksCard>();

        var items = cut.FindAll("[data-testid=scheduled-task]");
        Assert.Equal(["backup", "tidy"], items.Select(item => item.GetAttribute("data-task-id")));
        Assert.Contains("Back up", items[0].TextContent, StringComparison.Ordinal);
        Assert.Contains("Copies the data store.", items[0].TextContent, StringComparison.Ordinal);
        Assert.Equal("Every day at 2:00", items[0].QuerySelector("[data-testid=task-schedule]")!.TextContent);
        Assert.StartsWith("Done ", items[0].QuerySelector("[data-testid=task-last-run]")!.TextContent, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(items[0].QuerySelector("[data-testid=task-next-run]")!.TextContent));
    }

    [Fact]
    public void ScheduledTasks_ATaskThatHasNotRunSinceTheEngineStarted_SaysSo()
    {
        _onTasks = () => new ScheduledTasksResponse { Tasks = [Scheduled("tidy", "Tidy up", "Removes old files.", "Every day at 3:00")] };

        var cut = Render<ScheduledTasksCard>();

        Assert.Equal("Not run since the Engine started", cut.Find("[data-testid=task-last-run]").TextContent);
        Assert.Equal("Not scheduled yet", cut.Find("[data-testid=task-next-run]").TextContent);
    }

    [Fact]
    public void ScheduledTasks_AFailedRun_SaysFailedWithTheShortReason()
    {
        _onTasks = () => new ScheduledTasksResponse
        {
            Tasks = [Scheduled("backup", "Back up", "Copies the data store.", "Every day at 2:00", lastRun: Now, result: "failed", reason: "The disk is full.")],
        };

        var cut = Render<ScheduledTasksCard>();

        var last = cut.Find("[data-testid=task-last-run]").TextContent;
        Assert.StartsWith("Failed ", last, StringComparison.Ordinal);
        Assert.EndsWith(": The disk is full", last, StringComparison.Ordinal);
    }

    [Fact]
    public void ScheduledTasks_WhenTheEngineCannotBeAsked_SaysTheListIsUnavailable()
    {
        _onTasks = () => null;

        var cut = Render<ScheduledTasksCard>();

        Assert.Contains("Scheduled tasks are unavailable", cut.Markup, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("[data-testid=scheduled-task]"));
    }

    [Fact]
    public void ToolsAndRequirements_HasTheScheduledTasksCard()
    {
        _onTasks = () => new ScheduledTasksResponse { Tasks = [Scheduled("tidy", "Tidy up", "Removes old files.", "Every day at 3:00")] };

        var cut = Render<ToolsRequirementsTab>();

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-testid=scheduled-task]")));
        Assert.Contains("Scheduled tasks", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void ScheduledTaskText_NamesTodayTomorrowAndYesterdayRelativeToNow()
    {
        var now = new DateTimeOffset(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);

        Assert.StartsWith("today at ", ScheduledTaskText.Moment(now, now), StringComparison.Ordinal);
        Assert.StartsWith("tomorrow at ", ScheduledTaskText.Moment(now.AddDays(1), now), StringComparison.Ordinal);
        Assert.StartsWith("yesterday at ", ScheduledTaskText.Moment(now.AddDays(-1), now), StringComparison.Ordinal);
        Assert.Contains(" at ", ScheduledTaskText.Moment(now.AddDays(5), now), StringComparison.Ordinal);
        Assert.DoesNotContain("today", ScheduledTaskText.Moment(now.AddDays(5), now), StringComparison.Ordinal);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static TechnicalLogLineDto Line(string message, string level, string area, DateTimeOffset time) =>
        new() { Message = message, Level = level, Area = area, Time = time };

    private static ScheduledTaskDto Scheduled(
        string id, string name, string description, string schedule,
        DateTimeOffset? lastRun = null, string? result = null, string? reason = null, DateTimeOffset? next = null) =>
        new()
        {
            Id = id,
            Name = name,
            Description = description,
            Schedule = schedule,
            LastRunAt = lastRun,
            LastRunResult = result,
            LastRunReason = reason,
            NextRunAt = next,
        };

    private static LogRetentionStatusDto Status(
        LogRetentionSettingsDto? settings = null, long activityEvents = 1_000, int logFiles = 3, long logBytes = 5_000_000) =>
        new()
        {
            Settings = settings ?? new LogRetentionSettingsDto
            {
                ActivityRetentionDays = 90,
                ActivityMaxEvents = 500_000,
                LogRetentionDays = 90,
                LogMaxTotalMegabytes = 1024,
            },
            ActivityEventCount = activityEvents,
            LogFileCount = logFiles,
            LogTotalBytes = logBytes,
        };
}
