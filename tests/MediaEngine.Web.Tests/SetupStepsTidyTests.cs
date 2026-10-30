using Bunit;
using MediaEngine.Contracts.Settings;
using MediaEngine.Contracts.Setup;
using MediaEngine.Web.Components.Setup;
using MediaEngine.Web.Tests.Support;

namespace MediaEngine.Web.Tests;

public sealed class SetupStepsTidyTests : AsyncBunitContext
{
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

        var cut = Render<SetupPreflightStage>(parameters => parameters.Add(p => p.Preflight, failed));

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

        var cut = Render<SetupPreflightStage>(parameters => parameters
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
    public void SilentCheck_ShowsGettingThingsReady()
    {
        var cut = Render<SetupPreflightStage>(parameters => parameters.Add(p => p.Busy, true));

        Assert.Contains("Getting things ready", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Retry", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void FirstScreen_OffersRestoreAsATextLink()
    {
        var cut = Render<SetupWelcomeStage>(parameters => parameters.Add(p => p.ServerName, "Home"));

        Assert.Contains("Restore a backup instead", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void EnhanceStep_ShowsSkipForNow_UntilAKeyIsSaved()
    {
        var none = Render<SetupProvidersStage>(parameters => parameters.Add(p => p.Catalogue, [Provider(configured: false)]));
        Assert.Contains("Skip for now", none.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Set up later", none.Markup, StringComparison.Ordinal);

        var saved = Render<SetupProvidersStage>(parameters => parameters.Add(p => p.Catalogue, [Provider(configured: true)]));
        Assert.DoesNotContain("Skip for now", saved.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Set up later", saved.Markup, StringComparison.Ordinal);
        Assert.Contains("Continue", saved.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void EnhanceStep_DoesNotListWhatTheLibraryNeeds()
    {
        var source = new ProviderCatalogueDto { DisplayName = "Example", MediaTypes = ["Movies"], Capabilities = ["artwork"] };

        var cut = Render<SetupProvidersStage>(parameters => parameters.Add(p => p.Catalogue, [source]));

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

        var cut = Render<SetupProvidersStage>(parameters => parameters.Add(p => p.Catalogue, [free, withKey, withKeyAlso]));

        var noKey = cut.FindAll(".app-status-badge").Single(x => x.TextContent.Contains("No key needed", StringComparison.Ordinal));
        Assert.Contains("app-tone--success", noKey.ClassList);
        Assert.NotNull(noKey.QuerySelector("svg, .tl-icon-root"));

        var configured = cut.FindAll(".app-status-badge").Single(x => x.TextContent.Contains("Key configured", StringComparison.Ordinal));
        Assert.Contains("app-tone--success", configured.ClassList);
        Assert.NotNull(configured.QuerySelector("svg, .tl-icon-root"));

        // A provider whose key is still missing shows no done-badge.
        Assert.Single(cut.FindAll(".app-status-badge"), x => x.TextContent.Contains("Key configured", StringComparison.Ordinal));
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
