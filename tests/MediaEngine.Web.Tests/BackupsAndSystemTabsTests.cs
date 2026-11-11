using Bunit;
using MediaEngine.Contracts.Backups;
using MediaEngine.Contracts.System;
using MediaEngine.Web.Components.Settings;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Services.Ui;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>Settings and Setup simplified, WP13: Backups (Backups, Destinations, Advanced) and System (Health, Scheduled tasks, Logs, Advanced).</summary>
public sealed class BackupsAndSystemTabsTests : AsyncBunitContext
{
    private static readonly BackupArchiveDto Archive = new("tuvima-backup-20261022-080000.zip", DateTimeOffset.UtcNow.AddHours(-3), 3_145_728);

    private BackupStateDto? _state = new(0, 90, 0, null);
    private int? _savedDays;
    private bool _saveFails;
    private readonly List<RestoreFromDestinationRequest> _destinationRestores = [];
    private readonly List<string> _localRestores = [];
    private readonly BackupSettingsResponse _settings = new()
    {
        KeepLast = 10,
        LastRestoreCheckResult = "Passed",
        Destinations =
        [
            new() { Id = BackupDestinationIds.Local, Name = "This computer", IsConfigured = true, Enabled = true },
            new() { Id = "nas", Name = "Home NAS", Kind = BackupDestinationKinds.Share, IsConfigured = true, Enabled = true },
            new() { Id = "cloud", Name = "Cloud", Kind = BackupDestinationKinds.S3, IsConfigured = true, Enabled = true },
        ],
    };

    public BackupsAndSystemTabsTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        Services.AddLocalization();
        Services.AddSingleton<IAppToastService>(new AppToastService());
        var api = EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetBackupsAsync), _ => Task.FromResult<IReadOnlyList<BackupArchiveDto>>([Archive]));
            stub.SetHandler(nameof(IEngineApiClient.GetBackupStateAsync), _ => Task.FromResult(_state));
            stub.SetHandler(nameof(IEngineApiClient.GetBackupSettingsAsync), _ => Task.FromResult<BackupSettingsResponse?>(_settings));
            stub.SetHandler(nameof(IEngineApiClient.GetDestinationBackupsAsync), args => Task.FromResult<DestinationBackupsResponse?>(
                (string)args![0]! switch
                {
                    "nas" => new DestinationBackupsResponse { Items = [new() { Name = "tuvima-backup-20261021-080000.zip", SizeBytes = 2_000_000, ModifiedAt = DateTimeOffset.UtcNow.AddDays(-1) }] },
                    _ => null,
                }));
            stub.SetHandler(nameof(IEngineApiClient.RestoreFromDestinationAsync), args =>
            {
                _destinationRestores.Add((RestoreFromDestinationRequest)args![0]!);
                return Task.FromResult<ScheduleRestoreResultDto?>(new ScheduleRestoreResultDto(true, true, "Scheduled."));
            });
            stub.SetHandler(nameof(IEngineApiClient.ScheduleRestoreAsync), args =>
            {
                _localRestores.Add((string)args![0]!);
                return Task.FromResult<ScheduleRestoreResultDto?>(new ScheduleRestoreResultDto(true, true, "Scheduled."));
            });
            stub.SetHandler(nameof(IEngineApiClient.SetBackupWaitingPeriodAsync), args =>
            {
                _savedDays = (int)args![0]!;
                return Task.FromResult<BackupStateDto?>(_saveFails ? null : new BackupStateDto(0, _savedDays.Value, 0, null));
            });
        });
        Services.AddSingleton(api);
        Services.AddSingleton(new DeviceContextService(api));
    }

    [Fact]
    public void TheBackupsTab_HasOneRestoreList_WithEveryDestinationsBackups_NewestFirst()
    {
        var cut = Render<BackupRecoveryPanel>();

        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll(".backup-list__row").Count));
        Assert.Single(cut.FindAll("[data-testid='restore-list']"));
        var rows = cut.FindAll(".backup-list__row").Select(row => row.TextContent).ToList();
        Assert.Contains("This computer", rows[0]);
        Assert.Contains("Home NAS", rows[1]);
        Assert.Contains("Could not read the backups held at Cloud.", cut.Find("[data-testid='destination-unreachable']").TextContent);
    }

    [Fact]
    public void TheBackupsTab_ShowsTheSchedule_ButNotTheDestinationsOrTheAdvancedFacts()
    {
        var cut = Render<BackupRecoveryPanel>();

        cut.WaitForElement("[data-testid='backup-schedule']");
        Assert.Empty(cut.FindAll("[data-testid='backup-destinations']"));
        Assert.Empty(cut.FindAll("[data-testid='waiting-period']"));
        Assert.Empty(cut.FindAll("[data-testid='archive-facts']"));
    }

    [Fact]
    public void RestoringFromADestinationRow_SchedulesThatDestinationsBackup()
    {
        var cut = Render<BackupRecoveryPanel>();
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll(".backup-list__row").Count));

        cut.Find("[data-testid='restore-row-nas']").QuerySelectorAll("button").Single(button => button.TextContent.Contains("Restore")).Click();
        cut.FindAll("button").Single(button => button.TextContent.Contains("Validate and schedule")).Click();

        cut.WaitForAssertion(() => Assert.Equal(("nas", "tuvima-backup-20261021-080000.zip"), (_destinationRestores.Single().DestinationId, _destinationRestores.Single().Name)));
        Assert.Empty(_localRestores);
    }

    [Fact]
    public void TheDestinationsTab_ListsDestinations_WithNoRestorePickerAndNoSchedule()
    {
        var cut = Render<BackupDestinationsSection>();

        cut.WaitForElement("[data-testid='backup-destinations']");
        Assert.Contains("Home NAS", cut.Markup);
        Assert.DoesNotContain("Restore from a destination", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Back up automatically", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void TheAdvancedTab_HoldsTheWaitingPeriod_TheMonthlyRestoreCheck_AndTheArchiveFacts()
    {
        var cut = Render<BackupAdvancedPanel>();

        cut.WaitForElement("[data-testid='waiting-period']");
        Assert.Equal(["30 days", "60 days", "90 days", "180 days", "365 days"], cut.Find("[data-testid='waiting-period']").QuerySelectorAll("option").Select(option => option.TextContent));
        Assert.Contains("Monthly restore check: Passed", cut.Find("[data-testid='restore-check']").TextContent);
        Assert.NotEmpty(cut.FindAll("[data-testid='archive-facts']"));
    }

    [Fact]
    public void ChoosingAWaitingPeriod_SavesItStraightAway()
    {
        var cut = Render<BackupAdvancedPanel>();
        cut.WaitForElement("[data-testid='waiting-period']");

        cut.Find("[data-testid='waiting-period']").Change("180");

        cut.WaitForAssertion(() => Assert.Equal(180, _savedDays));
        cut.WaitForAssertion(() => Assert.Equal("180", cut.Find("[data-testid='waiting-period']").GetAttribute("value")));
    }

    [Fact]
    public void AWaitingPeriodThatCouldNotBeSaved_LeavesTheStoredChoiceInPlace()
    {
        _saveFails = true;
        var cut = Render<BackupAdvancedPanel>();
        cut.WaitForElement("[data-testid='waiting-period']");

        var before = cut.FindComponent<AppSelect>().Instance;
        cut.Find("[data-testid='waiting-period']").Change("30");

        cut.WaitForAssertion(() => Assert.Equal(30, _savedDays));
        cut.WaitForAssertion(() => Assert.NotSame(before, cut.FindComponent<AppSelect>().Instance));
        Assert.Equal("90", cut.Find("[data-testid='waiting-period']").GetAttribute("value"));
    }

    [Fact]
    public void AStoredWaitingPeriodThatIsNotOneOfTheChoices_IsShownAsOneMoreChoice()
    {
        _state = new BackupStateDto(0, 45, 0, null);

        var cut = Render<BackupAdvancedPanel>();

        cut.WaitForElement("[data-testid='waiting-period']");
        Assert.Equal(["30 days", "45 days", "60 days", "90 days", "180 days", "365 days"], cut.Find("[data-testid='waiting-period']").QuerySelectorAll("option").Select(option => option.TextContent));
    }

    [Fact]
    public void SettingsPage_RoutesTheBackupsAndSystemTabsToTheirPanels()
    {
        var page = ReadRepoFile("src/MediaEngine.Web/Components/Pages/Settings.razor");

        Assert.Contains("<BackupAdvancedPanel />", page, StringComparison.Ordinal);
        Assert.Contains("<BackupDestinationsSection />", page, StringComparison.Ordinal);
        Assert.DoesNotContain("BackupsAdvancedPending", page, StringComparison.Ordinal);
        var system = page[page.IndexOf("case SettingsSection.AdminOverview:", StringComparison.Ordinal)..page.IndexOf("case SettingsSection.Libraries:", StringComparison.Ordinal)];
        Assert.Contains("<SystemSummaryPage />", system, StringComparison.Ordinal);
        Assert.Contains("<ScheduledTasksPage />", system, StringComparison.Ordinal);
        Assert.Contains("<LogsPage", system, StringComparison.Ordinal);
        Assert.Contains("<LogRetentionPanel />", system, StringComparison.Ordinal);
        Assert.Contains("<LogExportPanel />", system, StringComparison.Ordinal);
    }

    private static string ReadRepoFile(string relativePath) =>
        LocalizedSource.ReadAllText(Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..", relativePath)));
}
