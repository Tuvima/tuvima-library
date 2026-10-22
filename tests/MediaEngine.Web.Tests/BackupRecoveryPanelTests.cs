using Bunit;
using MediaEngine.Contracts.Setup;
using MediaEngine.Contracts.System;
using MediaEngine.Web.Components.Settings;
using MediaEngine.Web.Components.Setup;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>
/// The Backup and recovery screen's plain words (backup and restore spec section 4): what a backup holds, keeping it safe,
/// the exact-file warning, what happened after a restore, the waiting period and the first look at a backup.
/// </summary>
public sealed class BackupRecoveryPanelTests : AsyncBunitContext
{
    private static readonly BackupArchiveDto Archive = new("tuvima-backup-20261022-080000.zip", DateTimeOffset.UtcNow.AddHours(-1), 3_145_728);

    private BackupStateDto? _state = new(0, 90, 0, null);
    private RestoreValidationResultDto? _validation;
    private int? _savedDays;
    private bool _saveFails;
    private bool _backupExists = true;
    private BackupArchiveDto? _created;

    public BackupRecoveryPanelTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        var api = EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetBackupsAsync),
                _ => Task.FromResult<IReadOnlyList<BackupArchiveDto>>(_backupExists ? [Archive] : []));
            stub.SetHandler(nameof(IEngineApiClient.GetBackupStateAsync), _ => Task.FromResult(_state));
            stub.SetHandler(nameof(IEngineApiClient.ValidateRestoreAsync), _ => Task.FromResult(_validation));
            stub.SetHandler(nameof(IEngineApiClient.CreateBackupAsync), _ => Task.FromResult(_created));
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
    public void TheBackupCard_ExplainsWhatABackupHolds_AndToKeepTheFileSafe_WithNoPasswordOption()
    {
        var cut = Render<BackupRecoveryPanel>();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid='backup-note']")));
        Assert.Equal(
            "Backups hold your settings and your own progress, bookmarks and favourites. They do not include your media files or folders. Back those up with your computer's own backup tool.",
            cut.Find("[data-testid='backup-note']").TextContent);
        Assert.Equal(
            "Anyone with this file can see your settings and sign in as you. Keep it somewhere safe.",
            cut.Find("[data-testid='backup-keep-safe']").TextContent);
        Assert.DoesNotContain("password", cut.Markup, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("passphrase", cut.Markup, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheNoteSitsUnderTheCreateBackupButton()
    {
        var cut = Render<BackupRecoveryPanel>();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid='backup-note']")));
        var card = cut.Find("[data-testid='backup-note']").ParentElement!;
        var button = card.QuerySelectorAll("button").First(item => item.TextContent.Contains("Create backup", StringComparison.Ordinal));
        Assert.True(Position(card.InnerHtml, "Create backup") < Position(card.InnerHtml, "Backups hold your settings"));
        Assert.NotNull(button);
    }

    [Fact]
    public void TheExactFileWarning_IsHiddenWhenNothingIsMatchedByExactFileOnly()
    {
        _state = new BackupStateDto(0, 90, 0, null);

        var cut = Render<BackupRecoveryPanel>();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid='backup-note']")));
        Assert.Empty(cut.FindAll("[data-testid='file-only-warning']"));
    }

    [Fact]
    public void TheExactFileWarning_SaysOneItemInTheSingular()
    {
        _state = new BackupStateDto(1, 90, 0, null);

        var cut = Render<BackupRecoveryPanel>();

        cut.WaitForAssertion(() => Assert.Equal(
            "1 item is matched by exact file only and may lose progress if the file changes.",
            cut.Find("[data-testid='file-only-warning']").TextContent.Trim()));
    }

    [Fact]
    public void TheExactFileWarning_CountsSeveralItemsInThePlural()
    {
        _state = new BackupStateDto(38, 90, 0, null);

        var cut = Render<BackupRecoveryPanel>();

        cut.WaitForAssertion(() => Assert.Equal(
            "38 items are matched by exact file only and may lose progress if the file changes.",
            cut.Find("[data-testid='file-only-warning']").TextContent.Trim()));
    }

    [Fact]
    public void TheExactFileWarning_UsesTheAppsStandardWarningStyle()
    {
        _state = new BackupStateDto(3, 90, 0, null);

        var cut = Render<BackupRecoveryPanel>();

        cut.WaitForAssertion(() => Assert.Contains("tl-alert-warning", cut.Find("[data-testid='file-only-warning']").ClassName, StringComparison.Ordinal));
    }

    [Fact]
    public void AfterARestoreWithItemsStillWaiting_OneSummaryLineSaysWhatCameBackAndWhatIsWaiting()
    {
        _state = new BackupStateDto(0, 90, 4, Restore(attached: 12, DateTimeOffset.UtcNow.AddDays(-30)));

        var cut = Render<BackupRecoveryPanel>();

        cut.WaitForAssertion(() => Assert.Equal(
            "12 items got their progress and bookmarks back. 4 are waiting for their files; they'll return if the files do, and are cleared after 90 days.",
            cut.Find("[data-testid='restore-summary']").TextContent));
        Assert.Single(cut.FindAll("[data-testid='restore-summary']"));
    }

    [Fact]
    public void AfterARestoreWithNothingWaiting_TheSummaryLineSaysOnlyWhatCameBack_ForAWeek()
    {
        _state = new BackupStateDto(0, 90, 0, Restore(attached: 12, DateTimeOffset.UtcNow.AddDays(-2)));

        var cut = Render<BackupRecoveryPanel>();

        cut.WaitForAssertion(() => Assert.Equal(
            "12 items got their progress and bookmarks back.",
            cut.Find("[data-testid='restore-summary']").TextContent));
    }

    [Fact]
    public void ARestoreFromMoreThanAWeekAgoWithNothingWaiting_ShowsNoSummaryLine()
    {
        _state = new BackupStateDto(0, 90, 0, Restore(attached: 12, DateTimeOffset.UtcNow.AddDays(-9)));

        var cut = Render<BackupRecoveryPanel>();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid='backup-note']")));
        Assert.Empty(cut.FindAll("[data-testid='restore-summary']"));
    }

    [Fact]
    public void WhenNothingCameBackYetAndSeveralItemsWait_TheSummaryLineSaysOnlyWhatWaits()
    {
        _state = new BackupStateDto(0, 90, 3, Restore(attached: 0, DateTimeOffset.UtcNow.AddDays(-1)));

        var cut = Render<BackupRecoveryPanel>();

        cut.WaitForAssertion(() => Assert.Equal(
            "3 items are waiting for their files; they'll return if the files do, and are cleared after 90 days.",
            cut.Find("[data-testid='restore-summary']").TextContent));
    }

    [Fact]
    public void WhenNothingCameBackYetAndOneItemWaits_TheSummaryLineSaysOnlyThatOneWaits()
    {
        _state = new BackupStateDto(0, 60, 1, Restore(attached: 0, DateTimeOffset.UtcNow.AddDays(-30)));

        var cut = Render<BackupRecoveryPanel>();

        cut.WaitForAssertion(() => Assert.Equal(
            "1 item is waiting for its file; it'll return if the file does, and is cleared after 60 days.",
            cut.Find("[data-testid='restore-summary']").TextContent));
    }

    [Fact]
    public void WhenNothingCameBackAndNothingWaits_TheSummaryLineIsHidden_EvenRightAfterTheRestore()
    {
        _state = new BackupStateDto(0, 90, 0, Restore(attached: 0, DateTimeOffset.UtcNow.AddHours(-1)));

        var cut = Render<BackupRecoveryPanel>();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid='backup-note']")));
        Assert.Empty(cut.FindAll("[data-testid='restore-summary']"));
    }

    [Fact]
    public void WithNoRestoreEver_NoSummaryLineIsShown_AndNoItemIsListed()
    {
        _state = new BackupStateDto(0, 90, 0, null);

        var cut = Render<BackupRecoveryPanel>();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid='backup-note']")));
        Assert.Empty(cut.FindAll("[data-testid='restore-summary']"));
    }

    [Fact]
    public void WhenTheEngineCannotBeAskedForTheState_NothingThatDependsOnItIsShown()
    {
        _state = null;

        var cut = Render<BackupRecoveryPanel>();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid='backup-note']")));
        Assert.Empty(cut.FindAll("[data-testid='file-only-warning']"));
        Assert.Empty(cut.FindAll("[data-testid='restore-summary']"));
        Assert.Empty(cut.FindAll("[data-testid='waiting-period']"));
    }

    [Fact]
    public void TheWaitingPeriod_OffersTheFiveChoices_WithTheStoredOneSelected()
    {
        _state = new BackupStateDto(0, 90, 0, null);

        var cut = Render<BackupRecoveryPanel>();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid='waiting-period']")));
        var select = cut.Find("[data-testid='waiting-period']");
        Assert.Equal(["30 days", "60 days", "90 days", "180 days", "365 days"], select.QuerySelectorAll("option").Select(option => option.TextContent));
        Assert.Contains("Keep restored progress waiting for", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void ChoosingAWaitingPeriod_SavesItStraightAway()
    {
        var cut = Render<BackupRecoveryPanel>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid='waiting-period']")));

        cut.Find("[data-testid='waiting-period']").Change("180");

        cut.WaitForAssertion(() => Assert.Equal(180, _savedDays));
        cut.WaitForAssertion(() => Assert.Equal("180", cut.Find("[data-testid='waiting-period']").GetAttribute("value")));
    }

    [Fact]
    public void AWaitingPeriodThatCouldNotBeSaved_LeavesTheStoredChoiceInPlace()
    {
        _saveFails = true;
        var cut = Render<BackupRecoveryPanel>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid='waiting-period']")));

        var before = cut.FindComponent<AppSelect>().Instance;
        cut.Find("[data-testid='waiting-period']").Change("30");

        cut.WaitForAssertion(() => Assert.Equal(30, _savedDays));
        // The field is built again, so the browser cannot keep showing the choice that was not saved.
        cut.WaitForAssertion(() => Assert.NotSame(before, cut.FindComponent<AppSelect>().Instance));
        Assert.Equal("90", cut.Find("[data-testid='waiting-period']").GetAttribute("value"));
    }

    [Fact]
    public void AStoredWaitingPeriodThatIsNotOneOfTheChoices_IsShownAsOneMoreChoice()
    {
        _state = new BackupStateDto(0, 45, 0, null);

        var cut = Render<BackupRecoveryPanel>();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid='waiting-period']")));
        var options = cut.Find("[data-testid='waiting-period']").QuerySelectorAll("option").Select(option => option.TextContent).ToList();
        Assert.Equal(["30 days", "45 days", "60 days", "90 days", "180 days", "365 days"], options);
    }

    [Fact]
    public async Task TheWaitingPeriodIsNotOffered_OnAPhone()
    {
        var api = EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetBackupsAsync), _ => Task.FromResult<IReadOnlyList<BackupArchiveDto>>([Archive]));
            stub.SetHandler(nameof(IEngineApiClient.GetBackupStateAsync), _ => Task.FromResult<BackupStateDto?>(new BackupStateDto(5, 90, 0, null)));
        });
        var phone = new DeviceContextService(api);
        await phone.InitialiseAsync(new DeviceContextSnapshot("mobile", "phone", true));
        Services.AddSingleton(api);
        Services.AddSingleton(phone);

        var cut = Render<BackupRecoveryPanel>();

        cut.WaitForAssertion(() => Assert.Contains("Backup status", cut.Markup, StringComparison.Ordinal));
        Assert.Empty(cut.FindAll("[data-testid='waiting-period']"));
        Assert.Empty(cut.FindAll("[data-testid='backup-note']"));
    }

    [Fact]
    public void TestRestore_ShowsAShortFirstLookFromTheCounts_AndNothingElse()
    {
        _validation = new RestoreValidationResultDto(true, Archive.FileName, DateTimeOffset.UtcNow, "Restore test passed.",
            new BackupContentsDto(2, 31, 120, 80, 5));
        var cut = Render<BackupRecoveryPanel>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("button")));

        cut.FindAll("button").First(button => button.TextContent.Contains("Test restore", StringComparison.Ordinal)).Click();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid='restore-first-look']")));
        Assert.Equal("2 profiles and your settings", cut.Find("[data-testid='backup-contents-profiles']").TextContent);
        Assert.Equal(
            "120 items of progress, bookmarks and favourites, which come back as their titles are found again",
            cut.Find("[data-testid='backup-contents-items']").TextContent);
        Assert.Equal("Media files and folders are not included.", cut.Find("[data-testid='backup-contents-media']").TextContent);
        Assert.DoesNotContain("Vault", cut.Find("[data-testid='restore-first-look']").InnerHtml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("adult", cut.Find("[data-testid='restore-first-look']").InnerHtml, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TestRestoreOfARefusedFile_ShowsTheEnginesOneSentenceAsItIs()
    {
        const string sentence = "This backup is from an older version of the Library and can't be restored. Make a new backup with this version.";
        _validation = new RestoreValidationResultDto(false, Archive.FileName, DateTimeOffset.UtcNow, sentence);
        var cut = Render<BackupRecoveryPanel>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("button")));

        cut.FindAll("button").First(button => button.TextContent.Contains("Test restore", StringComparison.Ordinal)).Click();

        cut.WaitForAssertion(() => Assert.Equal(sentence, cut.Find("[data-testid='restore-error']").TextContent));
        Assert.Empty(cut.FindAll("[data-testid='restore-first-look']"));
    }

    [Fact]
    public void CreatingABackup_ShowsItsFileNameAndSizeAfterwards()
    {
        _backupExists = false;
        _created = Archive;
        var cut = Render<BackupRecoveryPanel>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid='backup-note']")));
        Assert.Empty(cut.FindAll("[data-testid='backup-created']"));

        cut.FindAll("button").First(button => button.TextContent.Contains("Create backup", StringComparison.Ordinal)).Click();

        cut.WaitForAssertion(() => Assert.Equal(
            "Backup created: tuvima-backup-20261022-080000.zip, 3.0 MB.",
            cut.Find("[data-testid='backup-created']").TextContent));
    }

    [Fact]
    public void TheFirstLookAtASetupUpload_SummarisesTheCounts_ThenSaysTheAppRestarts()
    {
        var cut = Render<SetupServerStage>(parameters => parameters
            .Add(component => component.ServerName, "Home")
            .Add(component => component.ShowRestore, true)
            .Add(component => component.Backup, new SetupBackupInspectionDto(
                Guid.NewGuid(), "backup.zip", null, "3.0", "epoch", 10, 20, 3, 2, "Holds your settings.",
                new BackupContentsDto(1, 31, 1, 1, 0))));

        Assert.Equal("1 profile and your settings", cut.Find("[data-testid='backup-contents-profiles']").TextContent);
        Assert.Equal(
            "1 item of progress, bookmarks and favourites, which comes back as its title is found again",
            cut.Find("[data-testid='backup-contents-items']").TextContent);
        Assert.Equal("Media files and folders are not included.", cut.Find("[data-testid='backup-contents-media']").TextContent);
        Assert.Contains("The application will restart after confirmation.", cut.Find("[data-testid='setup-restore-first-look']").TextContent, StringComparison.Ordinal);
        Assert.DoesNotContain("Holds your settings.", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void ASetupUploadWithoutCounts_FallsBackToTheEnginesWarningSentence()
    {
        var cut = Render<SetupServerStage>(parameters => parameters
            .Add(component => component.ServerName, "Home")
            .Add(component => component.ShowRestore, true)
            .Add(component => component.Backup, new SetupBackupInspectionDto(
                Guid.NewGuid(), "backup.zip", null, "3.0", "epoch", 10, 20, 3, 2, "Holds your settings.")));

        Assert.Contains("Holds your settings.", cut.Find("[data-testid='setup-restore-first-look']").TextContent, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("[data-testid='backup-contents']"));
    }

    [Fact]
    public void ARefusedSetupUpload_ShowsTheEnginesOneSentenceAsItIs()
    {
        const string sentence = "This backup file is damaged and can't be restored.";

        var cut = Render<SetupServerStage>(parameters => parameters
            .Add(component => component.ServerName, "Home")
            .Add(component => component.ShowRestore, true)
            .Add(component => component.BackupError, sentence));

        Assert.Equal(sentence, cut.Find("[data-testid='setup-restore-error']").TextContent.Trim());
    }

    private static LatestRestoreDto Restore(int attached, DateTimeOffset restoredAt) =>
        new(restoredAt, Archive.FileName, attached + 3, attached, 0, 0);

    private static int Position(string html, string text) => html.IndexOf(text, StringComparison.Ordinal);
}
