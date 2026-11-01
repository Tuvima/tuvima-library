using Bunit;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Contracts.Settings;
using MediaEngine.Web.Components.Settings;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Navigation;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Web.Tests;

/// <summary>
/// The administrator attention banner above every Settings page, the one shared state behind it, and the permanent
/// Local AI and remote access notices: who sees what, how a dismissed line stays dismissed until something new needs
/// attention, and that Install for me and download progress still work from the banner.
/// </summary>
public sealed class SettingsAttentionBannerTests : AsyncBunitContext
{
    private const string FfmpegEffect = "Videos, music and audiobooks wait until FFmpeg is available.";

    private readonly DashboardSessionAccessor _session = new();
    private readonly DeviceContextService _device;
    private readonly IEngineApiClient _api;
    private readonly List<string> _installs = [];
    private readonly List<string> _modelDownloads = [];
    private readonly Dictionary<string, string> _stored = new(StringComparer.Ordinal);
    private int _toolReads;
    private int _reviewCount;
    private bool _reviewCountReadFails;
    private Func<ToolRequirementsDto?> _onTools = () => null;
    private Func<string, ToolActionResult> _onInstall = _ => new(false, ErrorMessage: "No.");

    public SettingsAttentionBannerTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        Services.AddLocalization();
        _api = EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetToolRequirementsAsync), _ =>
            {
                Interlocked.Increment(ref _toolReads);
                return Task.FromResult(_onTools());
            });
            stub.SetHandler(nameof(IEngineApiClient.InstallToolAsync), args =>
            {
                _installs.Add((string)args![0]!);
                return Task.FromResult(_onInstall((string)args[0]!));
            });
            stub.SetHandler(nameof(IEngineApiClient.GetReviewCountAsync), _ =>
                Task.FromResult<int?>(_reviewCountReadFails ? null : _reviewCount));
            stub.SetHandler(nameof(IEngineApiClient.GetSettingsNoticesAsync), _ =>
                Task.FromResult<ProfileNoticesResponse?>(new ProfileNoticesResponse(
                    _stored.Select(pair => new ProfileNoticeDismissalDto(pair.Key, pair.Value)).ToList())));
            stub.SetHandler(nameof(IEngineApiClient.DismissSettingsNoticeAsync), args =>
            {
                _stored[(string)args![0]!] = (string)args[1]!;
                return Task.FromResult(true);
            });
            stub.SetHandler(nameof(IEngineApiClient.StartAiModelDownloadAsync), args =>
            {
                _modelDownloads.Add((string)args![0]!);
                return Task.FromResult(AiOperationResultDto.Success());
            });
        });
        _device = new DeviceContextService(_api);
        Services.AddSingleton(_api);
        Services.AddSingleton(_session);
        Services.AddSingleton(_device);
        Services.AddSingleton(serviceProvider => NewOrchestrator(serviceProvider));
        Services.AddSingleton<SettingsAttentionState>();
    }

    private UIOrchestratorService NewOrchestrator(IServiceProvider serviceProvider) => new(
        _api,
        new UniverseStateContainer(),
        new ActiveProfileSessionService(
            serviceProvider.GetRequiredService<Microsoft.JSInterop.IJSRuntime>(), _api,
            authenticationStateProvider: new SessionOnlyAuthentication(), dashboardSession: _session),
        new ConfigurationBuilder().Build(),
        NullLogger<UIOrchestratorService>.Instance,
        dashboardSession: _session);

    private SettingsAttentionState State => Services.GetRequiredService<SettingsAttentionState>();

    // ── Who sees the banner ──────────────────────────────────────────────────

    [Fact]
    public async Task ServerAdministrator_WithAMissingToolAndThreeReviewItems_SeesBothLines_MissingToolFirst()
    {
        SignIn(SettingsRole.ServerAdministrator);
        _reviewCount = 3;
        _onTools = () => Tools(Tool("ffmpeg", "FFmpeg", canInstall: true));
        await State.StartAsync();

        var cut = Render<SettingsAttentionBanner>();

        var notices = cut.FindAll("[data-settings-notice]");
        Assert.Equal(["missing-tools", "needs-review"], notices.Select(notice => notice.GetAttribute("data-settings-notice")));
        Assert.Contains("FFmpeg is missing. " + FfmpegEffect, notices[0].TextContent, StringComparison.Ordinal);
        Assert.Contains("3 items need review", notices[1].TextContent, StringComparison.Ordinal);
        Assert.Contains(notices[0].QuerySelectorAll("button"), button => button.TextContent.Trim() == "Install for me");
        Assert.Contains(notices[1].QuerySelectorAll("a"), link => link.TextContent.Trim() == "Review"
            && link.GetAttribute("href") == "/settings/review");
        Assert.All(notices, notice => Assert.Single(notice.QuerySelectorAll("button[aria-label='Dismiss']")));
        Assert.True(notices.Count <= SettingsAttentionBanner.MaxLines);
    }

    [Theory]
    [InlineData(SettingsRole.HouseholdAdministrator)]
    [InlineData(SettingsRole.Member)]
    public async Task HouseholdAdministratorAndMember_SeeNeitherLine_AndTheEngineIsNotAsked(SettingsRole role)
    {
        SignIn(role);
        _reviewCount = 3;
        _onTools = () => Tools(Tool("ffmpeg", "FFmpeg", canInstall: true));
        await State.StartAsync();

        var cut = Render<SettingsAttentionBanner>();

        Assert.Empty(cut.FindAll("[data-settings-notice]"));
        Assert.Equal(string.Empty, cut.Markup.Trim());
        Assert.Equal(0, Volatile.Read(ref _toolReads));
        Assert.Equal(0, State.ReviewCount);
        Assert.False(State.LocalAiBlocked);
    }

    [Fact]
    public async Task OnAPhone_NoLineIsDrawn_EvenForAServerAdministrator()
    {
        SignIn(SettingsRole.ServerAdministrator);
        _reviewCount = 3;
        _onTools = () => Tools(Tool("ffmpeg", "FFmpeg", canInstall: true));
        await _device.InitialiseAsync(new DeviceContextSnapshot("mobile", "phone", true));
        await State.StartAsync();

        var cut = Render<SettingsAttentionBanner>();

        Assert.Empty(cut.FindAll("[data-settings-notice]"));
    }

    [Fact]
    public async Task WithNothingPending_TheBannerDrawsNothingAtAll()
    {
        SignIn(SettingsRole.ServerAdministrator);
        _onTools = () => Tools(Tool("ffmpeg", "FFmpeg", state: ToolRequirementStates.Ready));
        await State.StartAsync();

        var cut = Render<SettingsAttentionBanner>();

        Assert.Equal(string.Empty, cut.Markup.Trim());
    }

    [Fact]
    public async Task BeforeTheFirstReadHasFinished_NothingIsDrawn()
    {
        SignIn(SettingsRole.ServerAdministrator);
        _reviewCount = 3;
        _onTools = () => Tools(Tool("ffmpeg", "FFmpeg", canInstall: true));

        var cut = Render<SettingsAttentionBanner>();
        Assert.Empty(cut.FindAll("[data-settings-notice]"));

        await State.StartAsync();

        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[data-settings-notice]").Count));
    }

    // ── Dismissing ───────────────────────────────────────────────────────────

    [Fact]
    public async Task TheX_HidesTheLineAtOnce_RemembersItWithTheFingerprint_AndKeepsTheMenuShading()
    {
        SignIn(SettingsRole.ServerAdministrator);
        _reviewCount = 3;
        _onTools = () => Tools(Tool("ffmpeg", "FFmpeg", canInstall: true));
        await State.StartAsync();
        var cut = Render<SettingsAttentionBanner>();

        cut.Find("[data-settings-notice='needs-review'] button[aria-label='Dismiss']").Click();

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-settings-notice='needs-review']")));
        Assert.Single(cut.FindAll("[data-settings-notice='missing-tools']"));
        Assert.Equal("3", _stored["needs-review"]);

        // The shading in the Settings menu keeps reading the same shared state, so it stays after the line is dismissed.
        Assert.Equal(3, State.ReviewCount);
        var groups = SettingsMenuBuilder.Build(MenuContext(State));
        Assert.Equal(MediaSectionAttentionLevel.Waiting, Flatten(groups).Single(row => row.Label == "Libraries").Attention);

        cut.Find("[data-settings-notice='missing-tools'] button[aria-label='Dismiss']").Click();
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-settings-notice]")));
        Assert.Equal("ffmpeg", _stored["missing-tools"]);
    }

    [Fact]
    public async Task ADismissedLine_StaysDismissedAcrossARerender_AndAfterAReload()
    {
        SignIn(SettingsRole.ServerAdministrator);
        _reviewCount = 3;
        _onTools = () => Tools(Tool("ffmpeg", "FFmpeg", canInstall: true));
        await State.StartAsync();
        var cut = Render<SettingsAttentionBanner>();
        cut.Find("[data-settings-notice='needs-review'] button[aria-label='Dismiss']").Click();
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-settings-notice='needs-review']")));

        var again = Render<SettingsAttentionBanner>();
        Assert.Empty(again.FindAll("[data-settings-notice='needs-review']"));

        // A reload is a new circuit: a new state that asks the Engine what this profile dismissed.
        var reloaded = new SettingsAttentionState(_api, NewOrchestrator(Services), _session);
        await reloaded.StartAsync();
        Assert.False(reloaded.ReviewLineVisible);
        Assert.True(reloaded.ToolsLineVisible);
        reloaded.Stop();
    }

    [Fact]
    public async Task TheReviewLine_ReturnsOnlyWhenTheCountGrows_AndAfterItDropsToZeroThenRises()
    {
        SignIn(SettingsRole.ServerAdministrator);
        _reviewCount = 3;
        _onTools = () => Tools();
        await State.StartAsync();
        await State.DismissReviewAsync();
        Assert.False(State.ReviewLineVisible);

        _reviewCount = 2;
        await State.RefreshAsync(reloadDismissals: false);
        Assert.False(State.ReviewLineVisible);

        _reviewCount = 4;
        await State.RefreshAsync(reloadDismissals: false);
        Assert.True(State.ReviewLineVisible);

        await State.DismissReviewAsync();
        Assert.False(State.ReviewLineVisible);

        _reviewCount = 0;
        await State.RefreshAsync(reloadDismissals: false);
        Assert.Equal("0", _stored["needs-review"]);
        _reviewCount = 1;
        await State.RefreshAsync(reloadDismissals: false);
        Assert.True(State.ReviewLineVisible);
    }

    [Fact]
    public async Task TheToolsLine_ReturnsOnlyWhenAToolThatWasNotDismissedGoesMissing()
    {
        SignIn(SettingsRole.ServerAdministrator);
        _onTools = () => Tools(Tool("ffmpeg", "FFmpeg"));
        await State.StartAsync();
        await State.DismissToolsAsync();
        Assert.False(State.ToolsLineVisible);

        // The same tool still missing: nothing new.
        await State.RefreshAsync(reloadDismissals: false);
        Assert.False(State.ToolsLineVisible);

        // A different Required tool goes missing too.
        _onTools = () => Tools(Tool("ffmpeg", "FFmpeg"), Tool("image_engine", "Image engine", state: ToolRequirementStates.Problem));
        await State.RefreshAsync(reloadDismissals: false);
        Assert.True(State.ToolsLineVisible);
        Assert.Equal(["ffmpeg", "image_engine"], State.MissingTools.Select(tool => tool.Id));

        // Once everything works the dismissal is forgotten, so a later problem is shown afresh.
        await State.DismissToolsAsync();
        Assert.False(State.ToolsLineVisible);
        _onTools = () => Tools(Tool("ffmpeg", "FFmpeg", state: ToolRequirementStates.Ready), Tool("image_engine", "Image engine", state: ToolRequirementStates.Ready));
        await State.RefreshAsync(reloadDismissals: false);
        _onTools = () => Tools(Tool("ffmpeg", "FFmpeg"));
        await State.RefreshAsync(reloadDismissals: false);
        Assert.True(State.ToolsLineVisible);
    }

    [Fact]
    public async Task AFailedReviewCountRead_KeepsTheSavedDismissal_AndASuccessfulZeroThenResetsIt()
    {
        SignIn(SettingsRole.ServerAdministrator);
        _reviewCount = 3;
        _onTools = () => Tools();
        await State.StartAsync();
        await State.DismissReviewAsync();
        Assert.Equal("3", _stored["needs-review"]);

        // The Engine cannot answer: that is not "nothing needs review", so the choice is kept and the count stays as known.
        _reviewCountReadFails = true;
        _reviewCount = 0;
        await State.RefreshAsync(reloadDismissals: false);
        Assert.Equal("3", _stored["needs-review"]);
        Assert.Equal(3, State.ReviewCount);
        Assert.False(State.ReviewLineVisible);

        // A reload of the page reads the saved choice again and still finds it.
        await State.RefreshAsync(reloadDismissals: true);
        Assert.Equal("3", _stored["needs-review"]);

        // The Engine really answers zero: now the dismissal is replaced by the "nothing was wrong" mark.
        _reviewCountReadFails = false;
        await State.RefreshAsync(reloadDismissals: false);
        Assert.Equal("0", _stored["needs-review"]);
    }

    [Fact]
    public async Task AFailedToolsRead_KeepsTheSavedToolsDismissal_AndASuccessfulCleanReadResetsIt()
    {
        SignIn(SettingsRole.ServerAdministrator);
        _onTools = () => Tools(Tool("ffmpeg", "FFmpeg"));
        await State.StartAsync();
        await State.DismissToolsAsync();
        Assert.Equal("ffmpeg", _stored["missing-tools"]);

        // The catalogue could not be read: the earlier read must not count as "every tool works".
        _onTools = () => null;
        await State.RefreshAsync(reloadDismissals: false);
        Assert.Equal("ffmpeg", _stored["missing-tools"]);
        Assert.False(State.ToolsLineVisible);

        _onTools = () => Tools(Tool("ffmpeg", "FFmpeg", state: ToolRequirementStates.Ready));
        await State.RefreshAsync(reloadDismissals: false);
        Assert.Equal("none", _stored["missing-tools"]);
    }

    // ── The missing tools line ───────────────────────────────────────────────

    [Fact]
    public async Task ADownloadingTool_ShowsItsProgress_WithNoButtonAndNoX_EvenAfterTheLineWasDismissed()
    {
        SignIn(SettingsRole.ServerAdministrator);
        _onTools = () => Tools(Tool("ffmpeg", "FFmpeg"));
        await State.StartAsync();
        await State.DismissToolsAsync();
        _onTools = () => Tools(Tool("ffmpeg", "FFmpeg", state: ToolRequirementStates.Downloading, percent: 30));
        await State.RefreshAsync(reloadDismissals: false);

        var cut = Render<SettingsAttentionBanner>();

        var notice = cut.Find("[data-settings-notice='missing-tools']");
        Assert.Contains("Downloading FFmpeg 30%", notice.TextContent, StringComparison.Ordinal);
        Assert.Empty(notice.QuerySelectorAll("button"));
        Assert.Empty(notice.QuerySelectorAll("a"));
    }

    [Fact]
    public async Task InstallForMe_StartsTheDownload_AndTheLineTurnsIntoProgress()
    {
        SignIn(SettingsRole.ServerAdministrator);
        _onTools = () => Tools(Tool("ffmpeg", "FFmpeg", canInstall: true));
        _onInstall = _ => new(true, Tools(Tool("ffmpeg", "FFmpeg", state: ToolRequirementStates.Downloading, percent: 30)));
        await State.StartAsync();
        var cut = Render<SettingsAttentionBanner>();

        cut.FindAll("button").Single(button => button.TextContent.Trim() == "Install for me").Click();

        cut.WaitForAssertion(() => Assert.Contains("Downloading FFmpeg 30%", cut.Markup, StringComparison.Ordinal));
        Assert.Equal(["ffmpeg"], _installs);
        Assert.DoesNotContain(cut.FindAll("button"), button => button.TextContent.Trim() == "Install for me");
    }

    [Fact]
    public async Task AFailedInstallRequest_ShowsThePlainReason()
    {
        SignIn(SettingsRole.ServerAdministrator);
        _onTools = () => Tools(Tool("ffmpeg", "FFmpeg", canInstall: true));
        _onInstall = _ => new(false, ErrorMessage: "That is already being downloaded.");
        await State.StartAsync();
        var cut = Render<SettingsAttentionBanner>();

        cut.FindAll("button").Single(button => button.TextContent.Trim() == "Install for me").Click();

        cut.WaitForAssertion(() => Assert.Contains("That is already being downloaded.", cut.Markup, StringComparison.Ordinal));
    }

    [Fact]
    public async Task WhereTheEngineOffersNoInstall_TheButtonIsSeeTools_LinkingToTheToolsPage()
    {
        SignIn(SettingsRole.ServerAdministrator);
        _onTools = () => Tools(Tool("ffmpeg", "FFmpeg", canInstall: false));
        await State.StartAsync();

        var cut = Render<SettingsAttentionBanner>();

        Assert.DoesNotContain(cut.FindAll("button"), button => button.TextContent.Trim() == "Install for me");
        Assert.Contains(cut.FindAll("a"), link => link.TextContent.Trim() == "See tools" && link.GetAttribute("href") == "/settings/tools");
    }

    [Fact]
    public async Task ForABrokenTool_TheButtonIsDownloadAgain_AndTheTextSaysItIsNotWorking()
    {
        SignIn(SettingsRole.ServerAdministrator);
        _onTools = () => Tools(Tool("ffmpeg", "FFmpeg", state: ToolRequirementStates.Problem, canDownloadAgain: true));
        _onInstall = _ => new(true, Tools(Tool("ffmpeg", "FFmpeg", state: ToolRequirementStates.Downloading, percent: 5)));
        await State.StartAsync();
        var cut = Render<SettingsAttentionBanner>();

        Assert.Contains("FFmpeg isn't working.", cut.Markup, StringComparison.Ordinal);
        cut.FindAll("button").Single(button => button.TextContent.Trim() == "Download again").Click();

        cut.WaitForAssertion(() => Assert.Equal(["ffmpeg"], _installs));
    }

    [Fact]
    public async Task SeveralMissingTools_AreOneLine_AndInstallForMeStartsEachOneTheEngineOffers()
    {
        SignIn(SettingsRole.ServerAdministrator);
        _onTools = () => Tools(Tool("ffmpeg", "FFmpeg", canInstall: true), Tool("image_engine", "Image engine", canInstall: true));
        _onInstall = _ => new(true, Tools(Tool("ffmpeg", "FFmpeg", state: ToolRequirementStates.Downloading, percent: 10),
            Tool("image_engine", "Image engine", state: ToolRequirementStates.Downloading, percent: 20)));
        await State.StartAsync();
        var cut = Render<SettingsAttentionBanner>();

        Assert.Contains("2 tools are missing.", cut.Find("[data-settings-notice='missing-tools']").TextContent, StringComparison.Ordinal);
        cut.FindAll("button").Single(button => button.TextContent.Trim() == "Install for me").Click();

        cut.WaitForAssertion(() => Assert.Equal(["ffmpeg", "image_engine"], _installs));
    }

    [Fact]
    public async Task AToolThatHoldsFilesBack_IsRed_OtherwiseAmber()
    {
        SignIn(SettingsRole.ServerAdministrator);
        _onTools = () => Tools(Tool("ffmpeg", "FFmpeg", waitingFiles: 12));
        await State.StartAsync();
        var red = Render<SettingsAttentionBanner>();
        Assert.Contains("settings-notice--danger", red.Find("[data-settings-notice='missing-tools']").ClassName, StringComparison.Ordinal);

        _onTools = () => Tools(Tool("ffmpeg", "FFmpeg", waitingFiles: 0));
        await State.RefreshAsync(reloadDismissals: false);
        var amber = Render<SettingsAttentionBanner>();
        Assert.Contains("settings-notice--warning", amber.Find("[data-settings-notice='missing-tools']").ClassName, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OptionalAndReadyTools_AreNotShown()
    {
        SignIn(SettingsRole.ServerAdministrator);
        _onTools = () => Tools(
            Tool("tesseract", "Tesseract", requirement: ToolRequirementLevels.Optional, canInstall: true),
            Tool("image_engine", "Image engine", state: ToolRequirementStates.Ready));
        await State.StartAsync();

        var cut = Render<SettingsAttentionBanner>();

        Assert.Empty(cut.FindAll("[data-settings-notice]"));
    }

    [Fact]
    public async Task OnTheToolsPage_TheToolsLineIsNotShown_ButNeedsReviewStillIs()
    {
        SignIn(SettingsRole.ServerAdministrator);
        _reviewCount = 2;
        _onTools = () => Tools(Tool("ffmpeg", "FFmpeg", canInstall: true));
        await State.StartAsync();
        Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().NavigateTo("/settings/tools");

        var cut = Render<SettingsAttentionBanner>();

        Assert.Equal(["needs-review"], cut.FindAll("[data-settings-notice]").Select(notice => notice.GetAttribute("data-settings-notice")));
        Assert.Contains("2 items need review", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OneReviewItem_ReadsInTheSingular()
    {
        SignIn(SettingsRole.ServerAdministrator);
        _reviewCount = 1;
        _onTools = () => Tools();
        await State.StartAsync();

        var cut = Render<SettingsAttentionBanner>();

        Assert.Contains("1 item needs review", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WhenNothingIsDownloading_TheEngineIsNotAskedAgainRightAway()
    {
        SignIn(SettingsRole.ServerAdministrator);
        _onTools = () => Tools(Tool("ffmpeg", "FFmpeg", canInstall: true));
        State.ProgressInterval = TimeSpan.FromMilliseconds(20);
        State.RefreshInterval = TimeSpan.FromHours(1);

        await State.StartAsync();
        await Task.Delay(300);

        Assert.Equal(1, Volatile.Read(ref _toolReads));
    }

    [Fact]
    public async Task WhileADownloadRuns_TheStateReadsProgressEveryFewSeconds_AndStopsAfterStop()
    {
        SignIn(SettingsRole.ServerAdministrator);
        _onTools = () => Tools(Tool("ffmpeg", "FFmpeg", state: ToolRequirementStates.Downloading, percent: 30));
        State.ProgressInterval = TimeSpan.FromMilliseconds(20);
        State.RefreshInterval = TimeSpan.FromHours(1);

        await State.StartAsync();
        await Task.Delay(300);
        State.Stop();
        await Task.Delay(100);
        var afterStop = Volatile.Read(ref _toolReads);
        await Task.Delay(300);

        Assert.True(afterStop > 2, $"Expected progress reads while downloading, saw {afterStop}.");
        Assert.Equal(afterStop, Volatile.Read(ref _toolReads));
    }

    // ── Local AI and remote access page notices ──────────────────────────────

    [Fact]
    public async Task LocalAiNotice_ShowsWhileBlocked_HasNoDismiss_AndDownloadStartsTheMissingModels()
    {
        SignIn(SettingsRole.ServerAdministrator);
        _onTools = () => Tools(Model("text_fast", ToolRequirementStates.Missing), Model("audio", ToolRequirementStates.Ready));
        await State.StartAsync();

        var cut = Render<LocalAiBlockedNotice>();

        var notice = cut.Find("[data-settings-notice='local-ai-off']");
        Assert.Contains("Local AI is switched off until the tools it needs are downloaded.", notice.TextContent, StringComparison.Ordinal);
        Assert.Empty(notice.QuerySelectorAll("button[aria-label='Dismiss']"));
        Assert.Contains("settings-notice--danger", notice.ClassName, StringComparison.Ordinal);

        cut.FindAll("button").Single(button => button.TextContent.Trim() == "Download").Click();

        cut.WaitForAssertion(() => Assert.Equal(["text_fast"], _modelDownloads));
    }

    [Fact]
    public async Task LocalAiNotice_WhereNothingCanBeDownloadedHere_LinksToTheToolsPage()
    {
        SignIn(SettingsRole.ServerAdministrator);
        _onTools = () => Tools(Tool("ai_runtime", "Local AI engine", requirement: ToolRequirementLevels.Optional, state: ToolRequirementStates.Problem));
        await State.StartAsync();

        var cut = Render<LocalAiBlockedNotice>();

        Assert.Contains(cut.FindAll("a"), link => link.TextContent.Trim() == "See tools" && link.GetAttribute("href") == "/settings/tools");
        Assert.DoesNotContain(cut.FindAll("button"), button => button.TextContent.Trim() == "Download");
    }

    [Fact]
    public async Task LocalAiNotice_IsGoneOnceEverythingIsReady_AndNeverShownToAnyoneElse()
    {
        SignIn(SettingsRole.ServerAdministrator);
        _onTools = () => Tools(Model("text_fast", ToolRequirementStates.Ready), Tool("ai_runtime", "Local AI engine", requirement: ToolRequirementLevels.Optional, state: ToolRequirementStates.Ready));
        await State.StartAsync();
        Assert.Empty(Render<LocalAiBlockedNotice>().FindAll("[data-settings-notice]"));

        SignIn(SettingsRole.Member);
        _onTools = () => Tools(Model("text_fast", ToolRequirementStates.Missing));
        await State.RefreshAsync(reloadDismissals: false);
        Assert.Empty(Render<LocalAiBlockedNotice>().FindAll("[data-settings-notice]"));
    }

    [Fact]
    public async Task LocalAi_WhenTheEngineAndEveryModelAreMissing_IsBlocked()
    {
        SignIn(SettingsRole.ServerAdministrator);
        _onTools = () => Tools(
            Tool("ai_runtime", "Local AI engine", requirement: ToolRequirementLevels.Optional),
            Model("text_fast", ToolRequirementStates.Missing),
            Model("text_quality", ToolRequirementStates.Missing),
            Model("audio", ToolRequirementStates.Missing));
        await State.StartAsync();

        Assert.True(State.LocalAiBlocked);
        Assert.Equal(["ai_model:text_fast", "ai_runtime"], State.LocalAiBlockers.Select(tool => tool.Id).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task LocalAi_WhenTheEngineAndTheFastTextModelAreReady_IsNotBlockedByTheOptionalModels()
    {
        SignIn(SettingsRole.ServerAdministrator);
        _onTools = () => Tools(
            Tool("ai_runtime", "Local AI engine", requirement: ToolRequirementLevels.Optional, state: ToolRequirementStates.Ready),
            Model("text_fast", ToolRequirementStates.Ready),
            Model("text_quality", ToolRequirementStates.Missing),
            Model("audio", ToolRequirementStates.Missing),
            Model("text_scholar", ToolRequirementStates.Problem));
        await State.StartAsync();

        Assert.False(State.LocalAiBlocked);
        Assert.Empty(Render<LocalAiBlockedNotice>().FindAll("[data-settings-notice]"));
        Assert.Equal(MediaSectionAttentionLevel.None,
            Flatten(SettingsMenuBuilder.Build(MenuContext(State))).Single(row => row.Label == "Local AI").EffectiveAttention);
    }

    [Fact]
    public async Task LocalAi_WhenOnlyTheFastTextModelIsMissing_IsBlocked_AndDownloadFetchesJustThatModel()
    {
        SignIn(SettingsRole.ServerAdministrator);
        _onTools = () => Tools(
            Tool("ai_runtime", "Local AI engine", requirement: ToolRequirementLevels.Optional, state: ToolRequirementStates.Ready),
            Model("text_fast", ToolRequirementStates.Missing),
            Model("audio", ToolRequirementStates.Missing));
        await State.StartAsync();
        Assert.True(State.LocalAiBlocked);

        await State.DownloadLocalAiAsync();

        Assert.Equal(["text_fast"], _modelDownloads);
    }

    [Fact]
    public async Task LocalAi_WhenTheEngineItselfIsNotReady_IsBlocked_EvenWithEveryModelReady()
    {
        SignIn(SettingsRole.ServerAdministrator);
        _onTools = () => Tools(
            Tool("ai_runtime", "Local AI engine", requirement: ToolRequirementLevels.Optional, state: ToolRequirementStates.Problem),
            Model("text_fast", ToolRequirementStates.Ready),
            Model("audio", ToolRequirementStates.Ready));
        await State.StartAsync();

        Assert.True(State.LocalAiBlocked);
    }

    [Fact]
    public async Task LocalAiBlocked_MakesTheMenuRowRedWithBroken_AndToolsAreNeverShaded()
    {
        SignIn(SettingsRole.ServerAdministrator);
        _reviewCount = 3;
        _onTools = () => Tools(Tool("ffmpeg", "FFmpeg"), Model("text_fast", ToolRequirementStates.Missing));
        await State.StartAsync();

        var groups = SettingsMenuBuilder.Build(MenuContext(State));
        var rows = Flatten(groups).ToArray();

        Assert.Equal(MediaSectionAttentionLevel.Blocked, rows.Single(row => row.Label == "Local AI").Attention);
        Assert.Equal("Broken", rows.Single(row => row.Label == "Local AI").AttentionLabel);
        Assert.Equal(MediaSectionAttentionLevel.Waiting, rows.Single(row => row.Label == "Libraries").Attention);
        Assert.Null(rows.Single(row => row.Label == "Libraries").Meta);
        Assert.Equal(MediaSectionAttentionLevel.None, rows.Single(row => row.Label == "Tools").EffectiveAttention);
        Assert.Equal(MediaSectionAttentionLevel.None, rows.Single(row => row.Label == "System").EffectiveAttention);
    }

    [Fact]
    public async Task OnAPhone_NeitherPageNoticeIsDrawn_AndOnADesktopBothAre()
    {
        SignIn(SettingsRole.ServerAdministrator);
        _onTools = () => Tools(Model("text_fast", ToolRequirementStates.Missing));
        await State.StartAsync();

        Assert.Single(Render<LocalAiBlockedNotice>().FindAll("[data-settings-notice='local-ai-off']"));
        Assert.Single(Render<RemoteAccessOffNotice>().FindAll("[data-settings-notice='remote-access-off']"));

        await _device.InitialiseAsync(new DeviceContextSnapshot("mobile", "phone", true));

        Assert.Empty(Render<LocalAiBlockedNotice>().FindAll("[data-settings-notice]"));
        Assert.Empty(Render<RemoteAccessOffNotice>().FindAll("[data-settings-notice]"));
    }

    [Fact]
    public void TheSettingsPage_DrawsNoPageNoticeSlotOnAPhone()
    {
        var settings = File.ReadAllText(RepoFile("src", "MediaEngine.Web", "Components", "Pages", "Settings.razor"));

        Assert.Contains("PageNoticeFragment => DeviceContext.IsMobile ? null :", settings, StringComparison.Ordinal);
    }

    [Fact]
    public void RemoteAccessNotice_SaysRemoteAccessIsOff_CannotBeDismissed_AndSetUpOpensTheRemotePage()
    {
        var cut = Render<RemoteAccessOffNotice>();

        var notice = cut.Find("[data-settings-notice='remote-access-off']");
        Assert.Contains("Remote access is off, so the Library only works at home.", notice.TextContent, StringComparison.Ordinal);
        Assert.Empty(notice.QuerySelectorAll("button[aria-label='Dismiss']"));
        Assert.Contains(notice.QuerySelectorAll("a"), link => link.TextContent.Trim() == "Set up"
            && link.GetAttribute("href") == SettingsNav.RouteFor(SettingsSection.Network, "remote"));
    }

    [Fact]
    public void TheNetworkPage_ReportsWhetherRemoteAccessIsNotSetUp()
    {
        var api = EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetNetworkSettingsAsync), _ =>
                Task.FromResult<NetworkSettingsDto?>(new NetworkSettingsDto { Remote = { ConnectionMode = "local-only" } }));
        });
        Services.AddSingleton(api);
        var reports = new List<bool>();

        var cut = Render<NetworkRemoteAccessSettings>(parameters => parameters
            .Add(p => p.Subsection, "overview")
            .Add(p => p.RemoteAccessOffChanged, Microsoft.AspNetCore.Components.EventCallback.Factory.Create<bool>(this, off => reports.Add(off))));
        cut.WaitForAssertion(() => Assert.Equal([true], reports.ToArray()));
    }

    // ── The frame and the shell ──────────────────────────────────────────────

    [Fact]
    public void SettingsPage_HandsTheBannerAndThePageNoticesToTheFrame_AndTheAppShellNoLongerHasTheOldBanner()
    {
        var settings = File.ReadAllText(RepoFile("src", "MediaEngine.Web", "Components", "Pages", "Settings.razor"));
        var layout = File.ReadAllText(RepoFile("src", "MediaEngine.Web", "Shared", "MainLayout.razor"));

        Assert.Contains("Notices=\"@(ShowAttentionBanner ? AttentionBanner : null)\"", settings, StringComparison.Ordinal);
        Assert.Contains("PageNotice=\"@PageNoticeFragment\"", settings, StringComparison.Ordinal);
        Assert.Contains("<SettingsAttentionBanner />", settings, StringComparison.Ordinal);
        Assert.Contains("<LocalAiBlockedNotice />", settings, StringComparison.Ordinal);
        Assert.Contains("<RemoteAccessOffNotice />", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("ToolsAttentionBanner", layout, StringComparison.Ordinal);
        Assert.False(File.Exists(RepoFile("src", "MediaEngine.Web", "Components", "Shared", "Tools", "ToolsAttentionBanner.razor")));
    }

    [Fact]
    public void TheOldAiHealthAlert_IsGoneFromTheLocalAiPage()
    {
        var localAi = File.ReadAllText(RepoFile("src", "MediaEngine.Web", "Components", "Settings", "LocalAiSettingsTab.razor"));

        Assert.DoesNotContain("health-banner", localAi, StringComparison.Ordinal);
        Assert.DoesNotContain("Local AI is available with limitations", localAi, StringComparison.Ordinal);
    }

    [Fact]
    public void TheServerWideNotNow_IsGone_FromTheToolRowsAndTheClient()
    {
        Assert.Null(typeof(ToolRequirementDto).GetProperty("InstallDeclined"));
        Assert.Null(typeof(IEngineApiClient).GetMethod("DeclineToolInstallAsync"));
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    public enum SettingsRole
    {
        ServerAdministrator,
        HouseholdAdministrator,
        Member,
    }

    private static readonly Guid AccountId = Guid.Parse("44444444-4444-4444-8444-444444444444");

    // The stub's default profile, so the Dashboard can resolve who is active.
    private static readonly Guid ProfileId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    private void SignIn(SettingsRole role)
    {
        var administrator = role == SettingsRole.ServerAdministrator;
        IReadOnlyList<string> navigation = administrator ? ["settings.administration"] : [];
        IReadOnlyList<string> actions = role == SettingsRole.HouseholdAdministrator ? ["household.manage"] : [];
        AccountProfileGrantDto[] grants =
        [
            new(AccountId, ProfileId, "Test User", null, true, true, administrator,
                new ProfileAdminProtectionResponse(false, "None", null, 1), 1, DateTimeOffset.UtcNow),
        ];
        _session.Set("token", AccountId, ProfileId, Guid.NewGuid(),
            new DashboardAuthorityResponse(AccountId, ProfileId, true, true, 1, 1, administrator, administrator, null, 1, grants, navigation, actions));
    }

    /// <summary>The active person comes from the Dashboard session; the cookie principal adds nothing.</summary>
    private sealed class SessionOnlyAuthentication : Microsoft.AspNetCore.Components.Authorization.AuthenticationStateProvider
    {
        public override Task<Microsoft.AspNetCore.Components.Authorization.AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new Microsoft.AspNetCore.Components.Authorization.AuthenticationState(
                new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity())));
    }

    private static SettingsMenuContext MenuContext(SettingsAttentionState attention) => new(
        AdministrationAllowed: true,
        IsMobile: false,
        ReviewCount: attention.ReviewCount,
        SecurityNeedsAttention: false,
        LocalAiBlocked: attention.LocalAiBlocked);

    private static IEnumerable<MediaSectionNavigationItem> Flatten(IEnumerable<MediaSectionNavigationGroup> groups) =>
        groups.SelectMany(group => Flatten(group.Items));

    private static IEnumerable<MediaSectionNavigationItem> Flatten(IEnumerable<MediaSectionNavigationItem> items) =>
        items.SelectMany(item => new[] { item }.Concat(item.Children is null ? [] : Flatten(item.Children)));

    private static ToolRequirementsDto Tools(params ToolRequirementDto[] rows) => new(
        ToolInstallKinds.Source,
        DateTimeOffset.UnixEpoch,
        rows.Any(row => row.Requirement == ToolRequirementLevels.Required && row.State != ToolRequirementStates.Ready),
        rows);

    private static ToolRequirementDto Tool(
        string id,
        string name,
        string requirement = ToolRequirementLevels.Required,
        string state = ToolRequirementStates.Missing,
        bool canInstall = false,
        bool canDownloadAgain = false,
        int? percent = null,
        int waitingFiles = 0) =>
        new(id, name, "tool", requirement, state, FfmpegEffect, null,
            state == ToolRequirementStates.Ready ? string.Empty : "Run winget install Gyan.FFmpeg, then press Check again.",
            null, waitingFiles, false, canInstall, canDownloadAgain, percent);

    private static ToolRequirementDto Model(string role, string state) =>
        new(ToolRequirementIds.AiModelPrefix + role, "Local AI " + role + " model", "ai_model", ToolRequirementLevels.Optional, state,
            "Smart text analysis waits until this model is downloaded.", null, "Download it to turn these features on.", null, 0,
            false, false, state is ToolRequirementStates.Missing or ToolRequirementStates.Problem, null);

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
