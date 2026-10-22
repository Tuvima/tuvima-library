using MediaEngine.Api.Services.Tools;
using MediaEngine.Contracts.Settings;
using MediaEngine.Domain.Contracts;
using MediaEngine.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Api.Tests;

/// <summary>
/// A Required tool the Library can fetch (FFmpeg) is downloaded by itself when it is missing, at start-up and at first-run
/// setup, and so is ExifTool (Optional, marked <c>auto_install</c>); no other Optional tool is, and a person can turn it
/// off in <c>config/tool_downloads.json</c>.
/// </summary>
public sealed class ToolAutoInstallerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tuvima-tool-auto-install", Guid.NewGuid().ToString("N"));
    private readonly SpyInstaller _installer = new();
    private readonly FixedTools _tools = new(Tools(Tool(ToolRequirementIds.Ffmpeg, ToolRequirementStates.Downloading, canInstall: false)));

    public ToolAutoInstallerTests() => Directory.CreateDirectory(Path.Combine(_root, "config"));

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task AMissingRequiredToolThatIsOffered_StartsItsDownload_AndReturnsTheListShowingItDownloading()
    {
        var missing = Tools(Tool(ToolRequirementIds.Ffmpeg, ToolRequirementStates.Missing, canInstall: true));

        var result = await Service().StartMissingRequiredAsync(missing, CancellationToken.None);

        Assert.Equal([ToolRequirementIds.Ffmpeg], _installer.Started);
        Assert.Equal(ToolRequirementStates.Downloading, Assert.Single(result.Tools).State);
    }

    [Fact]
    public async Task AToolThatIsOptional_IsNeverStartedByItself()
    {
        var missing = Tools(Tool(ToolRequirementIds.AdaptiveStreaming, ToolRequirementStates.Missing, canInstall: true, requirement: ToolRequirementLevels.Optional));

        var result = await Service().StartMissingRequiredAsync(missing, CancellationToken.None);

        Assert.Empty(_installer.Started);
        Assert.Same(missing, result);
    }

    [Fact]
    public async Task AMissingTesseractThatIsOffered_IsDownloadedByItself_EvenThoughItIsOptional()
    {
        var missing = Tools(Tool(ToolRequirementIds.Tesseract, ToolRequirementStates.Missing, canInstall: true, requirement: ToolRequirementLevels.Optional));

        await Service().StartMissingRequiredAsync(missing, CancellationToken.None);

        Assert.Equal([ToolRequirementIds.Tesseract], _installer.Started);
    }

    [Fact]
    public async Task TesseractWithAutoInstallTurnedOffForIt_IsLeftForThePersonToStart()
    {
        File.WriteAllText(Path.Combine(_root, "config", "tool_downloads.json"), "{ \"tesseract\": { \"auto_install\": false } }");
        var missing = Tools(Tool(ToolRequirementIds.Tesseract, ToolRequirementStates.Missing, canInstall: true, requirement: ToolRequirementLevels.Optional));

        var result = await Service().StartMissingRequiredAsync(missing, CancellationToken.None);

        Assert.Empty(_installer.Started);
        Assert.Same(missing, result);
    }

    [Fact]
    public async Task AMissingExifToolThatIsOffered_IsStartedByItself_EvenThoughItIsOptional()
    {
        var missing = Tools(
            Tool(ToolRequirementIds.Ffmpeg, ToolRequirementStates.Ready, canInstall: false),
            Tool(ToolRequirementIds.ExifTool, ToolRequirementStates.Missing, canInstall: true, requirement: ToolRequirementLevels.Optional));

        await Service().StartMissingRequiredAsync(missing, CancellationToken.None);

        Assert.Equal([ToolRequirementIds.ExifTool], _installer.Started);
    }

    [Fact]
    public async Task AMissingExifToolAndAMissingTesseractAreBothStarted()
    {
        var missing = Tools(
            Tool(ToolRequirementIds.Tesseract, ToolRequirementStates.Missing, canInstall: true, requirement: ToolRequirementLevels.Optional),
            Tool(ToolRequirementIds.ExifTool, ToolRequirementStates.Missing, canInstall: true, requirement: ToolRequirementLevels.Optional));

        await Service().StartMissingRequiredAsync(missing, CancellationToken.None);

        Assert.Equal([ToolRequirementIds.Tesseract, ToolRequirementIds.ExifTool], _installer.Started);
    }

    [Fact]
    public async Task ExifToolWithAutoInstallTurnedOffForIt_IsLeftForThePersonToStart()
    {
        File.WriteAllText(Path.Combine(_root, "config", "tool_downloads.json"), "{ \"exiftool\": { \"auto_install\": false } }");
        var missing = Tools(Tool(ToolRequirementIds.ExifTool, ToolRequirementStates.Missing, canInstall: true, requirement: ToolRequirementLevels.Optional));

        var result = await Service().StartMissingRequiredAsync(missing, CancellationToken.None);

        Assert.Empty(_installer.Started);
        Assert.Same(missing, result);
    }

    [Fact]
    public async Task ExifToolThatIsNotOfferedHere_OrIsAlreadyReady_IsNotStarted()
    {
        var notOffered = Tools(Tool(ToolRequirementIds.ExifTool, ToolRequirementStates.Missing, canInstall: false, requirement: ToolRequirementLevels.Optional));
        var ready = Tools(Tool(ToolRequirementIds.ExifTool, ToolRequirementStates.Ready, canInstall: true, requirement: ToolRequirementLevels.Optional));

        await Service().StartMissingRequiredAsync(notOffered, CancellationToken.None);
        await Service().StartMissingRequiredAsync(ready, CancellationToken.None);

        Assert.Empty(_installer.Started);
    }

    [Fact]
    public async Task ARequiredToolTheLibraryCannotFetchHere_IsLeftForThePersonToFix()
    {
        var missing = Tools(Tool(ToolRequirementIds.Ffmpeg, ToolRequirementStates.Missing, canInstall: false));

        await Service().StartMissingRequiredAsync(missing, CancellationToken.None);

        Assert.Empty(_installer.Started);
    }

    [Theory]
    [InlineData(ToolRequirementStates.Ready)]
    [InlineData(ToolRequirementStates.Downloading)]
    [InlineData(ToolRequirementStates.Problem)]
    public async Task ARequiredToolThatIsNotMissing_IsNotStartedAgain(string state)
    {
        var tools = Tools(Tool(ToolRequirementIds.Ffmpeg, state, canInstall: true));

        await Service().StartMissingRequiredAsync(tools, CancellationToken.None);

        Assert.Empty(_installer.Started);
    }

    [Fact]
    public async Task WhenAutoInstallIsTurnedOffInTheDownloadSettings_NothingIsStarted()
    {
        File.WriteAllText(Path.Combine(_root, "config", "tool_downloads.json"), "{ \"auto_install_required\": false }");
        var missing = Tools(Tool(ToolRequirementIds.Ffmpeg, ToolRequirementStates.Missing, canInstall: true));

        var result = await Service().StartMissingRequiredAsync(missing, CancellationToken.None);

        Assert.Empty(_installer.Started);
        Assert.Same(missing, result);
    }

    [Fact]
    public async Task ADownloadAlreadyRunning_IsNotStartedTwice_ButTheFreshListIsReturned()
    {
        _installer.Outcome = ToolInstallOutcome.AlreadyRunning;
        var missing = Tools(Tool(ToolRequirementIds.Ffmpeg, ToolRequirementStates.Missing, canInstall: true));

        var result = await Service().StartMissingRequiredAsync(missing, CancellationToken.None);

        Assert.Equal(ToolRequirementStates.Downloading, Assert.Single(result.Tools).State);
    }

    [Fact]
    public async Task ADownloadThatIsNotOffered_LeavesTheListAsItWas()
    {
        _installer.Outcome = ToolInstallOutcome.NotOffered;
        var missing = Tools(Tool(ToolRequirementIds.Ffmpeg, ToolRequirementStates.Missing, canInstall: true));

        var result = await Service().StartMissingRequiredAsync(missing, CancellationToken.None);

        Assert.Same(missing, result);
    }

    // ── The start-up check ───────────────────────────────────────────────────

    [Fact]
    public async Task StartUp_WithARequiredToolMissing_StartsItsDownloadOnce()
    {
        var missing = Tools(Tool(ToolRequirementIds.Ffmpeg, ToolRequirementStates.Missing, canInstall: true));
        var auto = new SpyAutoInstaller();
        using var startup = new ToolRequirementsStartupService(new FixedTools(missing), auto, new NullNotificationPublisher(), NullLogger<ToolRequirementsStartupService>.Instance);

        await startup.StartAsync(CancellationToken.None);
        await startup.ExecuteTask!;

        Assert.Equal(1, auto.Calls);
    }

    [Fact]
    public async Task StartUp_WithEveryRequiredToolReady_StartsNoDownload()
    {
        var ready = Tools(Tool(ToolRequirementIds.Ffmpeg, ToolRequirementStates.Ready, canInstall: false));
        var auto = new SpyAutoInstaller();
        using var startup = new ToolRequirementsStartupService(new FixedTools(ready), auto, new NullNotificationPublisher(), NullLogger<ToolRequirementsStartupService>.Instance);

        await startup.StartAsync(CancellationToken.None);
        await startup.ExecuteTask!;

        Assert.Equal(0, auto.Calls);
    }

    [Fact]
    public async Task StartUp_WithEveryRequiredToolReadyButExifToolMissing_StillGivesTheAutoInstallerALook()
    {
        var tools = Tools(
            Tool(ToolRequirementIds.Ffmpeg, ToolRequirementStates.Ready, canInstall: false),
            Tool(ToolRequirementIds.ExifTool, ToolRequirementStates.Missing, canInstall: true, requirement: ToolRequirementLevels.Optional));
        var auto = new SpyAutoInstaller();
        using var startup = new ToolRequirementsStartupService(new FixedTools(tools), auto, new NullNotificationPublisher(), NullLogger<ToolRequirementsStartupService>.Instance);

        await startup.StartAsync(CancellationToken.None);
        await startup.ExecuteTask!;

        Assert.Equal(1, auto.Calls);
    }

    private ToolAutoInstaller Service() =>
        new(_installer, _tools, new ConfigurationDirectoryLoader(Path.Combine(_root, "config")), NullLogger<ToolAutoInstaller>.Instance);

    private static ToolRequirementsDto Tools(params ToolRequirementDto[] rows) => new(
        ToolInstallKinds.Source,
        DateTimeOffset.UnixEpoch,
        rows.Any(row => row.Requirement == ToolRequirementLevels.Required && row.State != ToolRequirementStates.Ready),
        rows);

    private static ToolRequirementDto Tool(
        string id, string state, bool canInstall, string requirement = ToolRequirementLevels.Required) =>
        new(id, id == ToolRequirementIds.Ffmpeg ? "FFmpeg" : id == ToolRequirementIds.ExifTool ? "ExifTool" : "Tesseract", "tool", requirement, state,
            "Videos, music and audiobooks wait until FFmpeg is available.", null, "Install it.",
            null, 0, false, canInstall, false, state == ToolRequirementStates.Downloading ? 10 : null);

    private sealed class FixedTools(ToolRequirementsDto result) : IToolRequirementsService
    {
        public Task<ToolRequirementsDto> GetAsync(CancellationToken ct) => Task.FromResult(result);
        public Task<ToolRequirementsDto> CheckAsync(CancellationToken ct) => Task.FromResult(result);
    }

    private sealed class SpyAutoInstaller : IToolAutoInstaller
    {
        public int Calls { get; private set; }

        public Task<ToolRequirementsDto> StartMissingRequiredAsync(ToolRequirementsDto tools, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(tools);
        }
    }

    private sealed class SpyInstaller : IToolInstallerService
    {
        public List<string> Started { get; } = [];

        public ToolInstallOutcome Outcome { get; set; } = ToolInstallOutcome.Accepted;

        public Task<ToolInstallResult> StartAsync(string toolId, CancellationToken ct)
        {
            if (Outcome == ToolInstallOutcome.Accepted)
            {
                Started.Add(toolId);
            }

            return Task.FromResult(new ToolInstallResult(Outcome));
        }

        public ToolInstallOffer GetOffer(string toolId, string installKind) => ToolInstallOffer.None;
        public ToolInstallSnapshot GetSnapshot(string toolId) => ToolInstallSnapshot.Idle;
        public Task CancelAsync(string toolId, CancellationToken ct) => Task.CompletedTask;
        public Task<ToolInstallResult> RunInstallerAsync(string toolId, CancellationToken ct) =>
            Task.FromResult(new ToolInstallResult(ToolInstallOutcome.NotOffered));
    }
}
