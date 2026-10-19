using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Models;
using MediaEngine.Domain.Services;
using MediaEngine.Processors;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Processors.Tests;

public sealed class FFmpegServiceDetectionTests : IDisposable
{
    private static readonly string FfmpegName = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
    private static readonly string FfprobeName = OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe";

    private readonly string _root = Directory.CreateTempSubdirectory("tuvima-ffmpeg-detect-").FullName;
    private readonly StubTranscodingConfig _config = new();

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void Service_WithNoToolAnywhere_IsUnavailableAndSaysNothingWasFound()
    {
        var service = Service();

        Assert.False(service.IsAvailable);
        Assert.Null(service.FfmpegPath);
        Assert.Equal(FFmpegDetectionSource.None, service.Detection.Source);
        Assert.False(service.Detection.ExplicitPathMissing);
    }

    [Fact]
    public void Redetect_AfterTheToolsAppearInTheDataFolder_FindsThemThere()
    {
        var service = Service();
        Assert.False(service.IsAvailable);

        var folder = Place(Path.Combine(_root, "data", "tools", "ffmpeg"));
        var found = service.Redetect();

        Assert.True(found);
        Assert.True(service.IsAvailable);
        Assert.Equal(Path.Combine(folder, FfmpegName), service.FfmpegPath);
        Assert.Equal(Path.Combine(folder, FfprobeName), service.FfprobePath);
        Assert.Equal(FFmpegDetectionSource.DataFolder, service.Detection.Source);
    }

    [Fact]
    public void Redetect_PrefersTheDataFolderOverTheSystemPath()
    {
        var onPath = Place(Path.Combine(_root, "bin"));
        var inData = Place(Path.Combine(_root, "data", "tools", "ffmpeg"));

        var service = Service(pathDirectories: [onPath]);

        Assert.Equal(Path.Combine(inData, FfmpegName), service.FfmpegPath);
        Assert.Equal(FFmpegDetectionSource.DataFolder, service.Detection.Source);
    }

    [Fact]
    public void Service_FindsTheToolsOnThePath_ThenInStandardFoldersWhenNotOnThePath()
    {
        var onPath = Place(Path.Combine(_root, "bin"));
        var standard = Place(Path.Combine(_root, "standard"));

        var viaPath = Service(pathDirectories: [onPath], standardFolders: [standard]);
        var viaStandard = Service(standardFolders: [standard]);

        Assert.Equal(FFmpegDetectionSource.SystemPath, viaPath.Detection.Source);
        Assert.Equal(FFmpegDetectionSource.StandardFolder, viaStandard.Detection.Source);
        Assert.Equal(Path.Combine(standard, FfmpegName), viaStandard.FfmpegPath);
    }

    [Fact]
    public void Service_WithAnExplicitPathThatNoLongerExists_IsUnavailableAndFlagsIt()
    {
        Place(Path.Combine(_root, "data", "tools", "ffmpeg"));
        _config.Settings.FfmpegBinaryPath = Path.Combine(_root, "gone", FfmpegName);
        _config.Settings.FfprobeBinaryPath = Path.Combine(_root, "gone", FfprobeName);

        var service = Service();

        Assert.False(service.IsAvailable);
        Assert.Equal(FFmpegDetectionSource.None, service.Detection.Source);
        Assert.True(service.Detection.ExplicitPathMissing);
    }

    [Fact]
    public void Redetect_DoesNotChangeThePathsAnEarlierCallerAlreadyHolds()
    {
        var first = Place(Path.Combine(_root, "data", "tools", "ffmpeg"));
        var service = Service();
        var runningJob = service.Current;

        var second = Place(Path.Combine(_root, "chosen"));
        _config.Settings.FfmpegBinaryPath = Path.Combine(second, FfmpegName);
        _config.Settings.FfprobeBinaryPath = Path.Combine(second, FfprobeName);
        service.Redetect();

        Assert.Equal(Path.Combine(first, FfmpegName), runningJob.FfmpegPath);
        Assert.Equal(Path.Combine(first, FfprobeName), runningJob.FfprobePath);
        Assert.Equal(Path.Combine(second, FfmpegName), service.FfmpegPath);
        Assert.Equal(FFmpegDetectionSource.ExplicitPath, service.Detection.Source);
    }

    private FFmpegService Service(IReadOnlyList<string>? pathDirectories = null, IReadOnlyList<string>? standardFolders = null) =>
        new(
            _config,
            new TuvimaDataPaths(Path.Combine(_root, "data")),
            NullLogger<FFmpegService>.Instance,
            new FFmpegEnvironment(
                Path.Combine(_root, "app"),
                Path.Combine(_root, "data"),
                pathDirectories ?? [],
                standardFolders ?? []));

    private static string Place(string folder)
    {
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, FfmpegName), [0]);
        File.WriteAllBytes(Path.Combine(folder, FfprobeName), [0]);
        return folder;
    }

    private sealed class StubTranscodingConfig : IConfigurationLoader
    {
        public TranscodingSettings Settings { get; } = new();

        public TranscodingSettings LoadTranscoding() => Settings;

        public CoreConfiguration LoadCore() => new();
        public void SaveCore(CoreConfiguration config) { }
        public ScoringSettings LoadScoring() => new();
        public void SaveScoring(ScoringSettings settings) { }
        public MaintenanceSettings LoadMaintenance() => new();
        public void SaveMaintenance(MaintenanceSettings settings) { }
        public HydrationSettings LoadHydration() => new();
        public void SaveHydration(HydrationSettings settings) { }
        public PipelineConfiguration LoadPipelines() => new();
        public void SavePipelines(PipelineConfiguration config) { }
        public DisambiguationSettings LoadDisambiguation() => new();
        public void SaveDisambiguation(DisambiguationSettings settings) { }
        public void SaveTranscoding(TranscodingSettings settings) { }
        public MediaTypeConfiguration LoadMediaTypes() => new();
        public void SaveMediaTypes(MediaTypeConfiguration config) { }
        public LibrariesConfiguration LoadLibraries() => new();
        public FieldPriorityConfiguration LoadFieldPriorities() => new();
        public void SaveFieldPriorities(FieldPriorityConfiguration config) { }
        public ProviderConfiguration? LoadProvider(string name) => null;
        public void SaveProvider(ProviderConfiguration config) { }
        public IReadOnlyList<ProviderConfiguration> LoadAllProviders() => [];
        public T? LoadAi<T>() where T : class => null;
        public void SaveAi<T>(T settings) where T : class { }
        public PaletteConfiguration LoadPalette() => new();
        public void SavePalette(PaletteConfiguration palette) { }
        public T? LoadConfig<T>(string subdirectory, string name) where T : class => null;
        public void SaveConfig<T>(string subdirectory, string name, T config) where T : class { }
    }
}
