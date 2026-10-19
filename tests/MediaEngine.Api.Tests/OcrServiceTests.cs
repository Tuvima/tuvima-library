using MediaEngine.Api.Services.LocalAssets.Ocr;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Services;
using MediaEngine.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Api.Tests;

public sealed class OcrServiceTests : IDisposable
{
    private static readonly string ExecutableName = OperatingSystem.IsWindows() ? "tesseract.exe" : "tesseract";

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"tuvima-ocr-service-{Guid.NewGuid():N}");
    private readonly SpyOcrProcessRunner _runner = new();
    private readonly ConfigurationDirectoryLoader _configuration;

    public OcrServiceTests()
    {
        Directory.CreateDirectory(_root);
        _configuration = new ConfigurationDirectoryLoader(Path.Combine(_root, "config"));
    }

    [Fact]
    public async Task Capability_IsUnavailable_WhenNoHelperIsFound()
    {
        var service = Service(baseDirectory: Directory.CreateDirectory(Path.Combine(_root, "app")).FullName);

        var capability = await service.GetCapabilityAsync();

        Assert.False(capability.IsAvailable);
        Assert.Equal("Text recognition is not installed.", capability.Reason);
        Assert.Empty(_runner.Requests);
    }

    [Fact]
    public async Task Capability_ExplicitPathThatIsMissing_FailsInsteadOfFallingBack()
    {
        var packaged = PlaceHelper(Path.Combine(_root, "app", "tools", "tesseract"));
        _configuration.SaveOcr(new OcrSettings { TesseractBinaryPath = Path.Combine(_root, "nowhere", ExecutableName) });
        var service = Service(baseDirectory: Path.Combine(_root, "app"));

        var capability = await service.GetCapabilityAsync();

        Assert.True(File.Exists(packaged));
        Assert.False(capability.IsAvailable);
        Assert.Contains("path", capability.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(_runner.Requests);
    }

    [Fact]
    public async Task Capability_FindsTheHelperOnPath_ReadsItsVersionAndSeedsEnglishData()
    {
        var bin = Path.Combine(_root, "bin");
        PlaceHelper(bin, withLanguageData: true);
        var service = Service(baseDirectory: Path.Combine(_root, "app"), pathDirectories: [bin]);

        var capability = await service.GetCapabilityAsync();

        Assert.True(capability.IsAvailable);
        Assert.Equal("5.4.0", capability.Version);
        Assert.Equal(["eng"], capability.InstalledLanguages);
        Assert.True(capability.HasOrientationData);
        Assert.True(File.Exists(Path.Combine(service.LanguageDataDirectory, "eng.traineddata")));
        Assert.Equal(Path.Combine(bin, ExecutableName), capability.ExecutablePath);
    }

    [Fact]
    public async Task Capability_PrefersThePackagedHelperOverPath()
    {
        var packaged = PlaceHelper(Path.Combine(_root, "app", "tools", "tesseract"), withLanguageData: true);
        var bin = Path.Combine(_root, "bin");
        PlaceHelper(bin, withLanguageData: true);
        var service = Service(baseDirectory: Path.Combine(_root, "app"), pathDirectories: [bin]);

        var capability = await service.GetCapabilityAsync();

        Assert.Equal(packaged, capability.ExecutablePath);
    }

    [Fact]
    public async Task Capability_PrefersTheCopyInTheDataFolderOverThePackagedHelperAndPath()
    {
        var fetched = PlaceHelper(Path.Combine(_root, "toolsroot", "tools", "tesseract"), withLanguageData: true);
        PlaceHelper(Path.Combine(_root, "app", "tools", "tesseract"), withLanguageData: true);
        var bin = Path.Combine(_root, "bin");
        PlaceHelper(bin, withLanguageData: true);
        var service = Service(baseDirectory: Path.Combine(_root, "app"), pathDirectories: [bin], dataRoot: Path.Combine(_root, "toolsroot"));

        var capability = await service.GetCapabilityAsync();

        Assert.Equal(fetched, capability.ExecutablePath);
    }

    [Fact]
    public async Task Capability_FallsBackToAStandardInstallFolder_AfterPath()
    {
        var standard = PlaceHelper(Path.Combine(_root, "Program Files", "Tesseract-OCR"), withLanguageData: true);
        var service = Service(baseDirectory: Path.Combine(_root, "app"), standardFolders: [Path.GetDirectoryName(standard)!]);

        var capability = await service.GetCapabilityAsync();

        Assert.Equal(standard, capability.ExecutablePath);
    }

    [Fact]
    public async Task Capability_ExplicitPathThatIsMissing_IsFlaggedSoTheToolsListCanSayWhy()
    {
        _configuration.SaveOcr(new OcrSettings { TesseractBinaryPath = Path.Combine(_root, "nowhere", ExecutableName) });
        var service = Service(baseDirectory: Path.Combine(_root, "app"));

        var capability = await service.GetCapabilityAsync();

        Assert.True(capability.ExplicitPathMissing);
    }

    [Fact]
    public async Task Capability_WithoutLanguageData_IsUnavailableAndSaysSo()
    {
        var bin = Path.Combine(_root, "bin");
        PlaceHelper(bin, withLanguageData: false);
        var service = Service(baseDirectory: Path.Combine(_root, "app"), pathDirectories: [bin]);

        var capability = await service.GetCapabilityAsync();

        Assert.False(capability.IsAvailable);
        Assert.Contains("language data", capability.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Capability_WhenTheHelperFailsToRun_IsUnavailable()
    {
        var bin = Path.Combine(_root, "bin");
        PlaceHelper(bin, withLanguageData: true);
        _runner.Handler = _ => new OcrProcessResult(1, string.Empty, "broken", false);
        var service = Service(baseDirectory: Path.Combine(_root, "app"), pathDirectories: [bin]);

        var capability = await service.GetCapabilityAsync();

        Assert.False(capability.IsAvailable);
    }

    [Fact]
    public async Task Recognize_RunsTheHelperWithTheChosenLanguagesAndAveragesConfidence()
    {
        var service = ReadyService();
        var capability = await service.GetCapabilityAsync();
        var image = Path.Combine(_root, "page.png");
        File.WriteAllBytes(image, [1]);
        _runner.Handler = request =>
        {
            if (request.Arguments.Contains("--version"))
            {
                return new OcrProcessResult(0, "tesseract 5.4.0", string.Empty, false);
            }

            var outputBase = request.Arguments[1];
            File.WriteAllText(outputBase + ".txt", "  Dear Sir\nthe invoice is attached  \n");
            File.WriteAllText(outputBase + ".tsv",
                "level\tpage_num\tblock_num\tpar_num\tline_num\tword_num\tleft\ttop\twidth\theight\tconf\ttext\n" +
                "5\t1\t1\t1\t1\t1\t0\t0\t10\t10\t90.5\tDear\n" +
                "5\t1\t1\t1\t1\t2\t0\t0\t10\t10\t70\tSir\n" +
                "5\t1\t1\t1\t1\t3\t0\t0\t10\t10\t-1\t\n");
            return new OcrProcessResult(0, string.Empty, string.Empty, false);
        };

        var result = await service.RecognizeAsync(
            image, new OcrRunOptions(["eng", "spa"], LowPriority: true, TimeSpan.FromSeconds(30)));

        Assert.Equal("Dear Sir\nthe invoice is attached", result.Text);
        Assert.Equal(80.3, result.AverageConfidence);
        Assert.Equal(2, result.WordCount);
        var request = _runner.Requests[^1];
        Assert.Equal("eng+spa", request.Arguments[request.Arguments.ToList().IndexOf("-l") + 1]);
        Assert.Equal(capability.TessdataDirectory, request.Arguments[request.Arguments.ToList().IndexOf("--tessdata-dir") + 1]);
        Assert.True(request.LowPriority);
        Assert.Equal("1", request.Environment!["OMP_THREAD_LIMIT"]);
        Assert.False(File.Exists(request.Arguments[1] + ".txt"));
    }

    [Fact]
    public async Task Recognize_ReportsAFailedRunWithoutQuotingTheHelpersOutput()
    {
        var service = ReadyService();
        await service.GetCapabilityAsync();
        var image = Path.Combine(_root, "page.png");
        File.WriteAllBytes(image, [1]);
        _runner.Handler = request => request.Arguments.Contains("--version")
            ? new OcrProcessResult(0, "tesseract 5.4.0", string.Empty, false)
            : new OcrProcessResult(1, "SECRET TEXT FROM THE PAGE", "SECRET TEXT FROM THE PAGE", false);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.RecognizeAsync(image, new OcrRunOptions(["eng"], false, TimeSpan.FromSeconds(5))));

        Assert.DoesNotContain("SECRET", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DetectRotation_ReadsTheTurnTheHelperAsksFor()
    {
        var service = ReadyService();
        await service.GetCapabilityAsync();
        var image = Path.Combine(_root, "page.png");
        File.WriteAllBytes(image, [1]);
        _runner.Handler = request =>
        {
            if (request.Arguments.Contains("--version"))
            {
                return new OcrProcessResult(0, "tesseract 5.4.0", string.Empty, false);
            }

            File.WriteAllText(request.Arguments[1] + ".osd", "Page number: 0\nOrientation in degrees: 270\nRotate: 90\nOrientation confidence: 12.4\n");
            return new OcrProcessResult(0, string.Empty, string.Empty, false);
        };

        Assert.Equal(90, await service.DetectRotationAsync(image, new OcrRunOptions(["eng"], false, TimeSpan.FromSeconds(5))));
    }

    private OcrService ReadyService()
    {
        var bin = Path.Combine(_root, "bin");
        PlaceHelper(bin, withLanguageData: true);
        return Service(baseDirectory: Path.Combine(_root, "app"), pathDirectories: [bin]);
    }

    private OcrService Service(
        string baseDirectory,
        IReadOnlyList<string>? pathDirectories = null,
        string? dataRoot = null,
        IReadOnlyList<string>? standardFolders = null) => new(
        _configuration,
        new AssetPathService(Path.Combine(_root, "library"), null, Path.Combine(_root, "data")),
        _runner,
        NullLogger<OcrService>.Instance,
        new OcrEnvironment(baseDirectory, pathDirectories ?? [], dataRoot, standardFolders));

    private static string PlaceHelper(string directory, bool withLanguageData = false)
    {
        Directory.CreateDirectory(directory);
        var executable = Path.Combine(directory, ExecutableName);
        File.WriteAllBytes(executable, [0]);
        if (withLanguageData)
        {
            var data = Directory.CreateDirectory(Path.Combine(directory, "tessdata")).FullName;
            File.WriteAllBytes(Path.Combine(data, "eng.traineddata"), [1, 2, 3]);
            File.WriteAllBytes(Path.Combine(data, "osd.traineddata"), [1, 2, 3]);
        }

        return executable;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
