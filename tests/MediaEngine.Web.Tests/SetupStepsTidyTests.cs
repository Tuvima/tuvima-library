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
        ["Welcome", "Your profile", "Tools", "Libraries", "Metadata", "All set"];

    private static readonly string[] SevenSteps =
        ["Welcome", "Your profile", "Tools", "Libraries", "Vault", "Metadata", "All set"];

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
        Assert.Single(skipped.FindAll("em"));

        var passed = Render<SetupStepper>(parameters => parameters
            .Add(p => p.Steps, Statuses())
            .Add(p => p.CurrentStep, "done")
            .Add(p => p.WorkflowStep, "done"));
        Assert.Empty(passed.FindAll("em"));
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
    public void FirstScreen_OffersRestoreAsASecondaryButtonBesideContinue()
    {
        var cut = Render<SetupWelcomeStage>(parameters => parameters.Add(p => p.ServerName, "Home"));

        var footer = cut.Find("footer.setup-stage__footer");
        var restore = footer.QuerySelector("[data-testid='setup-restore-open']");
        Assert.NotNull(restore);
        Assert.Contains("app-button--outlined", restore!.ClassName, StringComparison.Ordinal);
        Assert.Contains("Restore a backup", restore.TextContent, StringComparison.Ordinal);
        Assert.Contains("app-button--filled", footer.QuerySelector(".setup-stage__footer-actions .app-button")!.ClassName, StringComparison.Ordinal);
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
    public void EnhanceStep_ShowsSkipForNow_UntilAKeyIsSaved()
    {
        var none = Render<SetupMetadataStage>(parameters => parameters.Add(p => p.Catalogue, [Provider(configured: false)]));
        Assert.Contains("Skip for now", none.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Set up later", none.Markup, StringComparison.Ordinal);

        var saved = Render<SetupMetadataStage>(parameters => parameters.Add(p => p.Catalogue, [Provider(configured: true)]));
        Assert.DoesNotContain("Skip for now", saved.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Set up later", saved.Markup, StringComparison.Ordinal);
        Assert.Contains("Continue", saved.Markup, StringComparison.Ordinal);
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
    public void MetadataStep_ListsOnlySourcesThatFitTheLibrariesAdded_AndShowAllBringsTheRestBack()
    {
        var movies = new ProviderCatalogueDto { DisplayName = "Movie source", RequiresKey = false, MediaTypes = ["Movies"] };
        var comics = new ProviderCatalogueDto { DisplayName = "Comic source", RequiresKey = false, MediaTypes = ["Comics"] };

        var cut = Render<SetupMetadataStage>(parameters => parameters
            .Add(p => p.Catalogue, [movies, comics])
            .Add(p => p.LibraryMediaTypes, ["movies"]));

        Assert.Contains("Movie source", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Comic source", cut.Markup, StringComparison.Ordinal);

        cut.FindAll("button").Single(button => button.TextContent.Contains("Show all sources", StringComparison.Ordinal)).Click();

        Assert.Contains("Comic source", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void MetadataStep_ListsEverySourceWhenNoLibraryWasAdded()
    {
        var movies = new ProviderCatalogueDto { DisplayName = "Movie source", RequiresKey = false, MediaTypes = ["Movies"] };
        var comics = new ProviderCatalogueDto { DisplayName = "Comic source", RequiresKey = false, MediaTypes = ["Comics"] };

        var cut = Render<SetupMetadataStage>(parameters => parameters.Add(p => p.Catalogue, [movies, comics]));

        Assert.Contains("Movie source", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Comic source", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Show all sources", cut.Markup, StringComparison.Ordinal);
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
}
