using Bunit;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Contracts.Settings;
using MediaEngine.Web.Components.Settings;
using MediaEngine.Web.Components.Shared.Tools;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Web.Tests;

/// <summary>
/// Install for me, Download again, Cancel and Run installer on the Tools page, and the administrator-only banner
/// that offers the same install when a Required tool is missing.
/// </summary>
public sealed class ToolsInstallActionsTests : AsyncBunitContext
{
    private const string FfmpegEffect = "Videos, music and audiobooks wait until FFmpeg is available.";

    private readonly List<string> _installs = [];
    private readonly List<string> _cancels = [];
    private readonly List<string> _runs = [];
    private readonly List<string> _declines = [];
    private int _getCalls;
    private Func<ToolRequirementsDto?> _onGet = () => null;
    private Func<string, ToolActionResult> _onInstall = _ => new(false, ErrorMessage: "No.");
    private Func<string, ToolActionResult> _onDecline = _ => new(false, ErrorMessage: "No.");
    private readonly DashboardSessionAccessor _session = new();

    public ToolsInstallActionsTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        Services.AddLocalization();
        var api = EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetToolRequirementsAsync), _ =>
            {
                Interlocked.Increment(ref _getCalls);
                return Task.FromResult(_onGet());
            });
            stub.SetHandler(nameof(IEngineApiClient.InstallToolAsync), args =>
            {
                _installs.Add((string)args![0]!);
                return Task.FromResult(_onInstall((string)args[0]!));
            });
            stub.SetHandler(nameof(IEngineApiClient.CancelToolInstallAsync), args =>
            {
                _cancels.Add((string)args![0]!);
                return Task.FromResult(new ToolActionResult(true, _onGet()));
            });
            stub.SetHandler(nameof(IEngineApiClient.RunToolInstallerAsync), args =>
            {
                _runs.Add((string)args![0]!);
                return Task.FromResult(new ToolActionResult(true, _onGet()));
            });
            stub.SetHandler(nameof(IEngineApiClient.DeclineToolInstallAsync), args =>
            {
                _declines.Add((string)args![0]!);
                return Task.FromResult(_onDecline((string)args[0]!));
            });
        });
        Services.AddSingleton(api);
        Services.AddSingleton(_session);
        Services.AddSingleton(serviceProvider => new UIOrchestratorService(
            api,
            new UniverseStateContainer(),
            new ActiveProfileSessionService(serviceProvider.GetRequiredService<Microsoft.JSInterop.IJSRuntime>(), api),
            new ConfigurationBuilder().Build(),
            NullLogger<UIOrchestratorService>.Instance));
        Services.AddSingleton(new DeviceContextService(api));
    }

    // ── The banner ───────────────────────────────────────────────────────────

    [Fact]
    public void Banner_ForAnAdministrator_ShowsAMissingRequiredTool_WithInstallSeeToolsAndNotNow()
    {
        SignIn(administrator: true);
        _onGet = () => Tools(Tool("ffmpeg", "FFmpeg", canInstall: true));

        var cut = Render<ToolsAttentionBanner>();

        var banner = cut.Find("[data-testid=tools-banner]");
        Assert.Contains("FFmpeg is missing. " + FfmpegEffect, banner.TextContent, StringComparison.Ordinal);
        Assert.Contains(banner.QuerySelectorAll("button"), button => button.TextContent.Trim() == "Install for me");
        Assert.Contains(banner.QuerySelectorAll("button"), button => button.TextContent.Trim() == "Not now");
        Assert.Contains(banner.QuerySelectorAll("a"), link => link.GetAttribute("href") == "/settings/tools");
    }

    [Fact]
    public void Banner_OffersNoInstall_WhenTheEngineDoesNotOfferOne()
    {
        SignIn(administrator: true);
        _onGet = () => Tools(Tool("ffmpeg", "FFmpeg", canInstall: false));

        var cut = Render<ToolsAttentionBanner>();

        Assert.DoesNotContain(cut.FindAll("button"), button => button.TextContent.Trim() == "Install for me");
        Assert.Contains(cut.FindAll("button"), button => button.TextContent.Trim() == "Not now");
    }

    [Fact]
    public void Banner_ForABrokenTool_OffersDownloadAgain()
    {
        SignIn(administrator: true);
        _onGet = () => Tools(Tool("ffmpeg", "FFmpeg", state: ToolRequirementStates.Problem, canDownloadAgain: true));

        var cut = Render<ToolsAttentionBanner>();

        Assert.Contains("FFmpeg isn't working.", cut.Markup, StringComparison.Ordinal);
        Assert.Contains(cut.FindAll("button"), button => button.TextContent.Trim() == "Download again");
    }

    [Fact]
    public void Banner_NotNow_RecordsTheDecline_AndTheBannerStopsAsking()
    {
        SignIn(administrator: true);
        _onGet = () => Tools(Tool("ffmpeg", "FFmpeg", canInstall: true));
        _onDecline = _ => new(true, Tools(Tool("ffmpeg", "FFmpeg", canInstall: true, declined: true)));
        var cut = Render<ToolsAttentionBanner>();

        cut.FindAll("button").Single(button => button.TextContent.Trim() == "Not now").Click();

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-testid=tools-banner]")));
        Assert.Equal(["ffmpeg"], _declines);
    }

    [Fact]
    public void Banner_ForAToolTheAdministratorAlreadyDeclined_IsNotShown()
    {
        SignIn(administrator: true);
        _onGet = () => Tools(Tool("ffmpeg", "FFmpeg", canInstall: true, declined: true));

        var cut = Render<ToolsAttentionBanner>();

        Assert.Empty(cut.FindAll("[data-testid=tools-banner]"));
    }

    [Fact]
    public void Banner_ForSomeoneWhoIsNotAnAdministrator_IsNeverShown_AndTheEngineIsNotAsked()
    {
        SignIn(administrator: false);
        _onGet = () => Tools(Tool("ffmpeg", "FFmpeg", canInstall: true));

        var cut = Render<ToolsAttentionBanner>();

        Assert.Empty(cut.FindAll("[data-testid=tools-banner]"));
        Assert.Equal(0, Volatile.Read(ref _getCalls));
    }

    [Fact]
    public void Banner_IsNotShownForAnOptionalToolOrAReadyOne()
    {
        SignIn(administrator: true);
        _onGet = () => Tools(
            Tool("tesseract", "Tesseract", requirement: ToolRequirementLevels.Optional, canInstall: true),
            Tool("image_engine", "Image engine", state: ToolRequirementStates.Ready));

        var cut = Render<ToolsAttentionBanner>();

        Assert.Empty(cut.FindAll("[data-testid=tools-banner]"));
    }

    [Fact]
    public void Banner_IsNotShownOnTheToolsPageItself()
    {
        SignIn(administrator: true);
        _onGet = () => Tools(Tool("ffmpeg", "FFmpeg", canInstall: true));
        Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().NavigateTo("/settings/tools");

        var cut = Render<ToolsAttentionBanner>();

        Assert.Empty(cut.FindAll("[data-testid=tools-banner]"));
    }

    [Fact]
    public void Banner_InstallForMe_CallsTheEngine_AndShowsProgress()
    {
        SignIn(administrator: true);
        _onGet = () => Tools(Tool("ffmpeg", "FFmpeg", canInstall: true));
        _onInstall = _ => new(true, Tools(Tool("ffmpeg", "FFmpeg", state: ToolRequirementStates.Downloading, percent: 30)));
        var cut = Render<ToolsAttentionBanner>(parameters => parameters.Add(p => p.ProgressInterval, TimeSpan.FromHours(1)));

        cut.FindAll("button").Single(button => button.TextContent.Trim() == "Install for me").Click();

        cut.WaitForAssertion(() => Assert.Contains("Downloading FFmpeg.", cut.Markup, StringComparison.Ordinal));
        Assert.Equal(["ffmpeg"], _installs);
        Assert.Contains("30%", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain(cut.FindAll("button"), button => button.TextContent.Trim() == "Install for me");
    }

    [Fact]
    public void Banner_AFailedInstallRequest_ShowsThePlainReason()
    {
        SignIn(administrator: true);
        _onGet = () => Tools(Tool("ffmpeg", "FFmpeg", canInstall: true));
        _onInstall = _ => new(false, ErrorMessage: "That is already being downloaded.");
        var cut = Render<ToolsAttentionBanner>();

        cut.FindAll("button").Single(button => button.TextContent.Trim() == "Install for me").Click();

        cut.WaitForAssertion(() => Assert.Contains("That is already being downloaded.", cut.Markup, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Banner_DoesNotAskTheEngineAgainRightAway_WhenNothingIsDownloading()
    {
        SignIn(administrator: true);
        _onGet = () => Tools(Tool("ffmpeg", "FFmpeg", canInstall: true));

        Render<ToolsAttentionBanner>(parameters => parameters
            .Add(p => p.ProgressInterval, TimeSpan.FromMilliseconds(20))
            .Add(p => p.RefreshInterval, TimeSpan.FromHours(1)));
        await Task.Delay(300);

        Assert.Equal(1, Volatile.Read(ref _getCalls));
    }

    [Fact]
    public void MainLayout_ShowsTheBannerInTheAppShell()
    {
        var layout = File.ReadAllText(RepoFile("src", "MediaEngine.Web", "Shared", "MainLayout.razor"));

        Assert.Contains("<ToolsAttentionBanner />", layout, StringComparison.Ordinal);
    }

    // ── The Tools page ───────────────────────────────────────────────────────

    [Fact]
    public void ToolsPage_InstallForMe_CallsTheEngine_AndShowsProgressWithCancel()
    {
        _onGet = () => Tools(Tool("ffmpeg", "FFmpeg", canInstall: true));
        _onInstall = _ => new(true, Tools(Tool("ffmpeg", "FFmpeg", state: ToolRequirementStates.Downloading, percent: 30)));
        var cut = Render<ToolsRequirementsTab>(parameters => parameters.Add(p => p.PollInterval, TimeSpan.FromHours(1)));

        cut.FindAll("[data-tool-id=ffmpeg] button").Single(button => button.TextContent.Trim() == "Install for me").Click();

        cut.WaitForAssertion(() => Assert.Contains("Downloading 30%", cut.Find("[data-tool-id=ffmpeg]").TextContent, StringComparison.Ordinal));
        Assert.Equal(["ffmpeg"], _installs);
        Assert.Contains(cut.FindAll("[data-tool-id=ffmpeg] button"), button => button.TextContent.Trim() == "Cancel");
    }

    [Fact]
    public void ToolsPage_ForABrokenTool_OffersDownloadAgain_AndStartsTheSameInstall()
    {
        _onGet = () => Tools(Tool("ffmpeg", "FFmpeg", state: ToolRequirementStates.Problem, canDownloadAgain: true));
        _onInstall = _ => new(true, Tools(Tool("ffmpeg", "FFmpeg", state: ToolRequirementStates.Downloading, percent: 5)));
        var cut = Render<ToolsRequirementsTab>(parameters => parameters.Add(p => p.PollInterval, TimeSpan.FromHours(1)));

        cut.FindAll("[data-tool-id=ffmpeg] button").Single(button => button.TextContent.Trim() == "Download again").Click();

        cut.WaitForAssertion(() => Assert.Equal(["ffmpeg"], _installs));
    }

    [Fact]
    public void ToolsPage_Cancel_StopsTheDownload()
    {
        _onGet = () => Tools(Tool("ffmpeg", "FFmpeg", state: ToolRequirementStates.Downloading, percent: 40));
        var cut = Render<ToolsRequirementsTab>(parameters => parameters.Add(p => p.PollInterval, TimeSpan.FromHours(1)));

        cut.FindAll("[data-tool-id=ffmpeg] button").Single(button => button.TextContent.Trim() == "Cancel").Click();

        cut.WaitForAssertion(() => Assert.Equal(["ffmpeg"], _cancels));
    }

    [Fact]
    public void ToolsPage_ATesseractInstallerThatIsReady_OffersRunInstaller()
    {
        _onGet = () => Tools(Tool("tesseract", "Tesseract", requirement: ToolRequirementLevels.Optional, state: ToolRequirementStates.InstallerReady,
            fix: "Tesseract's installer is ready. Run it to finish."));
        var cut = Render<ToolsRequirementsTab>(parameters => parameters.Add(p => p.PollInterval, TimeSpan.FromHours(1)));

        Assert.Contains("Tesseract's installer is ready. Run it to finish.", cut.Find("[data-tool-id=tesseract]").TextContent, StringComparison.Ordinal);
        cut.FindAll("[data-tool-id=tesseract] button").Single(button => button.TextContent.Trim() == "Run installer").Click();

        cut.WaitForAssertion(() => Assert.Equal(["tesseract"], _runs));
    }

    [Fact]
    public void ToolsPage_ShowsNoInstallButton_WhereTheEngineDoesNotOfferOne()
    {
        _onGet = () => Tools(Tool("ffmpeg", "FFmpeg", canInstall: false));

        var cut = Render<ToolsRequirementsTab>();

        Assert.DoesNotContain(cut.FindAll("[data-tool-id=ffmpeg] button"), button => button.TextContent.Trim() == "Install for me");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private void SignIn(bool administrator)
    {
        var profile = Guid.NewGuid();
        _session.Set("token", Guid.NewGuid(), profile, Guid.NewGuid(),
            new DashboardAuthorityResponse(profile, profile, true, true, 1, 1, administrator, administrator, null, 1, [], [], []));
    }

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
        bool declined = false,
        int? percent = null,
        string fix = "Run winget install Gyan.FFmpeg, then press Check again.") =>
        new(id, name, "tool", requirement, state, FfmpegEffect, null,
            state == ToolRequirementStates.Ready ? string.Empty : fix,
            null, 0, false, canInstall, canDownloadAgain, percent, declined);

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
