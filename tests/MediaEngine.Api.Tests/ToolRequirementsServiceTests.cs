using MediaEngine.Api.Services;
using MediaEngine.Api.Services.LocalAssets.Ocr;
using MediaEngine.Api.Services.Tools;
using MediaEngine.Contracts.Settings;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Models;
using Microsoft.Extensions.Logging.Abstractions;
using PipelineConfiguration = MediaEngine.Domain.Configuration.PipelineConfiguration;
using TranscodingSettings = MediaEngine.Domain.Configuration.TranscodingSettings;

namespace MediaEngine.Api.Tests;

public sealed class ToolRequirementsServiceTests
{
    private readonly LibrariesOnlyConfiguration _configuration = new();
    private readonly FakeFfmpeg _ffmpeg = new();
    private readonly FakeOcr _ocr = new();
    private readonly FakeModels _models = new();
    private readonly FakeProbe _probe = new();
    private readonly FakeCounter _counter = new();
    private readonly FakeContainer _container = new();
    private readonly List<IMediaToolStatusListener> _listeners = [];
    private readonly Dictionary<string, string?> _environment = [];

    [Fact]
    public async Task FfmpegThatRuns_IsReady_AndReportsItsVersion()
    {
        _ffmpeg.Available = true;
        _ffmpeg.VersionOutput = "ffmpeg version 7.1.1-essentials_build Copyright (c) 2000-2025 the FFmpeg developers";

        var ffmpeg = Row(await Service().CheckAsync(CancellationToken.None), ToolRequirementIds.Ffmpeg);

        Assert.Equal(ToolRequirementStates.Ready, ffmpeg.State);
        Assert.Equal("7.1.1", ffmpeg.Version);
        Assert.Equal(string.Empty, ffmpeg.Fix);
        Assert.False(ffmpeg.CanLocate);
    }

