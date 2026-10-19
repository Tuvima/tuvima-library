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
/// Install for me, Download again, Cancel and Run installer on the Tools page. The administrator banner that offers the
/// same install is covered by <see cref="SettingsAttentionBannerTests"/>.
/// </summary>
public sealed class ToolsInstallActionsTests : AsyncBunitContext
{
    private const string FfmpegEffect = "Videos, music and audiobooks wait until FFmpeg is available.";

    private readonly List<string> _installs = [];
    private readonly List<string> _cancels = [];
    private readonly List<string> _runs = [];
    private int _getCalls;
    private Func<ToolRequirementsDto?> _onGet = () => null;
    private Func<string, ToolActionResult> _onInstall = _ => new(false, ErrorMessage: "No.");
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
        string fix = "Run winget install Gyan.FFmpeg, then press Check again.") =>
        new(id, name, "tool", requirement, state, FfmpegEffect, null,
            state == ToolRequirementStates.Ready ? string.Empty : fix,
            null, 0, false, canInstall, canDownloadAgain, percent);

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
