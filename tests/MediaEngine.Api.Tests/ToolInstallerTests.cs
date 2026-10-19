using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MediaEngine.Api.Endpoints;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services;
using MediaEngine.Api.Services.LocalAssets.Ocr;
using MediaEngine.Api.Services.Settings;
using MediaEngine.Api.Services.Tools;
using MediaEngine.Contracts.Settings;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Models;
using MediaEngine.Domain.Services;
using MediaEngine.Storage;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Api.Tests;

/// <summary>
/// Fetching FFmpeg and Tesseract on request: a fake download source stands in for the network, so no test downloads
/// anything. A correct archive installs; a wrong checksum installs nothing; a failed download leaves files waiting;
/// every attempt is written to the activity log.
/// </summary>
public sealed class ToolInstallerTests : IDisposable
{
    private const string FfmpegUrl = "https://github.com/test/ffmpeg-win64.zip";
    private const string InstallerUrl = "https://github.com/test/tesseract-setup.exe";
    private const string EnglishUrl = "https://github.com/test/eng.traineddata";
    private const string OrientationUrl = "https://github.com/test/osd.traineddata";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "tuvima-tool-installer-tests", Guid.NewGuid().ToString("N"));
    private readonly string _dataRoot;
    private readonly string _ocrData;
    private readonly FakeSource _source = new();
    private readonly RecordingActivity _activity = new();
    private readonly FakeLauncher _launcher = new();
    private readonly FakePrompts _prompts = new();
    private readonly FolderFfmpeg _ffmpeg;
    private readonly FolderOcr _ocr;
    private readonly ConfigurationDirectoryLoader _configuration;
    private ToolInstallerService _installer = null!;
    private ToolRequirementsService _tools = null!;
    private ToolDownloadSettings _settings = null!;

    public ToolInstallerTests()
    {
        _dataRoot = Path.Combine(_root, "data");
        _ocrData = Path.Combine(_root, "ocr", "tessdata");
        Directory.CreateDirectory(_dataRoot);
        _configuration = new ConfigurationDirectoryLoader(Path.Combine(_root, "config"));
        _ffmpeg = new FolderFfmpeg(Path.Combine(_dataRoot, "tools", "ffmpeg"));
        _ocr = new FolderOcr(_ocrData, Path.Combine(_dataRoot, "tools", "tesseract"));
        Build();
    }

    // ── FFmpeg ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task ACorrectArchive_InstallsIntoTheDataFolder_AndFfmpegBecomesReadyAfterTheCheck()
    {
        var archive = FfmpegArchive();
        UseFfmpeg(archive);
        Assert.Equal(ToolRequirementStates.Missing, (await Row(ToolRequirementIds.Ffmpeg)).State);

        var started = await _installer.StartAsync(ToolRequirementIds.Ffmpeg, CancellationToken.None);
        await WaitUntilIdleAsync(ToolRequirementIds.Ffmpeg);

        Assert.Equal(ToolInstallOutcome.Accepted, started.Outcome);
        var folder = Path.Combine(_dataRoot, "tools", "ffmpeg");
        Assert.Equal("fake ffmpeg", File.ReadAllText(Path.Combine(folder, "ffmpeg.exe")));
        Assert.Equal("fake ffprobe", File.ReadAllText(Path.Combine(folder, "ffprobe.exe")));
        Assert.True(File.Exists(Path.Combine(folder, "LICENSE.txt")));
        var row = await Row(ToolRequirementIds.Ffmpeg);
        Assert.Equal(ToolRequirementStates.Ready, row.State);
        Assert.False(row.CanInstall);
        Assert.False(Directory.Exists(Path.Combine(_dataRoot, "tools", ".downloads", "ffmpeg")));
        Assert.Equal(
            [SystemActionType.ToolDownloadStarted, SystemActionType.ToolDownloadFinished],
            _activity.ActionTypes);
    }

    [Fact]
    public async Task AWrongArchiveChecksum_InstallsNothing_SaysSoPlainly_AndLogsTheRefusal()
    {
        var archive = FfmpegArchive();
        UseFfmpeg(archive, archiveSha256: new string('0', 64));

        await _installer.StartAsync(ToolRequirementIds.Ffmpeg, CancellationToken.None);
        await WaitUntilIdleAsync(ToolRequirementIds.Ffmpeg);

        Assert.False(Directory.Exists(Path.Combine(_dataRoot, "tools", "ffmpeg")));
        var row = await Row(ToolRequirementIds.Ffmpeg);
        Assert.Equal(ToolRequirementStates.Missing, row.State);
        Assert.Contains("didn't match", row.StateReason, StringComparison.Ordinal);
        Assert.True(row.CanInstall);
        Assert.Contains(SystemActionType.ToolDownloadChecksumRefused, _activity.ActionTypes);
        Assert.DoesNotContain(SystemActionType.ToolDownloadFinished, _activity.ActionTypes);
        Assert.False(Directory.Exists(Path.Combine(_dataRoot, "tools", ".downloads", "ffmpeg")));
    }

    [Fact]
    public async Task AWrongProgramChecksum_InstallsNothing_AndLeavesTheOldCopyAlone()
    {
        var folder = Path.Combine(_dataRoot, "tools", "ffmpeg");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "old-marker.txt"), "the previous copy");
        var archive = FfmpegArchive();
        UseFfmpeg(archive, ffmpegSha256: new string('1', 64));

        await _installer.StartAsync(ToolRequirementIds.Ffmpeg, CancellationToken.None);
        await WaitUntilIdleAsync(ToolRequirementIds.Ffmpeg);

        Assert.Equal("the previous copy", File.ReadAllText(Path.Combine(folder, "old-marker.txt")));
        Assert.False(File.Exists(Path.Combine(folder, "ffmpeg.exe")));
        Assert.Contains(SystemActionType.ToolDownloadChecksumRefused, _activity.ActionTypes);
    }

    [Fact]
    public async Task AReplacementThatSucceeds_ReplacesTheOldCopy()
    {
        var folder = Path.Combine(_dataRoot, "tools", "ffmpeg");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "old-marker.txt"), "the previous copy");
        UseFfmpeg(FfmpegArchive());

        await _installer.StartAsync(ToolRequirementIds.Ffmpeg, CancellationToken.None);
        await WaitUntilIdleAsync(ToolRequirementIds.Ffmpeg);

        Assert.False(File.Exists(Path.Combine(folder, "old-marker.txt")));
        Assert.True(File.Exists(Path.Combine(folder, "ffmpeg.exe")));
    }

    [Fact]
    public async Task ArchiveEntriesThatClimbOutOfTheArchive_AreIgnored()
    {
        // The only copies of the programs sit under paths that climb out, so nothing may be placed from them.
        var archive = Zip(
            ("../../outside/ffmpeg.exe", "evil ffmpeg"),
            ("..\\..\\outside\\ffprobe.exe", "evil ffprobe"),
            ("/rooted/ffmpeg.exe", "evil"),
            ("C:/drive/ffprobe.exe", "evil"));
        UseFfmpeg(archive, ffmpegContent: "evil ffmpeg", ffprobeContent: "evil ffprobe");

        await _installer.StartAsync(ToolRequirementIds.Ffmpeg, CancellationToken.None);
        await WaitUntilIdleAsync(ToolRequirementIds.Ffmpeg);

        Assert.False(Directory.Exists(Path.Combine(_dataRoot, "tools", "ffmpeg")));
        Assert.False(Directory.Exists(Path.Combine(_root, "outside")));
        Assert.False(Directory.Exists(Path.Combine(_dataRoot, "outside")));
        Assert.Equal(ToolRequirementStates.Missing, (await Row(ToolRequirementIds.Ffmpeg)).State);
        Assert.Contains(SystemActionType.ToolDownloadFailed, _activity.ActionTypes);
    }

    [Fact]
    public async Task AnArchiveWithTraversalEntriesAlongsideTheRealOnes_StillInstallsOnlyTheRealOnes()
    {
        var archive = Zip(
            ("../ffmpeg.exe", "evil ffmpeg"),
            ("ffmpeg-9/bin/ffmpeg.exe", "fake ffmpeg"),
            ("ffmpeg-9/bin/ffprobe.exe", "fake ffprobe"),
            ("ffmpeg-9/LICENSE.txt", "GPL"));
        UseFfmpeg(archive);

        await _installer.StartAsync(ToolRequirementIds.Ffmpeg, CancellationToken.None);
        await WaitUntilIdleAsync(ToolRequirementIds.Ffmpeg);

        Assert.Equal("fake ffmpeg", File.ReadAllText(Path.Combine(_dataRoot, "tools", "ffmpeg", "ffmpeg.exe")));
        Assert.Equal(ToolRequirementStates.Ready, (await Row(ToolRequirementIds.Ffmpeg)).State);
    }

    [Fact]
    public async Task AFailedOfflineDownload_LeavesFilesWaiting_WithAPlainReason_AndNoException()
    {
        UseFfmpeg(FfmpegArchive());
        _source.Offline = true;

        var started = await _installer.StartAsync(ToolRequirementIds.Ffmpeg, CancellationToken.None);
        await WaitUntilIdleAsync(ToolRequirementIds.Ffmpeg);

        Assert.Equal(ToolInstallOutcome.Accepted, started.Outcome);
        var row = await Row(ToolRequirementIds.Ffmpeg);
        Assert.Equal(ToolRequirementStates.Missing, row.State);
        Assert.Equal("The download didn't finish. Check the connection and try again.", row.StateReason);
        Assert.True(row.CanInstall);
        Assert.Contains(SystemActionType.ToolDownloadFailed, _activity.ActionTypes);
        Assert.DoesNotContain(SystemActionType.ToolDownloadFinished, _activity.ActionTypes);
        Assert.False(Directory.Exists(Path.Combine(_dataRoot, "tools", "ffmpeg")));
    }

    [Fact]
    public async Task ADownloadInProgress_ShowsAsDownloadingWithProgress_AndCancelCleansUpAndLogsIt()
    {
        UseFfmpeg(FfmpegArchive());
        _source.HoldAfterFirstChunk = new TaskCompletionSource();

        await _installer.StartAsync(ToolRequirementIds.Ffmpeg, CancellationToken.None);
        await WaitUntilAsync(() => _source.Held);
        var downloading = await Row(ToolRequirementIds.Ffmpeg);

        Assert.Equal(ToolRequirementStates.Downloading, downloading.State);
        Assert.NotNull(downloading.DownloadPercent);
        Assert.False(downloading.CanInstall);
        Assert.False(downloading.CanDownloadAgain);
        Assert.True(Directory.Exists(Path.Combine(_dataRoot, "tools", ".downloads", "ffmpeg")));

        await _installer.CancelAsync(ToolRequirementIds.Ffmpeg, CancellationToken.None);

        Assert.Equal(ToolInstallPhase.Idle, _installer.GetSnapshot(ToolRequirementIds.Ffmpeg).Phase);
        Assert.False(Directory.Exists(Path.Combine(_dataRoot, "tools", ".downloads", "ffmpeg")));
        Assert.False(Directory.Exists(Path.Combine(_dataRoot, "tools", "ffmpeg")));
        Assert.Contains(SystemActionType.ToolDownloadCancelled, _activity.ActionTypes);
        Assert.Equal(ToolRequirementStates.Missing, (await Row(ToolRequirementIds.Ffmpeg)).State);
    }

    [Fact]
    public async Task ASecondInstallWhileOneIsRunning_IsRefusedAsAlreadyRunning()
    {
        UseFfmpeg(FfmpegArchive());
        _source.HoldAfterFirstChunk = new TaskCompletionSource();
        await _installer.StartAsync(ToolRequirementIds.Ffmpeg, CancellationToken.None);
        await WaitUntilAsync(() => _source.Held);

        var second = await _installer.StartAsync(ToolRequirementIds.Ffmpeg, CancellationToken.None);

        Assert.Equal(ToolInstallOutcome.AlreadyRunning, second.Outcome);
        await _installer.CancelAsync(ToolRequirementIds.Ffmpeg, CancellationToken.None);
    }

    [Fact]
    public async Task APinnedAddressOutsideGithub_IsNeverRequested()
    {
        // A hand-edited file is dropped by the loader, so nothing is offered; the service also refuses on its own.
        var archive = FfmpegArchive();
        UseFfmpeg(archive, url: "https://example.com/ffmpeg.zip");

        var offer = _installer.GetOffer(ToolRequirementIds.Ffmpeg, ToolInstallKinds.Source);
        var started = await _installer.StartAsync(ToolRequirementIds.Ffmpeg, CancellationToken.None);

        Assert.False(offer.Offered);
        Assert.Equal(ToolInstallOutcome.NotOffered, started.Outcome);
        Assert.Empty(_source.Requests);
    }

    // ── Who is offered the install ───────────────────────────────────────────

    [Theory]
    [InlineData("source", true, true, "ffmpeg", true)]
    [InlineData("windows_installer", true, true, "ffmpeg", true)]
    [InlineData("docker", true, true, "ffmpeg", false)]
    [InlineData("source", false, true, "ffmpeg", false)]
    [InlineData("source", true, true, "tesseract", true)]
    [InlineData("source", true, false, "tesseract", false)]
    [InlineData("windows_installer", true, true, "tesseract", false)]
    [InlineData("docker", true, true, "tesseract", false)]
    [InlineData("source", false, true, "tesseract", false)]
    [InlineData("source", true, true, "image_engine", false)]
    [InlineData("source", true, true, "adaptive_streaming", false)]
    public void TheInstallIsOfferedOnlyWhereItCanWork(string installKind, bool windowsX64, bool interactive, string toolId, bool expected)
    {
        Build(new ToolInstallPlatform(windowsX64, interactive), installKind);

        Assert.Equal(expected, _installer.GetOffer(toolId, installKind).Offered);
    }

    [Fact]
    public async Task AMissingFfmpegRow_OffersInstallForMe_AndABrokenOneOffersDownloadAgain()
    {
        var missing = await Row(ToolRequirementIds.Ffmpeg);
        Assert.True(missing.CanInstall);
        Assert.False(missing.CanDownloadAgain);

        var folder = Path.Combine(_dataRoot, "tools", "ffmpeg");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "ffmpeg.exe"), "x");
        File.WriteAllText(Path.Combine(folder, "ffprobe.exe"), "x");
        _ffmpeg.VersionExitCode = 1;
        var broken = (await _tools.CheckAsync(CancellationToken.None)).Tools.Single(tool => tool.Id == ToolRequirementIds.Ffmpeg);

        Assert.Equal(ToolRequirementStates.Problem, broken.State);
        Assert.False(broken.CanInstall);
        Assert.True(broken.CanDownloadAgain);
    }

    [Fact]
    public async Task InADockerContainer_NoRowOffersAnInstall()
    {
        Build(new ToolInstallPlatform(true, true), ToolInstallKinds.Docker);

        var rows = (await _tools.GetAsync(CancellationToken.None)).Tools;

        Assert.All(rows, row =>
        {
            Assert.False(row.CanInstall);
            Assert.False(row.CanDownloadAgain && row.Kind == "tool");
        });
    }

    [Fact]
    public async Task TesseractWhenTheEngineIsNotInteractive_SaysToReinstallWithTheWindowsInstaller()
    {
        Build(new ToolInstallPlatform(true, false), ToolInstallKinds.Source);

        var row = await Row(ToolRequirementIds.Tesseract);

        Assert.False(row.CanInstall);
        Assert.Equal("Reinstall the Library with the Windows installer; it includes Tesseract.", row.Fix);
    }

    // ── Tesseract ────────────────────────────────────────────────────────────

    [Fact]
    public async Task ATesseractDownload_LeavesTheInstallerReady_AndTheLanguageFilesInPlace()
    {
        UseTesseract();

        await _installer.StartAsync(ToolRequirementIds.Tesseract, CancellationToken.None);
        await WaitUntilIdleAsync(ToolRequirementIds.Tesseract);

        var row = await Row(ToolRequirementIds.Tesseract);
        Assert.Equal(ToolRequirementStates.InstallerReady, row.State);
        Assert.Equal("Tesseract's installer is ready. Run it to finish.", row.Fix);
        Assert.True(File.Exists(Path.Combine(_dataRoot, "tools", "installers", "tesseract-setup.exe")));
        Assert.Equal("english data", File.ReadAllText(Path.Combine(_ocrData, "eng.traineddata")));
        Assert.Equal("orientation data", File.ReadAllText(Path.Combine(_ocrData, "osd.traineddata")));
        Assert.Equal(
            [SystemActionType.ToolDownloadStarted, SystemActionType.ToolDownloadFinished],
            _activity.ActionTypes);
        Assert.Equal(0, _launcher.Runs);
    }

    [Fact]
    public async Task LanguageFilesAlreadyPresentWithTheRightChecksum_AreNotDownloadedAgain()
    {
        UseTesseract();
        Directory.CreateDirectory(_ocrData);
        File.WriteAllText(Path.Combine(_ocrData, "eng.traineddata"), "english data");

        await _installer.StartAsync(ToolRequirementIds.Tesseract, CancellationToken.None);
        await WaitUntilIdleAsync(ToolRequirementIds.Tesseract);

        Assert.DoesNotContain(EnglishUrl, _source.Requests);
        Assert.Contains(OrientationUrl, _source.Requests);
    }

    [Fact]
    public async Task ATesseractInstallerWithTheWrongChecksum_IsRefused_AndNeverOffered()
    {
        UseTesseract(installerSha256: new string('2', 64));

        await _installer.StartAsync(ToolRequirementIds.Tesseract, CancellationToken.None);
        await WaitUntilIdleAsync(ToolRequirementIds.Tesseract);

        Assert.False(File.Exists(Path.Combine(_dataRoot, "tools", "installers", "tesseract-setup.exe")));
        Assert.Equal(ToolRequirementStates.Missing, (await Row(ToolRequirementIds.Tesseract)).State);
        Assert.Contains(SystemActionType.ToolDownloadChecksumRefused, _activity.ActionTypes);
    }

    [Fact]
    public async Task RunInstaller_StartsTheDownloadedInstaller_ThenTheCheckMakesTesseractReadyAndTheInstallerIsRemoved()
    {
        UseTesseract();
        await _installer.StartAsync(ToolRequirementIds.Tesseract, CancellationToken.None);
        await WaitUntilIdleAsync(ToolRequirementIds.Tesseract);
        _launcher.OnRun = () => _ocr.Install();

        var result = await _installer.RunInstallerAsync(ToolRequirementIds.Tesseract, CancellationToken.None);
        await WaitUntilAsync(() => !_installer.GetSnapshot(ToolRequirementIds.Tesseract).InstallerRunning
            && _installer.GetSnapshot(ToolRequirementIds.Tesseract).Phase == ToolInstallPhase.Idle);

        Assert.Equal(ToolInstallOutcome.Accepted, result.Outcome);
        Assert.Equal(1, _launcher.Runs);
        Assert.EndsWith("tesseract-setup.exe", _launcher.LastPath, StringComparison.Ordinal);
        Assert.Equal(ToolRequirementStates.Ready, (await Row(ToolRequirementIds.Tesseract)).State);
        Assert.False(File.Exists(Path.Combine(_dataRoot, "tools", "installers", "tesseract-setup.exe")));
    }

    [Fact]
    public async Task RunInstaller_IsNotOffered_WhenTheEngineIsNotInteractive_AndNothingIsStarted()
    {
        UseTesseract();
        await _installer.StartAsync(ToolRequirementIds.Tesseract, CancellationToken.None);
        await WaitUntilIdleAsync(ToolRequirementIds.Tesseract);
        Build(new ToolInstallPlatform(true, false), ToolInstallKinds.Source);

        var result = await _installer.RunInstallerAsync(ToolRequirementIds.Tesseract, CancellationToken.None);

        Assert.Equal(ToolInstallOutcome.NotOffered, result.Outcome);
        Assert.Equal("Reinstall the Library with the Windows installer; it includes Tesseract.", result.Message);
        Assert.Equal(0, _launcher.Runs);
    }

    [Fact]
    public async Task RunInstaller_WithNothingDownloaded_IsNotOffered()
    {
        var result = await _installer.RunInstallerAsync(ToolRequirementIds.Tesseract, CancellationToken.None);

        Assert.Equal(ToolInstallOutcome.NotOffered, result.Outcome);
        Assert.Equal(0, _launcher.Runs);
    }

    [Fact]
    public async Task RunInstaller_ForAnInstallerThatChangedOnDisk_RefusesToRunIt()
    {
        UseTesseract();
        await _installer.StartAsync(ToolRequirementIds.Tesseract, CancellationToken.None);
        await WaitUntilIdleAsync(ToolRequirementIds.Tesseract);
        File.WriteAllText(Path.Combine(_dataRoot, "tools", "installers", "tesseract-setup.exe"), "tampered");

        var result = await _installer.RunInstallerAsync(ToolRequirementIds.Tesseract, CancellationToken.None);

        Assert.Equal(ToolInstallOutcome.NotOffered, result.Outcome);
        Assert.Equal(0, _launcher.Runs);
        Assert.False(File.Exists(Path.Combine(_dataRoot, "tools", "installers", "tesseract-setup.exe")));
    }

    // ── The routes ───────────────────────────────────────────────────────────

    [Fact]
    public async Task EveryToolsRoute_IsAdministratorOnly()
    {
        await using var app = BuildApplication();

        var endpoints = Endpoints(app).ToList();

        foreach (var endpoint in endpoints)
        {
            var policy = Assert.Single(endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>(), data => !string.IsNullOrEmpty(data.Policy));
            Assert.Equal(AuthPolicies.Administrator, policy.Policy);
        }

        foreach (var route in new[] { "/settings/tools/{id}/install", "/settings/tools/{id}/run-installer", "/settings/tools/{id}/decline" })
        {
            Assert.Contains(endpoints, endpoint => endpoint.RoutePattern.RawText == route);
        }

        Assert.Contains(endpoints, endpoint => endpoint.RoutePattern.RawText == "/settings/tools/{id}/install"
            && endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods.Contains("DELETE") == true);
    }

    [Fact]
    public async Task PostInstall_StartsTheDownload_Returns202WithTheToolsList()
    {
        UseFfmpeg(FfmpegArchive());
        _source.HoldAfterFirstChunk = new TaskCompletionSource();
        await using var app = BuildApplication();

        var (status, body) = await SendAsync(app, "POST", "ffmpeg", "install");
        await WaitUntilAsync(() => _source.Held);

        Assert.Equal(StatusCodes.Status202Accepted, status);
        Assert.Contains("\"id\":\"ffmpeg\"", body, StringComparison.Ordinal);
        await _installer.CancelAsync(ToolRequirementIds.Ffmpeg, CancellationToken.None);
    }

    [Fact]
    public async Task PostInstall_WhileOneIsRunning_Returns409()
    {
        UseFfmpeg(FfmpegArchive());
        _source.HoldAfterFirstChunk = new TaskCompletionSource();
        await using var app = BuildApplication();
        await SendAsync(app, "POST", "ffmpeg", "install");
        await WaitUntilAsync(() => _source.Held);

        var (status, _) = await SendAsync(app, "POST", "ffmpeg", "install");

        Assert.Equal(StatusCodes.Status409Conflict, status);
        await _installer.CancelAsync(ToolRequirementIds.Ffmpeg, CancellationToken.None);
    }

    [Fact]
    public async Task PostInstall_ForAToolThatIsNotInstalledHere_Returns400WithAPlainMessage()
    {
        await using var app = BuildApplication();

        var (status, body) = await SendAsync(app, "POST", "image_engine", "install");

        Assert.Equal(StatusCodes.Status400BadRequest, status);
        Assert.Equal(
            "The Library can't install this one for you here. Follow the steps shown for it instead.",
            JsonDocument.Parse(body).RootElement.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task EveryNewRoute_ForAnUnknownTool_Returns404()
    {
        await using var app = BuildApplication();

        Assert.Equal(StatusCodes.Status404NotFound, (await SendAsync(app, "POST", "nonsense", "install")).Status);
        Assert.Equal(StatusCodes.Status404NotFound, (await SendAsync(app, "DELETE", "nonsense", "install")).Status);
        Assert.Equal(StatusCodes.Status404NotFound, (await SendAsync(app, "POST", "nonsense", "run-installer")).Status);
        Assert.Equal(StatusCodes.Status404NotFound, (await SendAsync(app, "POST", "nonsense", "decline")).Status);
    }

    [Fact]
    public async Task DeleteInstall_CancelsTheDownload()
    {
        UseFfmpeg(FfmpegArchive());
        _source.HoldAfterFirstChunk = new TaskCompletionSource();
        await using var app = BuildApplication();
        await SendAsync(app, "POST", "ffmpeg", "install");
        await WaitUntilAsync(() => _source.Held);

        var (status, _) = await SendAsync(app, "DELETE", "ffmpeg", "install");

        Assert.Equal(StatusCodes.Status200OK, status);
        Assert.Equal(ToolInstallPhase.Idle, _installer.GetSnapshot(ToolRequirementIds.Ffmpeg).Phase);
        Assert.Contains(SystemActionType.ToolDownloadCancelled, _activity.ActionTypes);
    }

    [Fact]
    public async Task PostDecline_IsRemembered_AndShowsOnTheRow_UntilTheToolWorksAgain()
    {
        await using var app = BuildApplication();

        var (status, body) = await SendAsync(app, "POST", "ffmpeg", "decline");

        Assert.Equal(StatusCodes.Status200OK, status);
        var ffmpeg = JsonDocument.Parse(body).RootElement.GetProperty("tools").EnumerateArray()
            .Single(tool => tool.GetProperty("id").GetString() == "ffmpeg");
        Assert.True(ffmpeg.GetProperty("install_declined").GetBoolean());
        Assert.Contains("ffmpeg", await _prompts.GetDeclinedAsync());

        // Once the tool works, the decline is forgotten so a later problem is asked about afresh.
        UseFfmpeg(FfmpegArchive());
        await _installer.StartAsync(ToolRequirementIds.Ffmpeg, CancellationToken.None);
        await WaitUntilIdleAsync(ToolRequirementIds.Ffmpeg);
        await _tools.GetAsync(CancellationToken.None);
        Assert.DoesNotContain("ffmpeg", await _prompts.GetDeclinedAsync());
    }

    // ── Wiring ───────────────────────────────────────────────────────────────

    private void Build(ToolInstallPlatform? platform = null, string installKind = ToolInstallKinds.Source)
    {
        var paths = new TuvimaDataPaths(_dataRoot);
        if (!string.Equals(paths.Root, Path.GetFullPath(_dataRoot), StringComparison.Ordinal))
        {
            throw new InvalidOperationException("TUVIMA_DATA_DIR is set, so this test would write outside its own folder.");
        }

        _installer = new ToolInstallerService(
            _configuration, paths, _ocr, _source, _launcher, _activity, new RunningLifetime(),
            platform ?? new ToolInstallPlatform(true, true), () => _tools, NullLogger<ToolInstallerService>.Instance);
        _tools = new ToolRequirementsService(
            _ffmpeg, _ocr, new NoModels(), new WorkingProbe(), new NoToolWaitingWorkCounter(),
            [], new RunningLifetime(), new NotAContainer(), NullLogger<ToolRequirementsService>.Instance,
            name => name == "TUVIMA_INSTALL_KIND" ? installKind : null, null, _installer, _prompts);
    }

    private async Task<ToolRequirementDto> Row(string id) =>
        (await _tools.GetAsync(CancellationToken.None)).Tools.Single(tool => tool.Id == id);

    private void UseFfmpeg(
        byte[] archive, string? archiveSha256 = null, string? ffmpegSha256 = null, string? ffprobeSha256 = null,
        string ffmpegContent = "fake ffmpeg", string ffprobeContent = "fake ffprobe", string url = FfmpegUrl)
    {
        _settings = LoadSettings();
        _settings.Ffmpeg = new FfmpegDownload
        {
            Url = url,
            Sha256 = archiveSha256 ?? Hash(archive),
            Files =
            [
                new ExtractedFile { Name = "ffmpeg.exe", Sha256 = ffmpegSha256 ?? Hash(Encoding.UTF8.GetBytes(ffmpegContent)) },
                new ExtractedFile { Name = "ffprobe.exe", Sha256 = ffprobeSha256 ?? Hash(Encoding.UTF8.GetBytes(ffprobeContent)) },
            ],
        };
        Save();
        _source.Files[FfmpegUrl] = archive;
        _source.Files["https://example.com/ffmpeg.zip"] = archive;
    }

    private void UseTesseract(string? installerSha256 = null)
    {
        _settings = LoadSettings();
        _settings.Tesseract = new TesseractDownload
        {
            Installer = new DownloadFile
            {
                Name = "tesseract-setup.exe",
                Url = InstallerUrl,
                Sha256 = installerSha256 ?? Hash(Encoding.UTF8.GetBytes("tesseract setup")),
            },
            LanguageFiles =
            [
                new DownloadFile { Name = "eng.traineddata", Url = EnglishUrl, Sha256 = Hash(Encoding.UTF8.GetBytes("english data")) },
                new DownloadFile { Name = "osd.traineddata", Url = OrientationUrl, Sha256 = Hash(Encoding.UTF8.GetBytes("orientation data")) },
            ],
        };
        Save();
        _source.Files[InstallerUrl] = Encoding.UTF8.GetBytes("tesseract setup");
        _source.Files[EnglishUrl] = Encoding.UTF8.GetBytes("english data");
        _source.Files[OrientationUrl] = Encoding.UTF8.GetBytes("orientation data");
    }

    private ToolDownloadSettings LoadSettings() => new();

    private void Save()
    {
        Directory.CreateDirectory(Path.Combine(_root, "config"));
        File.WriteAllText(Path.Combine(_root, "config", "tool_downloads.json"), JsonSerializer.Serialize(_settings));
    }

    private static byte[] FfmpegArchive() => Zip(
        ("ffmpeg-9/bin/ffmpeg.exe", "fake ffmpeg"),
        ("ffmpeg-9/bin/ffprobe.exe", "fake ffprobe"),
        ("ffmpeg-9/bin/ffplay.exe", "not wanted"),
        ("ffmpeg-9/LICENSE.txt", "GPL licence text"));

    private static byte[] Zip(params (string Name, string Content)[] entries)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                var entry = zip.CreateEntry(name);
                using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
                writer.Write(content);
            }
        }

        return stream.ToArray();
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private async Task WaitUntilIdleAsync(string toolId) =>
        await WaitUntilAsync(() => _installer.GetSnapshot(toolId).Phase != ToolInstallPhase.Downloading);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("The condition did not become true in time.");
            }

            await Task.Delay(10);
        }
    }

    private WebApplication BuildApplication()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddAuthorization();
        builder.Services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = true);
        builder.Services.AddSingleton<IToolRequirementsService>(_tools);
        builder.Services.AddSingleton<IToolInstallerService>(_installer);
        builder.Services.AddSingleton<IToolPromptPreferenceRepository>(_prompts);
        builder.Services.AddSingleton(new ToolLocationService(
            new ServerFolderBrowserService(_configuration, NullLogger<ServerFolderBrowserService>.Instance),
            _configuration, _ocr, _tools, NullLogger<ToolLocationService>.Instance));
        var app = builder.Build();
        app.MapToolRequirementsEndpoints();
        return app;
    }

    private static IEnumerable<RouteEndpoint> Endpoints(WebApplication app) =>
        ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints).OfType<RouteEndpoint>();

    private static async Task<(int Status, string Text)> SendAsync(WebApplication app, string method, string id, string action)
    {
        var route = $"/settings/tools/{{id}}/{action}";
        var endpoint = Endpoints(app).Single(candidate =>
            candidate.RoutePattern.RawText == route
            && candidate.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods.Contains(method) == true);
        var responseBody = new MemoryStream();
        await using var scope = app.Services.CreateAsyncScope();
        var context = new DefaultHttpContext
        {
            RequestServices = scope.ServiceProvider,
            Request = { Method = method, Path = $"/settings/tools/{id}/{action}" },
            Response = { Body = responseBody },
        };
        context.Request.RouteValues["id"] = id;

        await endpoint.RequestDelegate!(context);
        responseBody.Position = 0;
        return (context.Response.StatusCode, await new StreamReader(responseBody).ReadToEndAsync());
    }

    public void Dispose()
    {
        _source.HoldAfterFirstChunk?.TrySetResult();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    // ── Fakes ────────────────────────────────────────────────────────────────

    /// <summary>The network: a map from address to bytes, with switches for offline and for holding a download mid-way.</summary>
    private sealed class FakeSource : IToolDownloadSource
    {
        public Dictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);
        public List<string> Requests { get; } = [];
        public bool Offline { get; set; }
        public TaskCompletionSource? HoldAfterFirstChunk { get; set; }
        public volatile bool Held;

        public Task<ToolDownload> OpenAsync(Uri address, CancellationToken ct)
        {
            Requests.Add(address.ToString());
            if (Offline)
            {
                throw new HttpRequestException("The network is unreachable.");
            }

            if (!Files.TryGetValue(address.ToString(), out var bytes))
            {
                throw new HttpRequestException("Not found.");
            }

            Stream content = HoldAfterFirstChunk is null ? new MemoryStream(bytes) : new HeldStream(bytes, this);
            return Task.FromResult(new ToolDownload(content, bytes.Length));
        }

        private sealed class HeldStream(byte[] bytes, FakeSource owner) : MemoryStream(bytes)
        {
            private bool _first = true;

            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                if (_first)
                {
                    _first = false;
                    return await base.ReadAsync(buffer[..Math.Min(buffer.Length, 4)], cancellationToken);
                }

                owner.Held = true;
                await owner.HoldAfterFirstChunk!.Task.WaitAsync(cancellationToken);
                return await base.ReadAsync(buffer, cancellationToken);
            }
        }
    }

    private sealed class RecordingActivity : ISystemActivityRepository
    {
        private readonly List<SystemActivityEntry> _entries = [];

        public IReadOnlyList<string> ActionTypes
        {
            get
            {
                lock (_entries)
                {
                    return _entries.Select(entry => entry.ActionType).ToList();
                }
            }
        }

        public Task LogAsync(SystemActivityEntry entry, CancellationToken ct = default)
        {
            lock (_entries)
            {
                _entries.Add(entry);
            }

            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<SystemActivityEntry>> GetRecentAsync(int limit = 50, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<int> PruneOlderThanAsync(int retentionDays, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<long> CountAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<SystemActivityEntry>> GetByRunIdAsync(Guid runId, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<SystemActivityEntry>> GetRecentByTypesAsync(
            IReadOnlyList<string> actionTypes, int limit = 50, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<SystemActivityEntry>> GetRecentByProfileAsync(
            Guid profileId, int limit = 50, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class FakeLauncher : IToolProcessLauncher
    {
        public int Runs { get; private set; }
        public string? LastPath { get; private set; }
        public Action? OnRun { get; set; }

        public Task<int> RunAndWaitAsync(string programPath, CancellationToken ct)
        {
            Runs++;
            LastPath = programPath;
            OnRun?.Invoke();
            return Task.FromResult(0);
        }
    }

    private sealed class FakePrompts : IToolPromptPreferenceRepository
    {
        private readonly HashSet<string> _declined = [];

        public Task<IReadOnlySet<string>> GetDeclinedAsync(CancellationToken ct = default)
        {
            lock (_declined)
            {
                return Task.FromResult<IReadOnlySet<string>>(_declined.ToHashSet());
            }
        }

        public Task DeclineAsync(string toolId, CancellationToken ct = default)
        {
            lock (_declined)
            {
                _declined.Add(toolId);
            }

            return Task.CompletedTask;
        }

        public Task ClearDeclinedAsync(string toolId, CancellationToken ct = default)
        {
            lock (_declined)
            {
                _declined.Remove(toolId);
            }

            return Task.CompletedTask;
        }
    }

    /// <summary>Behaves like FFmpeg detection: it finds the tool in the data folder's tools/ffmpeg when both programs are there.</summary>
    private sealed class FolderFfmpeg(string folder) : IFFmpegService
    {
        private string? _ffmpeg;
        private string? _ffprobe;

        public int VersionExitCode { get; set; }
        public string? FfmpegPath => _ffmpeg;
        public string? FfprobePath => _ffprobe;
        public bool IsAvailable => _ffmpeg is not null && _ffprobe is not null;
        public HardwareCapabilities HardwareCapabilities { get; } = new() { HasHlsMuxer = true, HasH264Encoder = true, HasAacEncoder = true };

        public FFmpegDetection Detection => new(IsAvailable ? FFmpegDetectionSource.DataFolder : FFmpegDetectionSource.None, false, null);

        public bool Redetect()
        {
            _ffmpeg = File.Exists(Path.Combine(folder, "ffmpeg.exe")) ? Path.Combine(folder, "ffmpeg.exe") : null;
            _ffprobe = File.Exists(Path.Combine(folder, "ffprobe.exe")) ? Path.Combine(folder, "ffprobe.exe") : null;
            return IsAvailable;
        }

        public Task<MediaProbeResult?> ProbeAsync(string filePath, CancellationToken ct = default) =>
            Task.FromResult<MediaProbeResult?>(null);

        public Task<(int ExitCode, string Output, string Error)> RunAsync(string arguments, CancellationToken ct = default) =>
            Task.FromResult((VersionExitCode, "ffmpeg version 9.0.1", string.Empty));
    }

    /// <summary>Text recognition that finds Tesseract once <see cref="Install"/> has put a program in the data folder.</summary>
    private sealed class FolderOcr(string languageData, string toolFolder) : IOcrService
    {
        public string LanguageDataDirectory => languageData;
        public string TemporaryRoot => Path.Combine(languageData, "tmp");
        public void Invalidate() { }

        public void Install()
        {
            Directory.CreateDirectory(toolFolder);
            File.WriteAllText(Path.Combine(toolFolder, "tesseract.exe"), "fake tesseract");
        }

        public Task<OcrCapability> GetCapabilityAsync(CancellationToken ct = default)
        {
            var path = Path.Combine(toolFolder, "tesseract.exe");
            return Task.FromResult(File.Exists(path)
                ? new OcrCapability(true, path, "5.4.0", languageData, ["eng"], true, null)
                : OcrCapability.Unavailable("Text recognition is not installed."));
        }

        public Task<OcrRecognition> RecognizeAsync(string imagePath, OcrRunOptions options, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<int?> DetectRotationAsync(string imagePath, OcrRunOptions options, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class NoModels : IModelDownloadManager
    {
        public IReadOnlyList<AiModelStatus> GetAllStatuses() => [];
        public AiModelStatus GetStatus(AiModelRole role) => throw new NotSupportedException();
        public bool AreAllModelsReady() => true;
        public Task StartDownloadAsync(AiModelRole role, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ModelDownloadResult> WaitForCompletionAsync(AiModelRole role, CancellationToken ct = default) => throw new NotSupportedException();
        public Task CancelDownloadAsync(AiModelRole role, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteModelAsync(AiModelRole role, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class WorkingProbe : IToolRuntimeProbe
    {
        public bool ImageEngineWorks() => true;
        public bool AiRuntimeLoads() => true;
        public void RefreshModelFiles() { }
    }

    private sealed class NotAContainer : IContainerProbe
    {
        public bool IsContainer() => false;
    }

    private sealed class RunningLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication()
        {
        }
    }
}
