using Bunit;
using MediaEngine.Contracts.Settings;
using MediaEngine.Contracts.Setup;
using MediaEngine.Web.Components.Settings;
using MediaEngine.Web.Components.Setup;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Navigation;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Tests.Support;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Web.Tests;

/// <summary>
/// The tools list at first run ("A few things to install") and in Settings ("Tools and requirements"), both worded
/// by the same row, plus the Local AI page that now keeps only AI settings.
/// </summary>
public sealed class FirstRunToolsTests : AsyncBunitContext
{
    private const string FfmpegEffect = "Videos, music and audiobooks wait until FFmpeg is available.";
    private const string FfmpegFix = "Run winget install Gyan.FFmpeg, then press Check again.";

    private int _getCalls;
    private int _checkCalls;
    private readonly List<string> _downloads = [];
    private Func<int, ToolRequirementsDto?> _onGet = _ => null;
    private Func<ToolRequirementsDto?> _onCheck = () => null;

    public FirstRunToolsTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        Services.AddLocalization();
        var api = EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetToolRequirementsAsync), _ => Task.FromResult(_onGet(Interlocked.Increment(ref _getCalls))));
            stub.SetHandler(nameof(IEngineApiClient.CheckToolRequirementsAsync), _ =>
            {
                Interlocked.Increment(ref _checkCalls);
                return Task.FromResult(_onCheck());
            });
            stub.SetHandler(nameof(IEngineApiClient.StartAiModelDownloadAsync), args =>
            {
                _downloads.Add((string)args![0]!);
                return Task.FromResult(AiOperationResultDto.Success());
            });
        });
        Services.AddSingleton(api);
        Services.AddSingleton(serviceProvider => new UIOrchestratorService(
            api,
            new UniverseStateContainer(),
            new ActiveProfileSessionService(serviceProvider.GetRequiredService<Microsoft.JSInterop.IJSRuntime>(), api),
            new ConfigurationBuilder().Build(),
            NullLogger<UIOrchestratorService>.Instance));
        Services.AddSingleton(new DeviceContextService(api));
        Services.AddSingleton(new DashboardSessionAccessor());
        Services.AddSingleton<SettingsAttentionState>();
    }

    // ── Setup: "Getting things ready" ────────────────────────────────────────

    [Fact]
    public void SetupStep_WithARequiredToolMissing_ListsItWithItsFixAndOffersCheckAgainAndContinue()
    {
        var cut = Render<SetupToolsStage>(parameters => parameters
            .Add(p => p.Preflight, Passed(Tools(Tool("ffmpeg", "FFmpeg", canLocate: true, waiting: 12))))
            .Add(p => p.Retry, EventCallback.Empty)
            .Add(p => p.Continue, EventCallback.Empty));

        Assert.Contains("FFmpeg", cut.Markup, StringComparison.Ordinal);
        Assert.Contains(FfmpegEffect, cut.Markup, StringComparison.Ordinal);
        Assert.Contains(FfmpegFix, cut.Markup, StringComparison.Ordinal);
        Assert.Contains("12 files are waiting for FFmpeg.", cut.Markup, StringComparison.Ordinal);
        Assert.Contains(cut.FindAll("button"), button => button.TextContent.Contains("Check again", StringComparison.Ordinal));
        Assert.Contains(cut.FindAll("button"), button => button.TextContent.Contains("Continue", StringComparison.Ordinal));
        Assert.DoesNotContain("Checking your tools", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void SetupStep_WhileARequiredToolDownloads_ShowsItsProgressInsteadOfAskingForAnInstall()
    {
        var cut = Render<SetupToolsStage>(parameters => parameters
            .Add(p => p.Preflight, Passed(Tools(Tool("ffmpeg", "FFmpeg", state: ToolRequirementStates.Downloading, percent: 55)))));

        Assert.Contains("Downloading 55%", cut.Find("[data-tool-id=ffmpeg]").TextContent, StringComparison.Ordinal);
        Assert.Contains("Continue", string.Join(' ', cut.FindAll("button").Select(button => button.TextContent)), StringComparison.Ordinal);
    }

    [Fact]
    public void SetupStep_WhenTheDownloadFailed_SaysWhyAndHowToTryAgain()
    {
        var failed = Tool("ffmpeg", "FFmpeg") with { StateReason = "The download didn't finish. Check the connection and try again." };

        var cut = Render<SetupToolsStage>(parameters => parameters.Add(p => p.Preflight, Passed(Tools(failed))));

        Assert.Contains("The download didn't finish.", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("press Check again", cut.Markup, StringComparison.Ordinal);
    }

    // One UI (spec A3): the Tools step and Settings > Tools and requirements draw the same row, so a wording fix in one is a fix in both.
    [Fact]
    public void SetupToolsStage_DrawsTheSameRowSettingsDraws()
    {
        var setup = File.ReadAllText(RepoFile("src", "MediaEngine.Web", "Components", "Setup", "SetupToolsStage.razor"));
        var settings = File.ReadAllText(RepoFile("src", "MediaEngine.Web", "Components", "Settings", "ToolsRequirementsTab.razor"));

        Assert.Contains("<ToolRequirementRow", setup, StringComparison.Ordinal);
        Assert.Contains("<ToolRequirementRow", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("<article", setup, StringComparison.Ordinal);
    }

    [Fact]
    public void SetupToolRules_FollowOnlyADownloadOfARequiredTool()
    {
        Assert.True(SetupToolRules.IsDownloadingRequiredTool(Tools(Tool("ffmpeg", "FFmpeg", state: ToolRequirementStates.Downloading))));
        Assert.False(SetupToolRules.IsDownloadingRequiredTool(Tools(Tool("ffmpeg", "FFmpeg"))));
        Assert.False(SetupToolRules.IsDownloadingRequiredTool(Tools(
            Tool("tesseract", "Tesseract", requirement: ToolRequirementLevels.Optional, state: ToolRequirementStates.Downloading))));
        Assert.False(SetupToolRules.IsDownloadingRequiredTool(null));
    }

    [Fact]
    public void SetupStep_NeverOffersLocateIt_BecauseThatLivesInSettings()
    {
        var cut = Render<SetupToolsStage>(parameters => parameters
            .Add(p => p.Preflight, Passed(Tools(Tool("ffmpeg", "FFmpeg", canLocate: true)))));

        Assert.DoesNotContain("Locate it", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void SetupStep_CheckAgainAsksAgain_AndContinueMovesOn()
    {
        var retried = 0;
        var continued = 0;
        var cut = Render<SetupToolsStage>(parameters => parameters
            .Add(p => p.Preflight, Passed(Tools(Tool("ffmpeg", "FFmpeg"))))
            .Add(p => p.Retry, EventCallback.Factory.Create(this, () => retried++))
            .Add(p => p.Continue, EventCallback.Factory.Create(this, () => continued++)));

        cut.FindAll("button").Single(button => button.TextContent.Contains("Check again", StringComparison.Ordinal)).Click();
        cut.FindAll("button").Single(button => button.TextContent.Contains("Continue", StringComparison.Ordinal)).Click();

        Assert.Equal(1, retried);
        Assert.Equal(1, continued);
    }

    [Fact]
    public void SetupStep_WithEveryToolReady_IsStillShown_WithATickPerToolAndAContinue()
    {
        var cut = Render<SetupToolsStage>(parameters => parameters
            .Add(p => p.Preflight, Passed(Tools(
                Tool("ffmpeg", "FFmpeg", state: ToolRequirementStates.Ready),
                Tool("tesseract", "Tesseract", requirement: ToolRequirementLevels.Optional, state: ToolRequirementStates.Ready),
                Tool("exiftool", "ExifTool", requirement: ToolRequirementLevels.Optional, state: ToolRequirementStates.Ready)))));

        Assert.Equal(3, cut.FindAll("[data-testid=setup-tools] article").Count);
        Assert.All(cut.FindAll("[data-testid=setup-tools] article"), row => Assert.Equal(ToolRequirementStates.Ready, row.GetAttribute("data-state")));
        Assert.Empty(cut.FindAll("input[type=checkbox]"));
        Assert.Contains(cut.FindAll("button"), button => button.TextContent.Contains("Continue", StringComparison.Ordinal));
    }

    [Fact]
    public void SetupStep_ASingleOptionalToolMissing_HasADownloadNowTickThatIsOnByDefault_AndReportsTheChange()
    {
        (string ToolId, bool Download)? change = null;
        var cut = Render<SetupToolsStage>(parameters => parameters
            .Add(p => p.Preflight, Passed(Tools(Tool("tesseract", "Tesseract", requirement: ToolRequirementLevels.Optional))))
            .Add(p => p.DownloadChanged, EventCallback.Factory.Create<(string ToolId, bool Download)>(this, value => change = value)));

        var tick = cut.Find("input[type=checkbox]");
        Assert.True(tick.HasAttribute("checked"));
        Assert.Contains("Download now", cut.Markup, StringComparison.Ordinal);

        tick.Change(false);

        Assert.Equal(("tesseract", false), change);
    }

    [Fact]
    public void SetupStep_ATickTurnedOffStaysOff_AndAReadyToolHasNoTick()
    {
        var cut = Render<SetupToolsStage>(parameters => parameters
            .Add(p => p.Skipped, new HashSet<string> { "tesseract" })
            .Add(p => p.Preflight, Passed(Tools(
                Tool("tesseract", "Tesseract", requirement: ToolRequirementLevels.Optional),
                Tool("exiftool", "ExifTool", requirement: ToolRequirementLevels.Optional, state: ToolRequirementStates.Ready)))));

        Assert.Single(cut.FindAll("input[type=checkbox]"));
        Assert.False(cut.Find("input[type=checkbox]").HasAttribute("checked"));
    }

    [Fact]
    public void SetupStep_NeverListsLocalAi_AndPointsToItsSettingsPage()
    {
        var cut = Render<SetupToolsStage>(parameters => parameters
            .Add(p => p.Preflight, Passed(Tools(
                Tool("ffmpeg", "FFmpeg", state: ToolRequirementStates.Ready),
                Tool(ToolRequirementIds.AiRuntime, "Local AI engine", requirement: ToolRequirementLevels.Optional)))));

        Assert.Single(cut.FindAll("[data-testid=setup-tools] article"));
        Assert.Contains("Settings, Local AI", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void SetupStep_WhileChecking_ShowsCheckingYourTools_EvenIfTheLastResultListedATool()
    {
        var cut = Render<SetupToolsStage>(parameters => parameters
            .Add(p => p.Preflight, Passed(Tools(Tool("ffmpeg", "FFmpeg"))))
            .Add(p => p.Busy, true));

        Assert.Contains("Checking your tools", cut.Markup, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("[data-testid=setup-tools]"));
    }

    [Fact]
    public void SetupStep_WhenTheServerCheckFails_KeepsItsFailureScreenUnderTheLibrarysName()
    {
        var failed = new SetupPreflightDto(false, false, string.Empty, "win-x64", "X64", "X64", [], Tools(Tool("ffmpeg", "FFmpeg")));

        var cut = Render<SetupToolsStage>(parameters => parameters.Add(p => p.Preflight, failed));

        Assert.Contains("The Library needs a hand", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Tuvima needs a hand", cut.Markup, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("[data-testid=setup-tools]"));
    }

    [Fact]
    public void SetupPage_DoesNotAutoAdvance_WhileARequiredToolNeedsAttention()
    {
        var setup = File.ReadAllText(RepoFile("src", "MediaEngine.Web", "Components", "Pages", "SetupPage.razor"));

        // The Tools step is always shown and only Continue passes it; nothing advances it by itself.
        Assert.DoesNotContain("automaticallyAdvance", setup, StringComparison.Ordinal);
        Assert.Contains("Continue=\"ContinuePastToolsAsync\"", setup, StringComparison.Ordinal);
    }

    // ── Setup: the last step ─────────────────────────────────────────────────

    [Fact]
    public void AllSetStep_NeverRepeatsTheToolsList()
    {
        var cut = RenderReadiness(Tools(
            Tool("ffmpeg", "FFmpeg", state: ToolRequirementStates.Ready),
            Tool("tesseract", "Tesseract", requirement: ToolRequirementLevels.Optional),
            Tool("ai_runtime", "Local AI engine", requirement: ToolRequirementLevels.Optional)));

        Assert.Empty(cut.FindAll("[data-testid=setup-media-tools]"));
        Assert.Empty(cut.FindAll("[data-tool-id]"));
        Assert.DoesNotContain("Your media tools", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Local AI", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Download Tesseract", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void AllSetStep_SaysOnceThatADownloadIsStillRunning_AndNothingWhenNoneIs()
    {
        var running = RenderReadiness(Tools(Tool("exiftool", "ExifTool", requirement: ToolRequirementLevels.Optional, state: ToolRequirementStates.Downloading, percent: 40)));
        Assert.Single(running.FindAll("[data-testid=setup-download-note]"));

        var idle = RenderReadiness(Tools(Tool("ffmpeg", "FFmpeg", state: ToolRequirementStates.Ready)));
        Assert.Empty(idle.FindAll("[data-testid=setup-download-note]"));
    }

    [Fact]
    public void AllSetStep_ShowsATickForDoneStepsALaterLinkToSettingsAndNeedsAttentionForTheRest()
    {
        var cut = Render<SetupReadinessStage>(parameters => parameters.Add(p => p.Readiness, new SetupReadinessDto(false,
        [
            new SetupCapabilityDto("welcome", "Welcome", "passed", false, "Named.", null),
            new SetupCapabilityDto("tools", "Tools", "passed", true, "Reviewed.", null),
            new SetupCapabilityDto("libraries", "Libraries", "deferred", false, "Later.", null),
            new SetupCapabilityDto("metadata", "Metadata", "blocked", false, "Missing.", "/setup?step=metadata"),
        ])));

        Assert.Equal(["Welcome", "Tools", "Libraries", "Metadata"], cut.FindAll("[data-testid=setup-summary] strong").Select(item => item.TextContent.Trim()));
        Assert.Equal("Ready", cut.Find("[data-step=welcome] .app-status-badge").TextContent.Trim());
        Assert.NotNull(cut.Find("[data-step=welcome] .app-status-badge").QuerySelector("svg, .tl-icon-root"));
        Assert.Equal("Later", cut.Find("[data-step=libraries] .app-status-badge").TextContent.Trim());
        Assert.Equal("/settings", cut.Find("[data-step=libraries] a").GetAttribute("href"));
        Assert.Equal("Needs attention", cut.Find("[data-step=metadata] .app-status-badge").TextContent.Trim());
        Assert.Equal("/setup?step=metadata", cut.Find("[data-step=metadata] a").GetAttribute("href"));
    }

    [Fact]
    public void AllSetStep_AskAnUnsignedBrowserToSignInInsteadOfSpinning()
    {
        var cut = Render<SetupReadinessStage>(parameters => parameters.Add(p => p.Failed, true));

        Assert.Contains("Sign in to finish setup", cut.Find("[data-testid=setup-sign-in-card]").TextContent, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll(".setup-preparing"));
    }

    // ── Settings: Tools and requirements ─────────────────────────────────────

    [Fact]
    public void ToolsPage_ListsEveryToolAndModel_WithWaitingFilesAndTheSameWording()
    {
        _onGet = _ => Tools(
            Tool("ffmpeg", "FFmpeg", canLocate: true, waiting: 12),
            Tool("tesseract", "Tesseract", requirement: ToolRequirementLevels.Optional),
            Tool("image_engine", "Image engine", state: ToolRequirementStates.Ready),
            Tool("ai_model:text_fast", "Local AI text model", kind: "ai_model", requirement: ToolRequirementLevels.Optional, canDownloadAgain: true));

        var cut = Render<ToolsRequirementsTab>();

        var tools = cut.Find("[data-testid=tools-list]");
        Assert.Contains("FFmpeg", tools.TextContent, StringComparison.Ordinal);
        Assert.Contains("12 files are waiting for FFmpeg.", tools.TextContent, StringComparison.Ordinal);
        Assert.Contains(FfmpegFix, tools.TextContent, StringComparison.Ordinal);
        Assert.Equal("Missing", cut.Find("[data-tool-id=ffmpeg] .app-chip").TextContent.Trim());
        Assert.Equal("Optional", cut.Find("[data-tool-id=tesseract] .app-chip").TextContent.Trim());
        Assert.Equal("Ready", cut.Find("[data-tool-id=image_engine] .app-chip").TextContent.Trim());
        // The model files are listed once, on Local AI > Models & Hardware; Tools and requirements only links there.
        Assert.Empty(cut.FindAll("[data-testid=models-list]"));
        Assert.Equal("/settings/ai/models", cut.Find("[data-testid=models-link]").GetAttribute("href"));

        var models = Render<LocalAiModelFilesPanel>();
        Assert.Contains("Local AI text model", models.Find("[data-testid=models-list]").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void ToolsPage_OffersLocateIt_OnlyWhereTheEngineSaysTheToolCanBeLocated()
    {
        _onGet = _ => Tools(
            Tool("ffmpeg", "FFmpeg", canLocate: true),
            Tool("tesseract", "Tesseract", requirement: ToolRequirementLevels.Optional, canLocate: true),
            Tool("image_engine", "Image engine", state: ToolRequirementStates.Problem),
            Tool("adaptive_streaming", "Smooth streaming", requirement: ToolRequirementLevels.Optional));

        var cut = Render<ToolsRequirementsTab>();

        Assert.Single(cut.FindAll("[data-tool-id=ffmpeg] button"), button => button.TextContent.Contains("Locate it", StringComparison.Ordinal));
        Assert.Single(cut.FindAll("[data-tool-id=tesseract] button"), button => button.TextContent.Contains("Locate it", StringComparison.Ordinal));
        Assert.Empty(cut.FindAll("[data-tool-id=image_engine] button"));
        Assert.Empty(cut.FindAll("[data-tool-id=adaptive_streaming] button"));
    }

    [Fact]
    public void ToolsPage_OffersDownloadForAMissingModel_DownloadAgainForAFailedOne_AndStartsTheDownload()
    {
        _onGet = _ => Tools(
            Tool("ai_model:text_fast", "Local AI text model", kind: "ai_model", requirement: ToolRequirementLevels.Optional, canDownloadAgain: true),
            Tool("ai_model:text_quality", "Local AI quality model", kind: "ai_model", requirement: ToolRequirementLevels.Optional,
                state: ToolRequirementStates.Problem, canDownloadAgain: true));

        var cut = Render<LocalAiModelFilesPanel>();

        Assert.Contains(cut.FindAll("[data-tool-id='ai_model:text_fast'] button"), button => button.TextContent.Trim() == "Download");
        var again = cut.FindAll("[data-tool-id='ai_model:text_quality'] button").Single(button => button.TextContent.Trim() == "Download again");
        again.Click();

        cut.WaitForAssertion(() => Assert.Equal(["text_quality"], _downloads));
    }

    [Fact]
    public void ToolsPage_ShowsProgressAndCancelWhileAModelDownloads_AndLoadWhenItIsReady()
    {
        _onGet = _ => Tools(
            Tool("ai_model:text_fast", "Local AI text model", kind: "ai_model", requirement: ToolRequirementLevels.Optional,
                state: ToolRequirementStates.Downloading, percent: 40));

        var downloading = Render<LocalAiModelFilesPanel>(parameters => parameters.Add(p => p.PollInterval, TimeSpan.FromHours(1)));

        Assert.Contains("Downloading 40%", downloading.Markup, StringComparison.Ordinal);
        Assert.Contains(downloading.FindAll("button"), button => button.TextContent.Trim() == "Cancel");

        _onGet = _ => Tools(
            Tool("ai_model:text_fast", "Local AI text model", kind: "ai_model", requirement: ToolRequirementLevels.Optional, state: ToolRequirementStates.Ready));
        var ready = Render<LocalAiModelFilesPanel>();
        // The stub reports text_fast as Ready and not loaded, so it can be loaded (the old Local AI page's Load button).
        Assert.Contains(ready.FindAll("button"), button => button.TextContent.Trim() == "Load");
    }

    [Fact]
    public void ToolsPage_CheckAgainAsksTheEngineToLookAgain_AndShowsTheFreshAnswer()
    {
        _onGet = _ => Tools(Tool("ffmpeg", "FFmpeg"));
        _onCheck = () => Tools(Tool("ffmpeg", "FFmpeg", state: ToolRequirementStates.Ready));
        var cut = Render<ToolsRequirementsTab>();
        Assert.Equal("Missing", cut.Find("[data-tool-id=ffmpeg] .app-chip").TextContent.Trim());

        cut.FindAll("button").Single(button => button.TextContent.Contains("Check again", StringComparison.Ordinal)).Click();

        cut.WaitForAssertion(() => Assert.Equal("Ready", cut.Find("[data-tool-id=ffmpeg] .app-chip").TextContent.Trim()));
        Assert.Equal(1, _checkCalls);
        Assert.Contains("Everything the Library needs is ready.", cut.Find("[data-testid=tools-message]").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void ToolsPage_WhenTheEngineCannotBeAsked_SaysSoAndOffersRetry()
    {
        _onGet = _ => null;

        var cut = Render<ToolsRequirementsTab>();

        Assert.Contains("Could not check the tools", cut.Markup, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("[data-tool-id]"));
    }

    [Fact]
    public async Task ToolsPage_KeepsRefreshingWhileAModelDownloads_AndStopsOnceNothingIsDownloading()
    {
        _onGet = call => call < 3
            ? Tools(Tool("ai_model:text_fast", "Local AI text model", kind: "ai_model", requirement: ToolRequirementLevels.Optional,
                state: ToolRequirementStates.Downloading, percent: call * 30))
            : Tools(Tool("ai_model:text_fast", "Local AI text model", kind: "ai_model", requirement: ToolRequirementLevels.Optional,
                state: ToolRequirementStates.Ready));

        var cut = Render<ToolsRequirementsTab>(parameters => parameters.Add(p => p.PollInterval, TimeSpan.FromMilliseconds(40)));

        cut.WaitForAssertion(() => Assert.Equal(3, Volatile.Read(ref _getCalls)), TimeSpan.FromSeconds(5));
        await Task.Delay(400);
        Assert.Equal(3, Volatile.Read(ref _getCalls));
    }

    [Fact]
    public async Task ToolsPage_StopsRefreshingWhenThePageIsClosed()
    {
        _onGet = _ => Tools(Tool("ai_model:text_fast", "Local AI text model", kind: "ai_model", requirement: ToolRequirementLevels.Optional,
            state: ToolRequirementStates.Downloading, percent: 10));
        var cut = Render<ToolsRequirementsTab>(parameters => parameters.Add(p => p.PollInterval, TimeSpan.FromMilliseconds(40)));
        cut.WaitForAssertion(() => Assert.True(Volatile.Read(ref _getCalls) >= 3), TimeSpan.FromSeconds(5));

        await DisposeComponentsAsync();
        await Task.Delay(100);
        var afterClose = Volatile.Read(ref _getCalls);
        await Task.Delay(300);

        Assert.Equal(afterClose, Volatile.Read(ref _getCalls));
    }

    [Fact]
    public void ToolsPage_IsRegisteredInSettingsAndRendersFromTheSettingsPage()
    {
        var settings = File.ReadAllText(RepoFile("src", "MediaEngine.Web", "Components", "Pages", "Settings.razor"));

        Assert.Contains("case SettingsSection.Tools:", settings, StringComparison.Ordinal);
        Assert.Contains("<ConfigurationPage />", settings, StringComparison.Ordinal);
        Assert.Contains("<ToolsRequirementsTab />", File.ReadAllText(RepoFile("src", "MediaEngine.Web", "Components", "Settings", "ConfigurationPage.razor")), StringComparison.Ordinal);
    }

    // ── Local AI keeps only AI settings ──────────────────────────────────────

    [Fact]
    public void LocalAiStatus_KeepsOnlyTheEngineCard_AndPointsToItsOtherPages()
    {
        var cut = Render<LocalAiSettingsTab>();

        cut.WaitForAssertion(() => Assert.Contains("AI features are plugins", cut.Markup, StringComparison.Ordinal));
        Assert.Contains(cut.FindAll("a"), link => link.GetAttribute("href") == "/settings/ai/models");
        // The runtime is its own Local AI page and the per-model cards (and their Download, Cancel, Load and Unload buttons) are on the tools page.
        Assert.DoesNotContain("Runtime settings", cut.Markup, StringComparison.Ordinal);
        foreach (var label in new[] { "Download", "Cancel", "Load", "Unload" })
        {
            Assert.DoesNotContain(cut.FindAll("button"), button => button.TextContent.Trim() == label);
        }

        Assert.DoesNotContain("fast.gguf", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void ToolsPage_DoesNotRepeatThePlaybackToolsStatus_OrHoldTheLocalAiRuntime()
    {
        _onGet = _ => Tools(Tool("ffmpeg", "FFmpeg", state: ToolRequirementStates.Ready));

        var cut = Render<ToolsRequirementsTab>();

        cut.WaitForAssertion(() => Assert.Contains("FFmpeg", cut.Markup, StringComparison.Ordinal));
        // The playback tools status is shown once, under Streaming > Diagnostics.
        Assert.DoesNotContain("Playback tools and services", cut.Markup, StringComparison.Ordinal);
        // The runtime (resource profile, limits, diagnostics) is Local AI > Runtime until Developer tools rebuilds Local AI.
        Assert.Empty(cut.FindAll("[data-testid=local-ai-runtime]"));
        Assert.DoesNotContain("Runtime settings", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void LocalAiRuntimePage_HoldsTheResourceProfileRuntimeSettingsAndDiagnostics()
    {
        var cut = Render<LocalAiSettingsTab>(parameters => parameters.Add(p => p.Subsection, "models"));

        cut.WaitForAssertion(() => Assert.Contains("Runtime settings", cut.Find("[data-testid=local-ai-runtime]").TextContent, StringComparison.Ordinal));
        Assert.DoesNotContain("AI features are plugins", cut.Markup, StringComparison.Ordinal);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private IRenderedComponent<SetupReadinessStage> RenderReadiness(ToolRequirementsDto tools) =>
        Render<SetupReadinessStage>(parameters => parameters
            .Add(p => p.Readiness, new SetupReadinessDto(true,
                [new SetupCapabilityDto("administrator", "Administrator", "passed", true, "Ready.", null)]))
            .Add(p => p.Tools, tools));

    private static SetupPreflightDto Passed(ToolRequirementsDto tools) =>
        new(true, false, string.Empty, "win-x64", "X64", "X64", [], tools);

    private static ToolRequirementsDto Tools(params ToolRequirementDto[] rows) => new(
        ToolInstallKinds.Source,
        DateTimeOffset.UnixEpoch,
        rows.Any(row => row.Requirement == ToolRequirementLevels.Required && row.State != ToolRequirementStates.Ready),
        rows);

    private static ToolRequirementDto Tool(
        string id,
        string name,
        string kind = "tool",
        string requirement = ToolRequirementLevels.Required,
        string state = ToolRequirementStates.Missing,
        int waiting = 0,
        bool canLocate = false,
        bool canDownloadAgain = false,
        int? percent = null) =>
        new(id, name, kind, requirement, state, FfmpegEffect, null,
            state == ToolRequirementStates.Ready ? string.Empty : FfmpegFix,
            null, waiting, canLocate && state != ToolRequirementStates.Ready, false, canDownloadAgain, percent);

    private static string RepoFile(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MediaEngine.slnx")))
        {
            directory = directory.Parent;
        }

        return Path.Combine([directory?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found."), .. parts]);
    }
}
