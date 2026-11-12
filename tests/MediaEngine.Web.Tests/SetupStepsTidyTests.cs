using Bunit;
using MediaEngine.Contracts.Settings;
using MediaEngine.Contracts.Setup;
using MediaEngine.Web.Components.Setup;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Tests.Support;
using Microsoft.AspNetCore.Components;

namespace MediaEngine.Web.Tests;

public sealed class SetupStepsTidyTests : AsyncBunitContext
{
    public SetupStepsTidyTests() => JSInterop.Mode = JSRuntimeMode.Loose;

    private static readonly string[] SixSteps =
        ["Your Library", "Your profile", "Tools", "Libraries", "Metadata", "All set"];

    private static readonly string[] SevenSteps =
        ["Your Library", "Your profile", "Tools", "Vault", "Libraries", "Metadata", "All set"];

    private static readonly ToolRequirementsDto NoToolsNeeded = new(
        ToolInstallKinds.Source, DateTimeOffset.UnixEpoch, false, []);

    private static IReadOnlyList<SetupStepStatusDto> Statuses(string deferredKey = "") => SetupWorkflow.StepKeys
        .Select(key => new SetupStepStatusDto(key, key == deferredKey ? "deferred" : "passed", null, null, null))
        .ToList();

    [Fact]
    public void Stepper_ShowsTheEnginesStepsInOrder_AndNoVaultEntryUnlessOffered()
    {
        var cut = Render<SetupStepper>(parameters => parameters
            .Add(p => p.Steps, Statuses())
            .Add(p => p.CurrentStep, "profile")
            .Add(p => p.WorkflowStep, "profile"));

        var labels = cut.FindAll("li small").Select(item => item.TextContent.Trim()).ToList();
        Assert.Equal(SixSteps, labels);
        Assert.DoesNotContain("Vault", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void Stepper_ShowsAllSevenSteps_WhenTheVaultIsOffered_AndDropsItAfterNotNow()
    {
        var offered = Render<SetupStepper>(parameters => parameters
            .Add(p => p.Steps, Statuses())
            .Add(p => p.ShowVault, true)
            .Add(p => p.CurrentStep, "profile")
            .Add(p => p.WorkflowStep, "profile"));
        Assert.Equal(SevenSteps, offered.FindAll("li small").Select(item => item.TextContent.Trim()).ToList());

        var declined = Render<SetupStepper>(parameters => parameters
            .Add(p => p.Steps, Statuses("vault"))
            .Add(p => p.ShowVault, true)
            .Add(p => p.CurrentStep, "done")
            .Add(p => p.WorkflowStep, "done"));
        Assert.Equal(SixSteps, declined.FindAll("li small").Select(item => item.TextContent.Trim()).ToList());
        Assert.DoesNotContain("Vault", declined.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void Stepper_MarksTheWorkflowStepCurrent_WithNoMappingToAnotherEntry()
    {
        var cut = Render<SetupStepper>(parameters => parameters
            .Add(p => p.Steps, Statuses())
            .Add(p => p.CurrentStep, "tools")
            .Add(p => p.WorkflowStep, "tools"));

        var current = cut.FindAll("li.is-current");
        Assert.Single(current);
        Assert.Contains("Tools", current[0].TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void Stepper_ShowsLater_OnlyWhenTheMetadataStepWasSkipped()
    {
        var skipped = Render<SetupStepper>(parameters => parameters
            .Add(p => p.Steps, Statuses("metadata"))
            .Add(p => p.CurrentStep, "done")
            .Add(p => p.WorkflowStep, "done"));
        Assert.Equal("Later", Assert.Single(skipped.FindAll("em")).TextContent.Trim());

        var passed = Render<SetupStepper>(parameters => parameters
            .Add(p => p.Steps, Statuses())
            .Add(p => p.CurrentStep, "done")
            .Add(p => p.WorkflowStep, "done"));
        Assert.Empty(passed.FindAll("em"));
    }

    [Fact]
    public void Stepper_ShowsOptionalOnTheVaultStep_AndNowhereElse()
    {
        var cut = Render<SetupStepper>(parameters => parameters
            .Add(p => p.Steps, Statuses())
            .Add(p => p.ShowVault, true)
            .Add(p => p.CurrentStep, "vault")
            .Add(p => p.WorkflowStep, "vault"));

        var optional = Assert.Single(cut.FindAll("em"));
        Assert.Equal("optional", optional.TextContent.Trim());
        Assert.Contains("Vault", optional.ParentElement!.TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void FailedCheck_ShowsOnePlainMessageAndRetry_WithNoStorageList()
    {
        var failed = new SetupPreflightDto(false, false, string.Empty, "win-x64", "X64", "X64",
        [
            new SetupPathCheckDto("storage-media", "Tuvima library folder", "/data/media", "config", "failed", false, false, null,
                "Tuvima cannot write to the folder it keeps your library in."),
        ],
            NoToolsNeeded);

        var cut = Render<SetupToolsStage>(parameters => parameters.Add(p => p.Preflight, failed));

        Assert.Contains("We could not check this server. Check that the data folder is writable and has free space, then try again.", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Tuvima cannot write to the folder it keeps your library in.", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Retry", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Tuvima library folder", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Ignore this location", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Save and check again", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Server storage locations", cut.Markup, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("input"));
    }

    [Fact]
    public void SeveralFailingChecksRenderOneMessage_AndRawExceptionTextIsNeverShown()
    {
        var failed = new SetupPreflightDto(false, false, string.Empty, "win-x64", "X64", "X64",
        [
            new SetupPathCheckDto("database", "Database", "/data/library.db", "config", "blocked", false, false, null,
                "SQLite quick_check did not pass."),
            new SetupPathCheckDto("artwork", "Artwork", "/data/.data", "config", "blocked", false, false, null,
                "IOException: The process cannot access the file '/data/x' because it is being used by another process."),
        ],
            NoToolsNeeded);

        var cut = Render<SetupToolsStage>(parameters => parameters
            .Add(p => p.Preflight, failed)
            .Add(p => p.Error, "System.IO.IOException: disk I/O error"));

        const string message = "We could not check this server. Check that the data folder is writable and has free space, then try again.";
        Assert.Equal(1, cut.FindAll("p").Count(paragraph => paragraph.TextContent.Trim() == message));
        Assert.DoesNotContain("quick_check", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("IOException", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("disk I/O", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Retry", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void SilentCheck_ShowsCheckingYourTools()
    {
        var cut = Render<SetupToolsStage>(parameters => parameters.Add(p => p.Busy, true));

        Assert.Contains("Checking your tools", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Retry", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void FirstScreen_HasContinueOnly_BecauseRestoreLivesOnTheIntro()
    {
        var cut = Render<SetupWelcomeStage>(parameters => parameters.Add(p => p.ServerName, "Home"));

        var footer = cut.Find("footer.setup-stage__footer");
        Assert.Empty(cut.FindAll("[data-testid='setup-restore-open']"));
        Assert.DoesNotContain("Restore a backup", footer.TextContent, StringComparison.Ordinal);
        var buttons = footer.QuerySelectorAll(".app-button");
        Assert.Single(buttons);
        Assert.Contains("Continue", buttons[0].TextContent, StringComparison.Ordinal);
        Assert.Contains("app-button--filled", buttons[0].ClassName, StringComparison.Ordinal);
    }

    [Fact]
    public void FirstScreen_IsHeadedNameYourLibrary()
    {
        var cut = Render<SetupWelcomeStage>(parameters => parameters.Add(p => p.ServerName, "Home"));

        Assert.Equal("Name your Library", cut.Find("#setup-stage-title").TextContent.Trim());
    }

    [Fact]
    public void Welcome_AsksOneNameAndShowsTheDerivedNetworkNameWithAChangeLink()
    {
        var cut = Render<SetupWelcomeStage>(parameters => parameters
            .Add(p => p.ServerName, "Shaya's Library")
            .Add(p => p.HostName, "shayas-library"));

        Assert.Contains("What should we call this Library?", cut.Markup, StringComparison.Ordinal);
        Assert.Single(cut.FindAll("input"));
        var line = cut.Find("[data-testid='setup-network-name-line']").TextContent;
        Assert.Contains("Phones and TVs on your home network will find it at shayas-library.local", line, StringComparison.Ordinal);
        Assert.Contains("Change", line, StringComparison.Ordinal);
    }

    [Fact]
    public void Welcome_ChangeRevealsTheNetworkNameFieldAndAHandSetNameStopsFollowingTheFriendlyName()
    {
        var host = "shayas-library";
        var derived = true;
        var cut = Render<SetupWelcomeStage>(parameters => parameters
            .Add(p => p.ServerName, "Shaya's Library")
            .Add(p => p.HostName, host)
            .Add(p => p.HostNameDerived, derived)
            .Add(p => p.HostNameChanged, EventCallback.Factory.Create<string>(this, value => host = value))
            .Add(p => p.HostNameDerivedChanged, EventCallback.Factory.Create<bool>(this, value => derived = value)));

        cut.Find("[data-testid='setup-network-name-change']").Click();

        Assert.Empty(cut.FindAll("[data-testid='setup-network-name-change']"));
        Assert.Equal(2, cut.FindAll("input").Count);
        cut.Find("input[placeholder='tuvima']").Input("Den TV");
        Assert.Equal("den-tv", host);
        Assert.False(derived);
    }

    [Fact]
    public void Welcome_WhileDerivedTheNetworkNameFollowsTheFriendlyName_AndAHandSetOneIsLeftAlone()
    {
        var host = string.Empty;
        var hostChanged = EventCallback.Factory.Create<string>(this, value => host = value);

        var following = Render<SetupWelcomeStage>(parameters => parameters
            .Add(p => p.HostNameDerived, true)
            .Add(p => p.HostNameChanged, hostChanged));
        following.Find("input").Input("Shaya's Library");
        Assert.Equal("shayas-library", host);

        host = "den";
        var handSet = Render<SetupWelcomeStage>(parameters => parameters
            .Add(p => p.HostName, "den")
            .Add(p => p.HostNameDerived, false)
            .Add(p => p.HostNameChanged, hostChanged));
        handSet.Find("input").Input("Another Name");
        Assert.Equal("den", host);
        Assert.Contains("den.local", handSet.Find("[data-testid='setup-network-name-line']").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void AppButton_DrawsThreeLevels_FilledOutlinedText()
    {
        foreach (var (style, cls) in new[] { (AppButtonStyle.Filled, "app-button--filled"), (AppButtonStyle.Outlined, "app-button--outlined"), (AppButtonStyle.Text, "app-button--text") })
        {
            var cut = Render<AppButton>(parameters => parameters.Add(p => p.Label, "x").Add(p => p.ButtonStyle, style));
            Assert.Contains(cls, cut.Find("button").ClassName, StringComparison.Ordinal);
        }
        Assert.DoesNotContain("Ghost", Enum.GetNames<AppButtonStyle>());
    }

    [Fact]
    public void MetadataStep_ShowsSkipForNowAndContinueTogether_ContinueWaitingForASavedKey()
    {
        var none = Render<SetupMetadataStage>(parameters => parameters.Add(p => p.Catalogue, [Provider(configured: false)]));
        var noneButtons = none.FindAll("footer .app-button");
        Assert.Equal(["Skip for now", "Continue"], noneButtons.Select(button => button.TextContent.Trim()).ToArray());
        Assert.Contains("app-button--text", noneButtons[0].ClassName, StringComparison.Ordinal);
        Assert.False(noneButtons[0].HasAttribute("disabled"));
        Assert.Contains("app-button--filled", noneButtons[1].ClassName, StringComparison.Ordinal);
        Assert.True(noneButtons[1].HasAttribute("disabled"));

        var saved = Render<SetupMetadataStage>(parameters => parameters.Add(p => p.Catalogue, [Provider(configured: true)]));
        var savedButtons = saved.FindAll("footer .app-button");
        Assert.Equal(["Skip for now", "Continue"], savedButtons.Select(button => button.TextContent.Trim()).ToArray());
        Assert.False(savedButtons[1].HasAttribute("disabled"));
    }

    [Fact]
    public void MetadataStep_SkipForNowAndContinueEachRaiseTheirOwnCallback()
    {
        var continued = 0;
        var deferred = 0;
        var cut = Render<SetupMetadataStage>(parameters => parameters
            .Add(p => p.Catalogue, [Provider(configured: true)])
            .Add(p => p.Continue, EventCallback.Factory.Create(this, () => continued++))
            .Add(p => p.Defer, EventCallback.Factory.Create(this, () => deferred++)));

        cut.FindAll("footer .app-button").Single(button => button.TextContent.Trim() == "Skip for now").Click();
        cut.FindAll("footer .app-button").Single(button => button.TextContent.Trim() == "Continue").Click();

        Assert.Equal(1, deferred);
        Assert.Equal(1, continued);
    }

    [Fact]
    public void MetadataStep_IsHeadedWhereShouldLibraryLookThingsUp()
    {
        var cut = Render<SetupMetadataStage>(parameters => parameters.Add(p => p.Catalogue, [Provider(configured: false)]));

        Assert.Equal("Where should Library look things up?", cut.Find("#setup-stage-title").TextContent.Trim());
    }

    [Fact]
    public void EnhanceStep_DoesNotListWhatTheLibraryNeeds()
    {
        var source = new ProviderCatalogueDto { DisplayName = "Example", MediaTypes = ["Movies"], Capabilities = ["artwork"] };

        var cut = Render<SetupMetadataStage>(parameters => parameters.Add(p => p.Catalogue, [source]));

        Assert.DoesNotContain("What your library needs", cut.Markup, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("[data-testid='setup-need']"));
    }

    [Fact]
    public void EnhanceStep_ShowsAGreenCheckForProvidersThatNeedNoKeyAndForConfiguredKeys()
    {
        var free = new ProviderCatalogueDto { DisplayName = "Free source", RequiresKey = false };
        var withKey = Provider(configured: true);
        var withKeyAlso = Provider(configured: false);
        withKeyAlso.DisplayName = "Needs a key";

        var cut = Render<SetupMetadataStage>(parameters => parameters.Add(p => p.Catalogue, [free, withKey, withKeyAlso]));

        var noKey = cut.FindAll(".app-status-badge").Single(x => x.TextContent.Contains("No key needed", StringComparison.Ordinal));
        Assert.Contains("app-tone--success", noKey.ClassList);
        Assert.NotNull(noKey.QuerySelector("svg, .tl-icon-root"));

        var configured = cut.FindAll(".app-status-badge").Single(x => x.TextContent.Contains("Key saved", StringComparison.Ordinal));
        Assert.Contains("app-tone--success", configured.ClassList);
        Assert.NotNull(configured.QuerySelector("svg, .tl-icon-root"));

        // A provider whose key is still missing shows no done-badge.
        Assert.Single(cut.FindAll(".app-status-badge"), x => x.TextContent.Contains("Key saved", StringComparison.Ordinal));
    }

    [Fact]
    public void MetadataStep_ListsEverySource_WithNoSourcesFilterAndNoLayoutSwitch()
    {
        var movies = new ProviderCatalogueDto { DisplayName = "Movie source", RequiresKey = false, MediaTypes = ["Movies"] };
        var comics = new ProviderCatalogueDto { DisplayName = "Comic source", RequiresKey = false, MediaTypes = ["Comics"] };

        var cut = Render<SetupMetadataStage>(parameters => parameters.Add(p => p.Catalogue, [movies, comics]));

        Assert.Contains("Movie source", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Comic source", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Show all sources", cut.Markup, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("[data-testid='setup-sources-list']"));
        Assert.Empty(cut.FindAll(".app-layout-switch"));
        Assert.Empty(cut.FindAll(".setup-sources-bar"));
    }

    [Fact]
    public void MetadataStep_KeepsTheMetadataLanguageLine_BecauseNoOtherStepChoosesIt()
    {
        var locale = new SetupLocaleDto("en", "en", "US");
        var cut = Render<SetupMetadataStage>(parameters => parameters
            .Add(p => p.Catalogue, [Provider(configured: false)])
            .Add(p => p.Locale, locale));
        var welcome = Render<SetupWelcomeStage>(parameters => parameters
            .Add(p => p.ServerName, "Home")
            .Add(p => p.Locale, locale));

        Assert.Contains("Metadata language", cut.Find(".setup-locale-form").TextContent, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Metadata language", welcome.Find(".setup-locale-form").TextContent, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MetadataStep_ShowsTileSkeletonsWhileTheSourcesLoad()
    {
        var cut = Render<SetupMetadataStage>(parameters => parameters
            .Add(p => p.Catalogue, [])
            .Add(p => p.Loaded, false));

        var skeleton = cut.Find("[data-testid='setup-sources-skeleton']");
        Assert.Equal(6, skeleton.QuerySelectorAll(".app-skeleton").Length);
        Assert.Empty(cut.FindAll("[data-testid='setup-sources-error']"));
        Assert.Empty(cut.FindAll(".app-spinner"));
    }

    [Fact]
    public async Task MetadataStep_ShowsAnErrorWithTryAgain_WhenTheSourcesCouldNotBeLoaded_AndTryAgainAsksAgain()
    {
        var asked = 0;
        var cut = Render<SetupMetadataStage>(parameters => parameters
            .Add(p => p.Catalogue, [])
            .Add(p => p.Loaded, true)
            .Add(p => p.RefreshRequested, EventCallback.Factory.Create(this, () => asked++)));

        var error = cut.Find("[data-testid='setup-sources-error']");
        Assert.NotNull(error.QuerySelector(".app-error-state"));
        Assert.Empty(cut.FindAll("[data-testid='setup-sources-skeleton']"));
        var tryAgain = cut.Find("[data-testid='setup-sources-try-again']");
        Assert.Equal("Try again", tryAgain.TextContent.Trim());

        await cut.InvokeAsync(() => tryAgain.Click());

        Assert.Equal(1, asked);
    }

    [Fact]
    public void MetadataStep_OffersConnectOnlyWhereAKeyIsNeeded_WithNoSwitchOrMenu()
    {
        var free = new ProviderCatalogueDto { DisplayName = "Free source", RequiresKey = false };
        var needsKey = Provider(configured: false);
        needsKey.DisplayName = "Keyed source";

        var cut = Render<SetupMetadataStage>(parameters => parameters.Add(p => p.Catalogue, [free, needsKey]));

        Assert.Contains("Needs a key", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("No key needed", cut.Markup, StringComparison.Ordinal);
        Assert.Single(cut.FindAll("button"), button => button.TextContent.Trim() == "Connect");
        Assert.Empty(cut.FindAll("[role='switch']"));
        Assert.Contains("data-testid=\"setup-sources-cards\"", cut.Markup, StringComparison.Ordinal);
    }

    private static ProviderCatalogueDto Provider(bool configured) => new()
    {
        DisplayName = "Example",
        RequiresKey = true,
        Onboarding = new ProviderOnboardingDto
        {
            Credentials = [new ProviderCredentialFieldDto { Key = "api_key", Label = "Key", Required = true, Configured = configured }],
        },
    };

    [Fact]
    public void NetworkNameCancel_KeepsItsOwnWidth_BesideTheField()
    {
        // #854: the field and Cancel are child components, so the row's rules must reach them through ::deep.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MediaEngine.slnx")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        var css = File.ReadAllText(Path.Combine(dir.FullName, "src", "MediaEngine.Web", "Components", "Setup", "SetupWelcomeStage.razor.css"));
        Assert.Contains(".setup-server-form__host ::deep > :first-child { flex: 1 1 auto; min-width: 0; }", css, StringComparison.Ordinal);
        Assert.Contains(".setup-server-form__host ::deep > :not(:first-child) { flex: none; }", css, StringComparison.Ordinal);
    }
}