    [Fact]
    public async Task FfmpegThatIsNowhere_IsMissing_AndOffersLocateButNotInstall()
    {
        _ffmpeg.Available = false;

        var ffmpeg = Row(await Service().CheckAsync(CancellationToken.None), ToolRequirementIds.Ffmpeg);

        Assert.Equal(ToolRequirementStates.Missing, ffmpeg.State);
        Assert.True(ffmpeg.CanLocate);
        Assert.False(ffmpeg.CanInstall);
        Assert.EndsWith("then press Check again.", ffmpeg.Fix, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FfmpegThatIsFoundButAnswersWithAFailure_IsAProblemNotMissing()
    {
        _ffmpeg.Available = true;
        _ffmpeg.VersionExitCode = 1;

        var ffmpeg = Row(await Service().CheckAsync(CancellationToken.None), ToolRequirementIds.Ffmpeg);

        Assert.Equal(ToolRequirementStates.Problem, ffmpeg.State);
        Assert.Equal("FFmpeg was found but isn't working.", ffmpeg.StateReason);
    }

    [Fact]
    public async Task FfmpegThatCannotBeStarted_IsAProblem_WithoutTheErrorTextInTheResult()
    {
        _ffmpeg.Available = true;
        _ffmpeg.ThrowOnRun = new InvalidOperationException("C:\\secret\\ffmpeg.exe is not a valid Win32 application");

        var result = await Service().CheckAsync(CancellationToken.None);

        var ffmpeg = Row(result, ToolRequirementIds.Ffmpeg);
        Assert.Equal(ToolRequirementStates.Problem, ffmpeg.State);
        Assert.DoesNotContain(result.Tools, tool => (tool.StateReason ?? string.Empty).Contains("secret", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnExplicitFfmpegLocationThatNoLongerHasIt_IsMissing_WithAPlainReason()
    {
        _ffmpeg.Available = false;
        _ffmpeg.ExplicitPathMissing = true;

        var ffmpeg = Row(await Service().CheckAsync(CancellationToken.None), ToolRequirementIds.Ffmpeg);

        Assert.Equal(ToolRequirementStates.Missing, ffmpeg.State);
        Assert.Equal("The location you chose for FFmpeg no longer has it.", ffmpeg.StateReason);
    }

    [Fact]
    public async Task EveryCheckSearchesForFfmpegAgain()
    {
        var service = Service();

        await service.CheckAsync(CancellationToken.None);
        await service.CheckAsync(CancellationToken.None);

        Assert.Equal(2, _ffmpeg.RedetectCalls);
    }

    [Fact]
    public async Task ToolsThatAppearBetweenChecks_AreSeenByTheNextCheck()
    {
        _ffmpeg.Available = false;
        var service = Service();
        Assert.Equal(ToolRequirementStates.Missing, Row(await service.CheckAsync(CancellationToken.None), ToolRequirementIds.Ffmpeg).State);

        _ffmpeg.AvailableAfterRedetect = true;

        Assert.Equal(ToolRequirementStates.Ready, Row(await service.CheckAsync(CancellationToken.None), ToolRequirementIds.Ffmpeg).State);
    }

    [Fact]
    public async Task MissingTesseract_IsOptional_SaysWhatItAffects_AndDoesNotNeedAttention()
    {
        _ffmpeg.Available = true;
        _ocr.Capability = OcrCapability.Unavailable("Text recognition is not installed.");

        var result = await Service().CheckAsync(CancellationToken.None);

        var tesseract = Row(result, ToolRequirementIds.Tesseract);
        Assert.Equal(ToolRequirementLevels.Optional, tesseract.Requirement);
        Assert.Equal(ToolRequirementStates.Missing, tesseract.State);
        Assert.Equal("Scanned pages and photos of paper won't be searchable.", tesseract.Effect);
        Assert.False(result.NeedsAttention);
    }

    [Fact]
    public async Task Tesseract_FoundButNotWorking_IsAProblem()
    {
        _ocr.Capability = OcrCapability.Unavailable("The text recognition helper did not respond.") with { ExecutablePath = "x" };

        var tesseract = Row(await Service().CheckAsync(CancellationToken.None), ToolRequirementIds.Tesseract);

        Assert.Equal(ToolRequirementStates.Problem, tesseract.State);
    }

    [Fact]
    public async Task Tesseract_Ready_ReportsItsVersion_AndDropsTheFix()
    {
        _ocr.Capability = new OcrCapability(true, "x", "5.4.0", "t", ["eng"], true, null);

        var tesseract = Row(await Service().CheckAsync(CancellationToken.None), ToolRequirementIds.Tesseract);

        Assert.Equal(ToolRequirementStates.Ready, tesseract.State);
        Assert.Equal("5.4.0", tesseract.Version);
        Assert.Equal(string.Empty, tesseract.Fix);
    }

    [Fact]
    public async Task ABooksAndComicsOnlyLibrary_DoesNotRequireFfmpeg_EvenWhenItIsMissing()
    {
        WriteLibraries("Books", "Comics");
        _ffmpeg.Available = false;

        var result = await Service().CheckAsync(CancellationToken.None);

        var ffmpeg = Row(result, ToolRequirementIds.Ffmpeg);
        Assert.Equal(ToolRequirementLevels.Optional, ffmpeg.Requirement);
        Assert.Equal("Only needed if you add videos, music or audiobooks.", ffmpeg.Effect);
        Assert.False(result.NeedsAttention);
    }

    [Theory]
    [InlineData("Movies")]
    [InlineData("TV")]
    [InlineData("Music")]
    [InlineData("Audiobooks")]
    public async Task AnyLibraryThatNeedsFfmpeg_MakesItRequired_AndAMissingOneNeedsAttention(string mediaType)
    {
        WriteLibraries("Books", mediaType);
        _ffmpeg.Available = false;

        var result = await Service().CheckAsync(CancellationToken.None);

        var ffmpeg = Row(result, ToolRequirementIds.Ffmpeg);
        Assert.Equal(ToolRequirementLevels.Required, ffmpeg.Requirement);
        Assert.Equal("Videos, music and audiobooks wait until FFmpeg is available.", ffmpeg.Effect);
        Assert.True(result.NeedsAttention);
    }

    [Fact]
    public async Task ARequiredFfmpegThatIsReady_DoesNotNeedAttention()
    {
        WriteLibraries("Movies");
        _ffmpeg.Available = true;

        Assert.False((await Service().CheckAsync(CancellationToken.None)).NeedsAttention);
    }

    [Fact]
    public async Task AdaptiveStreaming_IsAProblemWhenFfmpegLacksTheStreamingFeatures()
    {
        _ffmpeg.Available = true;
        _ffmpeg.Hardware = new HardwareCapabilities { HasHlsMuxer = true, HasH264Encoder = true, HasAacEncoder = false };

        var streaming = Row(await Service().CheckAsync(CancellationToken.None), ToolRequirementIds.AdaptiveStreaming);

        Assert.Equal(ToolRequirementStates.Problem, streaming.State);
        Assert.Equal(ToolRequirementLevels.Optional, streaming.Requirement);
    }

    [Fact]
    public async Task EveryCheckTellsEachListenerWhatIsAvailable()
    {
        _ffmpeg.Available = true;
        _ocr.Capability = OcrCapability.Unavailable("Text recognition is not installed.");
        var first = new SpyListener();
        var second = new SpyListener();
        _listeners.Add(first);
        _listeners.Add(second);

        await Service().CheckAsync(CancellationToken.None);

        Assert.Equal(new MediaToolAvailability(true, false), Assert.Single(first.Received));
        Assert.Equal(new MediaToolAvailability(true, false), Assert.Single(second.Received));
    }

    [Fact]
    public async Task AListenerThatFails_DoesNotFailTheCheck_OrStopTheOthers()
    {
        var after = new SpyListener();
        _listeners.Add(new ThrowingListener());
        _listeners.Add(after);

        var result = await Service().CheckAsync(CancellationToken.None);

        Assert.NotEmpty(result.Tools);
        Assert.Single(after.Received);
    }

    [Fact]
    public async Task InstallKindEnvironmentVariable_DecidesTheFixText()
    {
        _ffmpeg.Available = false;
        _ocr.Capability = OcrCapability.Unavailable("Text recognition is not installed.");
        _environment["TUVIMA_INSTALL_KIND"] = "windows_installer";
        var windows = await Service().CheckAsync(CancellationToken.None);
        _environment["TUVIMA_INSTALL_KIND"] = "docker";
        var docker = await Service().CheckAsync(CancellationToken.None);

        Assert.Equal(ToolInstallKinds.WindowsInstaller, windows.InstallKind);
        Assert.Equal("Reinstall the Library with the Windows installer; it includes FFmpeg.", Row(windows, ToolRequirementIds.Ffmpeg).Fix);
        Assert.Equal(ToolInstallKinds.Docker, docker.InstallKind);
        Assert.Equal("Update to the latest Library image; it includes FFmpeg.", Row(docker, ToolRequirementIds.Ffmpeg).Fix);
        Assert.Equal("Reinstall the Library with the Windows installer; it includes Tesseract.", Row(windows, ToolRequirementIds.Tesseract).Fix);
    }

    [Fact]
    public async Task WithoutTheVariable_AContainerIsDocker_AndAnythingElseIsSource()
    {
        var source = await Service().CheckAsync(CancellationToken.None);
        _container.InContainer = true;
        var container = await Service().CheckAsync(CancellationToken.None);

        Assert.Equal(ToolInstallKinds.Source, source.InstallKind);
        Assert.Equal(ToolInstallKinds.Docker, container.InstallKind);
    }

    [Fact]
    public async Task EnvironmentVariableBeatsContainerDetection()
    {
        _container.InContainer = true;
        _environment["TUVIMA_INSTALL_KIND"] = "windows_installer";

        Assert.Equal(ToolInstallKinds.WindowsInstaller, (await Service().CheckAsync(CancellationToken.None)).InstallKind);
    }

    [Fact]
    public async Task WaitingFiles_AreTakenFromTheCounter_ForToolsThatAreNotReady()
    {
        _ffmpeg.Available = false;
        _counter.Counts[ToolRequirementIds.Ffmpeg] = 12;

        var result = await Service().CheckAsync(CancellationToken.None);

        Assert.Equal(12, Row(result, ToolRequirementIds.Ffmpeg).WaitingFiles);
        Assert.Equal(0, Row(result, ToolRequirementIds.Tesseract).WaitingFiles);
    }

    [Fact]
    public async Task WaitingFiles_ArePlainZero_ForAToolThatIsReady()
    {
        _ffmpeg.Available = true;
        _counter.Counts[ToolRequirementIds.Ffmpeg] = 12;

        Assert.Equal(0, Row(await Service().CheckAsync(CancellationToken.None), ToolRequirementIds.Ffmpeg).WaitingFiles);
    }

    [Fact]
    public async Task AiModels_AreReportedOneRowPerRole_WithProgressAndDownloadAgain()
    {
        _models.Statuses =
        [
            Model(AiModelRole.TextQuality, AiModelState.Ready),
            Model(AiModelRole.Audio, AiModelState.Downloading, percent: 42),
            Model(AiModelRole.TextScholar, AiModelState.NotDownloaded),
            Model(AiModelRole.TextCjk, AiModelState.Error),
        ];

        var result = await Service().CheckAsync(CancellationToken.None);

        var ready = Row(result, "ai_model:text_quality");
        var downloading = Row(result, "ai_model:audio");
        var missing = Row(result, "ai_model:text_scholar");
        var failed = Row(result, "ai_model:text_cjk");
        Assert.Equal(ToolRequirementStates.Ready, ready.State);
        Assert.False(ready.CanDownloadAgain);
        Assert.Equal(ToolRequirementStates.Downloading, downloading.State);
        Assert.Equal(42, downloading.DownloadPercent);
        Assert.Equal(ToolRequirementStates.Missing, missing.State);
        Assert.True(missing.CanDownloadAgain);
        Assert.Equal(ToolRequirementStates.Problem, failed.State);
        Assert.True(failed.CanDownloadAgain);
        Assert.All(result.Tools.Where(tool => tool.Kind == "ai_model"), tool =>
        {
            Assert.Equal(ToolRequirementLevels.Optional, tool.Requirement);
            Assert.EndsWith("waits until this model is downloaded.", tool.Effect, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task ImageEngineAndAiRuntime_FollowTheirProbes()
    {
        _probe.ImageWorks = false;
        _probe.AiLoads = false;

        var result = await Service().CheckAsync(CancellationToken.None);

        var image = Row(result, ToolRequirementIds.ImageEngine);
        Assert.Equal(ToolRequirementLevels.Required, image.Requirement);
        Assert.Equal(ToolRequirementStates.Problem, image.State);
        Assert.Equal("Covers and thumbnails can't be created.", image.Effect);
        Assert.Equal(ToolRequirementStates.Problem, Row(result, ToolRequirementIds.AiRuntime).State);
        Assert.True(result.NeedsAttention);
    }

    [Fact]
    public async Task NothingInTheResultMentionsTuvimaOrAFilePath()
    {
        _ffmpeg.Available = false;
        _ocr.Capability = OcrCapability.Unavailable("x");

        var result = await Service().CheckAsync(CancellationToken.None);

        foreach (var text in result.Tools.SelectMany(tool => new[] { tool.Name, tool.Effect, tool.StateReason ?? string.Empty, tool.Fix }))
        {
            Assert.DoesNotContain("Tuvima", text, StringComparison.Ordinal);
            Assert.DoesNotContain("\\", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Get_RunsTheFirstCheck_ThenReturnsTheLastResultWithoutLookingAgain()
    {
        var service = Service();

        var first = await service.GetAsync(CancellationToken.None);
        var second = await service.GetAsync(CancellationToken.None);

        Assert.Same(first, second);
        Assert.Equal(1, _ffmpeg.RedetectCalls);
    }

    [Fact]
    public void TheStartupLine_NamesEachToolAndMarksMissingOptionalOnes()
    {
        var dto = new ToolRequirementsDto(ToolInstallKinds.Source, DateTimeOffset.UnixEpoch, false,
        [
            new ToolRequirementDto("ffmpeg", "FFmpeg", "tool", "required", "ready", "e", null, "", "7.1", 0, false, false, false, null),
            new ToolRequirementDto("tesseract", "Tesseract", "tool", "optional", "missing", "e", null, "f", null, 0, true, false, false, null),
            new ToolRequirementDto("ai_model:audio", "Audio", "ai_model", "optional", "ready", "e", null, "", null, 0, false, false, false, null),
        ]);

        Assert.Equal("FFmpeg ready (7.1), Tesseract missing (optional), AI models 1 of 1 ready", ToolRequirementsStartupService.Summarise(dto));
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static ToolRequirementDto Row(ToolRequirementsDto result, string id) => Assert.Single(result.Tools, tool => tool.Id == id);

    private static AiModelStatus Model(AiModelRole role, AiModelState state, int percent = 0) => new()
    {
        Role = role,
        ModelType = role == AiModelRole.Audio ? AiModelType.Audio : AiModelType.Text,
        State = state,
        ModelFile = "m.gguf",
        DownloadProgressPercent = percent,
    };

    private void WriteLibraries(params string[] mediaTypes) =>
        _configuration.Libraries = new LibrariesConfiguration
        {
            Libraries = mediaTypes.Select(type => new LibraryFolderConfig { Name = type, MediaTypes = [type] }).ToList(),
        };

    private ToolRequirementsService Service() => new(
        _ffmpeg,
        _ocr,
        _configuration,
        _models,
        _probe,
        _counter,
        _listeners,
        _container,
        NullLogger<ToolRequirementsService>.Instance,
        name => _environment.GetValueOrDefault(name));

    private sealed class LibrariesOnlyConfiguration : IConfigurationLoader
    {
        public LibrariesConfiguration Libraries { get; set; } = new();

        public LibrariesConfiguration LoadLibraries() => Libraries;

        public CoreConfiguration LoadCore() => throw new NotSupportedException();
        public void SaveCore(CoreConfiguration config) => throw new NotSupportedException();
        public ScoringSettings LoadScoring() => throw new NotSupportedException();
        public void SaveScoring(ScoringSettings settings) => throw new NotSupportedException();
        public MaintenanceSettings LoadMaintenance() => throw new NotSupportedException();
        public void SaveMaintenance(MaintenanceSettings settings) => throw new NotSupportedException();
        public HydrationSettings LoadHydration() => throw new NotSupportedException();
        public void SaveHydration(HydrationSettings settings) => throw new NotSupportedException();
        public PipelineConfiguration LoadPipelines() => throw new NotSupportedException();
        public void SavePipelines(PipelineConfiguration config) => throw new NotSupportedException();
        public DisambiguationSettings LoadDisambiguation() => throw new NotSupportedException();
        public void SaveDisambiguation(DisambiguationSettings settings) => throw new NotSupportedException();
        public TranscodingSettings LoadTranscoding() => throw new NotSupportedException();
        public void SaveTranscoding(TranscodingSettings settings) => throw new NotSupportedException();
        public MediaTypeConfiguration LoadMediaTypes() => throw new NotSupportedException();
        public void SaveMediaTypes(MediaTypeConfiguration config) => throw new NotSupportedException();
        public FieldPriorityConfiguration LoadFieldPriorities() => throw new NotSupportedException();
        public void SaveFieldPriorities(FieldPriorityConfiguration config) => throw new NotSupportedException();
        public ProviderConfiguration? LoadProvider(string name) => throw new NotSupportedException();
        public void SaveProvider(ProviderConfiguration config) => throw new NotSupportedException();
        public IReadOnlyList<ProviderConfiguration> LoadAllProviders() => throw new NotSupportedException();
        public T? LoadAi<T>() where T : class => throw new NotSupportedException();
        public void SaveAi<T>(T settings) where T : class => throw new NotSupportedException();
        public PaletteConfiguration LoadPalette() => throw new NotSupportedException();
        public void SavePalette(PaletteConfiguration palette) => throw new NotSupportedException();
        public T? LoadConfig<T>(string subdirectory, string name) where T : class => throw new NotSupportedException();
        public void SaveConfig<T>(string subdirectory, string name, T config) where T : class => throw new NotSupportedException();
    }

    private sealed class FakeFfmpeg : IFFmpegService
    {
        public bool Available { get; set; } = true;
        public bool AvailableAfterRedetect { get; set; }
        public bool ExplicitPathMissing { get; set; }
        public string VersionOutput { get; set; } = "ffmpeg version 7.1";
        public int VersionExitCode { get; set; }
        public Exception? ThrowOnRun { get; set; }
        public int RedetectCalls { get; private set; }

        public HardwareCapabilities Hardware { get; set; } =
            new() { HasHlsMuxer = true, HasH264Encoder = true, HasAacEncoder = true };

        public string? FfmpegPath => Available ? "ffmpeg" : null;
        public string? FfprobePath => Available ? "ffprobe" : null;
        public bool IsAvailable => Available;
        public HardwareCapabilities HardwareCapabilities => Hardware;

        public FFmpegDetection Detection =>
            new(Available ? FFmpegDetectionSource.SystemPath : FFmpegDetectionSource.None, ExplicitPathMissing, null);

        public bool Redetect()
        {
            RedetectCalls++;
            if (AvailableAfterRedetect)
            {
                Available = true;
            }

            return Available;
        }

        public Task<MediaProbeResult?> ProbeAsync(string filePath, CancellationToken ct = default) =>
            Task.FromResult<MediaProbeResult?>(null);

        public Task<(int ExitCode, string Output, string Error)> RunAsync(string arguments, CancellationToken ct = default) =>
            ThrowOnRun is not null
                ? throw ThrowOnRun
                : Task.FromResult((VersionExitCode, VersionOutput, string.Empty));
    }

    private sealed class FakeOcr : IOcrService
    {
        public OcrCapability Capability { get; set; } = new(true, "t", "5.4.0", "d", ["eng"], true, null);
        public string LanguageDataDirectory => "d";
        public string TemporaryRoot => "t";
        public void Invalidate() { }
        public Task<OcrCapability> GetCapabilityAsync(CancellationToken ct = default) => Task.FromResult(Capability);

        public Task<OcrRecognition> RecognizeAsync(string imagePath, OcrRunOptions options, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<int?> DetectRotationAsync(string imagePath, OcrRunOptions options, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class FakeModels : IModelDownloadManager
    {
        public IReadOnlyList<AiModelStatus> Statuses { get; set; } = [];
        public IReadOnlyList<AiModelStatus> GetAllStatuses() => Statuses;
        public AiModelStatus GetStatus(AiModelRole role) => Statuses.First(status => status.Role == role);
        public bool AreAllModelsReady() => Statuses.All(status => status.State == AiModelState.Ready);
        public Task StartDownloadAsync(AiModelRole role, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ModelDownloadResult> WaitForCompletionAsync(AiModelRole role, CancellationToken ct = default) => throw new NotSupportedException();
        public Task CancelDownloadAsync(AiModelRole role, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteModelAsync(AiModelRole role, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class FakeProbe : IToolRuntimeProbe
    {
        public bool ImageWorks { get; set; } = true;
        public bool AiLoads { get; set; } = true;
        public bool ImageEngineWorks() => ImageWorks;
        public bool AiRuntimeLoads() => AiLoads;
        public void RefreshModelFiles() { }
    }

    private sealed class FakeCounter : IToolWaitingWorkCounter
    {
        public Dictionary<string, int> Counts { get; } = [];
        public Task<int> CountWaitingAsync(string toolId, CancellationToken ct) => Task.FromResult(Counts.GetValueOrDefault(toolId));
    }

    private sealed class FakeContainer : IContainerProbe
    {
        public bool InContainer { get; set; }
        public bool IsContainer() => InContainer;
    }

    private sealed class SpyListener : IMediaToolStatusListener
    {
        public List<MediaToolAvailability> Received { get; } = [];

        public Task OnMediaToolsCheckedAsync(MediaToolAvailability availability, CancellationToken ct)
        {
            Received.Add(availability);
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingListener : IMediaToolStatusListener
    {
        public Task OnMediaToolsCheckedAsync(MediaToolAvailability availability, CancellationToken ct) =>
            throw new InvalidOperationException("listener broke");
    }
}
