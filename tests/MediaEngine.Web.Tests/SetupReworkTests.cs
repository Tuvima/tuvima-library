using Bunit;
using MediaEngine.Contracts.Settings;
using MediaEngine.Contracts.Setup;
using MediaEngine.Web.Components.Setup;
using MediaEngine.Web.Components.Shared.Tools;
using MediaEngine.Web.Tests.Support;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

// First-run setup rework: the intro, the step order, the coloured Tools chips and the All set warning.
public sealed class SetupReworkTests : AsyncBunitContext
{
    public SetupReworkTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLocalization();
        Services.AddNativeUiServices();
    }

    [Fact]
    public void TheIntroShowsTheTaglineThreePointsThePrivacyPanelAndBothButtons()
    {
        var started = 0;
        var restored = 0;
        var cut = Render<SetupIntroStage>(parameters => parameters
            .Add(p => p.GetStarted, EventCallback.Factory.Create(this, () => started++))
            .Add(p => p.Restore, EventCallback.Factory.Create(this, () => restored++)));

        Assert.Contains("ONE LIBRARY. EVERY STORY.", cut.Markup, StringComparison.Ordinal);
        Assert.Equal(["Find", "Connect", "Enjoy"], cut.FindAll(".setup-intro__cards strong").Select(item => item.TextContent.Trim()).ToArray());
        Assert.Contains("Private by default", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("account", cut.Find(".setup-intro__private").TextContent, StringComparison.OrdinalIgnoreCase);
        var link = cut.Find(".setup-intro__private a");
        Assert.Equal("_blank", link.GetAttribute("target"));
        Assert.StartsWith("https://docs.tuvima.com/", link.GetAttribute("href"), StringComparison.Ordinal);

        cut.Find("[data-testid=setup-get-started]").Click();
        cut.Find("[data-testid=setup-restore-open]").Click();
        Assert.Equal(1, started);
        Assert.Equal(1, restored);
    }

    [Fact]
    public void TheStepsRunVaultBeforeLibraries()
    {
        Assert.Equal(["welcome", "profile", "tools", "vault", "libraries", "metadata", "done"], SetupWorkflow.StepKeys);
    }

    [Fact]
    public void TheToolsSummaryDrawsReadyNeedsSetupAndDownloadingAsSeparateChips()
    {
        var tools = new[]
        {
            Tool("a", ToolRequirementStates.Ready), Tool("b", ToolRequirementStates.Ready),
            Tool("c", ToolRequirementStates.Missing), Tool("d", ToolRequirementStates.Downloading),
        };

        var cut = Render<ToolsSummaryLine>(parameters => parameters.Add(p => p.Tools, tools));

        Assert.Equal(["ready", "attention", "busy"], cut.FindAll(".tools-summary__part[data-tone]").Skip(1).Select(item => item.GetAttribute("data-tone")!).ToArray());
        Assert.Equal("4 tools detected · 2 ready · 1 needs setup · 1 downloading", cut.Find("[data-testid=tools-summary]").TextContent.Trim());
    }

    [Fact]
    public void AllSetWarnsAboutKindsWaitingForASourceOnlyWhenThereAreSome()
    {
        var readiness = new SetupReadinessDto(true, []);

        var none = Render<SetupReadinessStage>(parameters => parameters.Add(p => p.Readiness, readiness));
        Assert.Empty(none.FindAll("[data-testid=setup-no-source-warning]"));

        var held = Render<SetupReadinessStage>(parameters => parameters
            .Add(p => p.Readiness, readiness)
            .Add(p => p.HeldKinds, new[] { "films", "TV shows" }));
        Assert.Contains("films, TV shows", held.Find("[data-testid=setup-no-source-warning]").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void AllSetPutsTheNoSourceWarningAtTheTop_AboveTheRows()
    {
        var readiness = new SetupReadinessDto(true,
        [
            new SetupCapabilityDto("profile", "Administrator", "passed", true, "Ready.", null),
            new SetupCapabilityDto("libraries", "Libraries", "passed", true, "2 libraries.", null),
        ]);

        var cut = Render<SetupReadinessStage>(parameters => parameters
            .Add(p => p.Readiness, readiness)
            .Add(p => p.HeldKinds, new[] { "films" }));

        var warningAt = cut.Markup.IndexOf("data-testid=\"setup-no-source-warning\"", StringComparison.Ordinal);
        var rowsAt = cut.Markup.IndexOf("data-testid=\"setup-summary\"", StringComparison.Ordinal);
        Assert.True(warningAt >= 0 && rowsAt >= 0, "Both the warning and the rows are drawn.");
        Assert.True(warningAt < rowsAt, "The warning must come before the rows.");
    }

    [Fact]
    public void ToolsStageListsLocalAiAsALaterRow_NotAFootnote()
    {
        var tools = new ToolRequirementsDto(ToolInstallKinds.Source, DateTimeOffset.UnixEpoch, false, [Tool("ffmpeg", ToolRequirementStates.Ready)]);
        var preflight = new SetupPreflightDto(true, false, string.Empty, "win-x64", "X64", "X64", [], tools);

        var cut = Render<SetupToolsStage>(parameters => parameters.Add(p => p.Preflight, preflight));

        var later = cut.Find("[data-testid=setup-tools] [data-tool-id=local-ai]");
        Assert.Equal("later", later.GetAttribute("data-state"));
        Assert.Contains("Local AI", later.TextContent, StringComparison.Ordinal);
        Assert.Contains("Set up later in Settings, Local AI", later.TextContent, StringComparison.Ordinal);
        Assert.Contains("Later", later.QuerySelector(".app-status-badge")!.TextContent, StringComparison.Ordinal);
        Assert.DoesNotContain("is set up in Settings, Local AI, after setup", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void TheIntroAnimationStyleHasNoHandTypedColors()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MediaEngine.slnx")))
        {
            directory = directory.Parent;
        }

        var css = File.ReadAllText(Path.Combine(directory!.FullName, "src", "MediaEngine.Web", "Components", "Setup", "IntroAnimation.razor.css"));

        Assert.DoesNotContain("#f2f8fc", css, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("#ddd6fe", css, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("color: var(--tl-text-primary)", css, StringComparison.Ordinal);
    }

    private static ToolRequirementDto Tool(string id, string state) => new(id, id, "tool", ToolRequirementLevels.Required, state, "Effect.", null, string.Empty, null, 0, false, false, false, null);
}
