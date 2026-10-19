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
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Models;
using MediaEngine.Storage;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Api.Tests;

/// <summary>
/// Pointing the Library at the folder that holds FFmpeg or Tesseract: the chosen folder must really contain the
/// tool, is saved as that tool's explicit path, and the tools are checked again.
/// </summary>
public sealed class ToolLocationTests : IDisposable
{
    private static readonly string Suffix = OperatingSystem.IsWindows() ? ".exe" : string.Empty;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "tuvima-tool-location-tests", Guid.NewGuid().ToString("N"));
    private readonly string _shared;
    private readonly ConfigurationDirectoryLoader _configuration;
    private readonly ServerFolderBrowserService _folders;
    private readonly SpyOcr _ocr = new();
    private readonly ConfigurationBackedFfmpeg _ffmpeg;
    private readonly ToolRequirementsService _tools;
    private readonly ToolLocationService _service;

    public ToolLocationTests()
    {
        _shared = Path.Combine(_root, "shared");
        Directory.CreateDirectory(_shared);
        _configuration = new ConfigurationDirectoryLoader(Path.Combine(_root, "config"));
        SaveLibraries([]);
        _folders = new ServerFolderBrowserService(_configuration, NullLogger<ServerFolderBrowserService>.Instance);
        _ffmpeg = new ConfigurationBackedFfmpeg(_configuration);
        _tools = new ToolRequirementsService(
            _ffmpeg, _ocr, new NoModels(), new WorkingProbe(), new NoToolWaitingWorkCounter(),
            [], new RunningLifetime(), new NotAContainer(), NullLogger<ToolRequirementsService>.Instance, _ => null);
        _service = new ToolLocationService(_folders, _configuration, _ocr, _tools, NullLogger<ToolLocationService>.Instance);
    }

    [Fact]
    public async Task AFolderWithFfmpegAndFfprobe_IsSavedAsBothExplicitPaths_AndTheToolsCheckReportsFfmpegReady()
    {
        var folder = Install("ffmpeg/bin", "ffmpeg", "ffprobe");

        var result = await _service.SetAsync(ToolRequirementIds.Ffmpeg, Chosen("ffmpeg/bin"), CancellationToken.None);

        Assert.Equal(ToolLocationOutcome.Saved, result.Outcome);
        var settings = _configuration.LoadTranscoding();
        Assert.Equal(Path.Combine(folder, "ffmpeg" + Suffix), settings.FfmpegBinaryPath);
        Assert.Equal(Path.Combine(folder, "ffprobe" + Suffix), settings.FfprobeBinaryPath);
        var row = Assert.Single(result.Tools!.Tools, tool => tool.Id == ToolRequirementIds.Ffmpeg);
        Assert.Equal(ToolRequirementStates.Ready, row.State);
        Assert.False(row.CanLocate);
    }

    [Fact]
    public async Task AFolderThatDoesNotHoldFfmpeg_IsRefusedWithAPlainMessage_AndNothingIsSaved()
    {
        Install("empty");

        var result = await _service.SetAsync(ToolRequirementIds.Ffmpeg, Chosen("empty"), CancellationToken.None);

        Assert.Equal(ToolLocationOutcome.Rejected, result.Outcome);
        Assert.Equal("That folder doesn't contain FFmpeg.", result.Message);
        Assert.Null(result.Tools);
        Assert.Equal(string.Empty, _configuration.LoadTranscoding().FfmpegBinaryPath);
        Assert.Equal(string.Empty, _configuration.LoadTranscoding().FfprobeBinaryPath);
    }

    [Fact]
    public async Task AFolderWithFfmpegButNoFfprobe_IsRefused_BecauseTheLibraryNeedsBoth()
    {
        Install("half", "ffmpeg");

        var result = await _service.SetAsync(ToolRequirementIds.Ffmpeg, Chosen("half"), CancellationToken.None);

        Assert.Equal(ToolLocationOutcome.Rejected, result.Outcome);
        Assert.Equal(string.Empty, _configuration.LoadTranscoding().FfmpegBinaryPath);
    }

    [Fact]
    public async Task AFolderWithTesseract_IsSavedAsItsExplicitPath_AndTextRecognitionIsToldToLookAgain()
    {
        var folder = Install("tesseract", "tesseract");

        var result = await _service.SetAsync(ToolRequirementIds.Tesseract, Chosen("tesseract"), CancellationToken.None);

        Assert.Equal(ToolLocationOutcome.Saved, result.Outcome);
        Assert.Equal(Path.Combine(folder, "tesseract" + Suffix), _configuration.LoadOcr().TesseractBinaryPath);
        Assert.True(_ocr.Invalidations >= 1);
    }

    [Fact]
    public async Task AFolderThatDoesNotHoldTesseract_IsRefusedWithAPlainMessage()
    {
        Install("nothing");

        var result = await _service.SetAsync(ToolRequirementIds.Tesseract, Chosen("nothing"), CancellationToken.None);

        Assert.Equal("That folder doesn't contain Tesseract.", result.Message);
        Assert.Equal(string.Empty, _configuration.LoadOcr().TesseractBinaryPath);
    }

    [Theory]
    [InlineData("adaptive_streaming")]
    [InlineData("image_engine")]
    [InlineData("ai_model:audio")]
    [InlineData("nonsense")]
    public async Task AnythingElseThanFfmpegOrTesseract_IsUnknown(string id)
    {
        var result = await _service.SetAsync(id, Chosen(string.Empty), CancellationToken.None);

        Assert.Equal(ToolLocationOutcome.UnknownTool, result.Outcome);
    }

    [Fact]
    public async Task AFolderOutsideEveryApprovedLocation_IsRefusedInsteadOfThrowing()
    {
        var result = await _service.SetAsync(
            ToolRequirementIds.Ffmpeg, new ToolLocationRequest(null, null, "relative/not/absolute"), CancellationToken.None);

        Assert.Equal(ToolLocationOutcome.Rejected, result.Outcome);
        Assert.Equal("That folder can't be used.", result.Message);
    }

    [Fact]
    public void ToolLocationMode_AcceptsAFolderNamedBin_WhereTheLibraryModesRefuseIt()
    {
        Directory.CreateDirectory(Path.Combine(_shared, "tools", "bin"));

        var toolMode = Validate("tools/bin", ServerFolderSelectionModes.ToolLocation);
        var libraryMode = Validate("tools/bin", ServerFolderSelectionModes.ExistingLibrary);

        Assert.True(toolMode.CanSelect);
        Assert.False(libraryMode.CanSelect);
        Assert.Contains(libraryMode.Issues, issue => issue.Code == "protected_path");
    }

    [Fact]
    public void ToolLocationMode_IsReadOnly_SoAFolderInAReadOnlyLocationCanStillBeChosen()
    {
        Assert.False(ServerFolderSelectionModes.RequiresWrite(ServerFolderSelectionModes.ToolLocation));
        Assert.True(ServerFolderSelectionModes.IsValid(ServerFolderSelectionModes.ToolLocation));
    }

    [Fact]
    public void ToolLocationMode_StillRefusesTheLibrarysOwnFolders()
    {
        var dataRoot = Directory.CreateDirectory(Path.Combine(_shared, "library-data")).FullName;
        var core = _configuration.LoadCore();
        core.DataRoot = dataRoot;
        _configuration.SaveCore(core);

        var result = Validate("library-data", ServerFolderSelectionModes.ToolLocation);

        Assert.False(result.CanSelect);
        Assert.Contains(result.Issues, issue => issue.Code == "protected_path");
    }

    [Fact]
    public void ToolLocationMode_IgnoresFoldersAlreadyUsedByALibrary()
    {
        var movies = Directory.CreateDirectory(Path.Combine(_shared, "Movies")).FullName;
        SaveLibraries(
        [
            new LibraryFolderConfig
            {
                Id = Guid.NewGuid().ToString("D"),
                Name = "Movies",
                Category = "Movies",
                Kind = LibraryKinds.Catalogued,
                Area = LibraryAreas.Watch,
                Presentation = LibraryPresentations.Catalogue,
                MetadataPolicy = LibraryMetadataPolicies.Enriched,
                MediaTypes = ["Movies"],
                Sources =
                [
                    new LibrarySourceConfig
                    {
                        Id = Guid.NewGuid().ToString("D"),
                        Path = movies,
                        ManagementMode = LibrarySourceManagementModes.ExistingLibrary,
                        AccessMode = LibrarySourceAccessModes.ReadOnly,
                    },
                ],
            },
        ]);

        Assert.True(Validate("Movies", ServerFolderSelectionModes.ToolLocation).CanSelect);
        Assert.False(Validate("Movies", ServerFolderSelectionModes.ExistingLibrary).CanSelect);
    }

    // ── The route ────────────────────────────────────────────────────────────

    [Fact]
    public async Task PutLocation_IsOnlyForAdministrators_LikeTheOtherToolsRoutes()
    {
        await using var app = BuildApplication();

        foreach (var endpoint in Endpoints(app))
        {
            var policy = Assert.Single(endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>(), data => !string.IsNullOrEmpty(data.Policy));
            Assert.Equal(AuthPolicies.Administrator, policy.Policy);
        }

        Assert.Contains(Endpoints(app), endpoint => endpoint.RoutePattern.RawText == "/settings/tools/{id}/location");
    }

    [Fact]
    public async Task PutLocation_ForAFolderWithTheTool_ReturnsTheFreshToolsCheck()
    {
        Install("ffmpeg/bin", "ffmpeg", "ffprobe");
        await using var app = BuildApplication();

        var (status, body) = await PutAsync(app, "ffmpeg", new ToolLocationRequest("shared", "ffmpeg/bin", null));

        Assert.Equal(StatusCodes.Status200OK, status);
        var tools = JsonDocument.Parse(body).RootElement.GetProperty("tools");
        var ffmpeg = tools.EnumerateArray().Single(tool => tool.GetProperty("id").GetString() == "ffmpeg");
        Assert.Equal("ready", ffmpeg.GetProperty("state").GetString());
    }

    [Fact]
    public async Task PutLocation_ForAFolderWithoutTheTool_Returns400WithAPlainMessage()
    {
        Install("empty");
        await using var app = BuildApplication();

        var (status, body) = await PutAsync(app, "ffmpeg", new ToolLocationRequest("shared", "empty", null));

        Assert.Equal(StatusCodes.Status400BadRequest, status);
        Assert.Equal("That folder doesn't contain FFmpeg.", JsonDocument.Parse(body).RootElement.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task PutLocation_ForAnUnknownTool_Returns404()
    {
        await using var app = BuildApplication();

        var (status, _) = await PutAsync(app, "nonsense", new ToolLocationRequest("shared", string.Empty, null));

        Assert.Equal(StatusCodes.Status404NotFound, status);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>Creates a folder under the shared location holding empty files named like the tools.</summary>
    private string Install(string relativeFolder, params string[] programs)
    {
        var folder = Directory.CreateDirectory(Path.Combine(_shared, relativeFolder)).FullName;
        foreach (var program in programs)
        {
            File.WriteAllText(Path.Combine(folder, program + Suffix), "not a real program");
        }

        return folder;
    }

    private void SaveLibraries(List<LibraryFolderConfig> libraries) => _configuration.SaveLibraries(new LibrariesConfiguration
    {
        StorageLocations = [new ServerStorageLocationConfig { Id = "shared", Label = "Shared", Path = _shared, AllowWrite = true }],
        ViewStorage = new ViewStorageConfig { StorageLocationId = "shared", RelativeRoot = "." },
        Libraries = libraries,
    });

    private static ToolLocationRequest Chosen(string relative) => new("shared", relative, null);

    private ServerFolderValidationResultDto Validate(string relative, string mode) => _folders.Validate(new ValidateServerFolderRequest
    {
        StorageLocationId = "shared",
        RelativePath = relative,
        SelectionMode = mode,
    });

    private WebApplication BuildApplication()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddAuthorization();
        // A request the route cannot bind throws with the reason, instead of a bare 400 that hides it.
        builder.Services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = true);
        builder.Services.AddSingleton<IToolRequirementsService>(_tools);
        builder.Services.AddSingleton(_service);
        var app = builder.Build();
        app.MapToolRequirementsEndpoints();
        return app;
    }

    private static IEnumerable<RouteEndpoint> Endpoints(WebApplication app) =>
        ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints).OfType<RouteEndpoint>();

    private static async Task<(int Status, string Text)> PutAsync(WebApplication app, string id, ToolLocationRequest request)
    {
        var endpoint = Endpoints(app).Single(candidate =>
            candidate.RoutePattern.RawText == "/settings/tools/{id}/location"
            && candidate.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods.Contains("PUT") == true);
        var responseBody = new MemoryStream();
        var requestBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request));
        await using var scope = app.Services.CreateAsyncScope();
        var context = new DefaultHttpContext
        {
            RequestServices = scope.ServiceProvider,
            Request =
            {
                Method = "PUT",
                Path = $"/settings/tools/{id}/location",
                ContentType = "application/json",
                ContentLength = requestBytes.Length,
                Body = new MemoryStream(requestBytes),
            },
            Response = { Body = responseBody },
        };
        context.Request.RouteValues["id"] = id;
        context.Features.Set<IHttpRequestBodyDetectionFeature>(new RequestBodyDetectionFeature());

        await endpoint.RequestDelegate!(context);
        responseBody.Position = 0;
        return (context.Response.StatusCode, await new StreamReader(responseBody).ReadToEndAsync());
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    /// <summary>Behaves like the real FFmpeg service for detection: it finds the tool where the saved settings say it is.</summary>
    private sealed class ConfigurationBackedFfmpeg(IConfigurationLoader configuration) : IFFmpegService
    {
        private string? _ffmpeg;
        private string? _ffprobe;

        public string? FfmpegPath => _ffmpeg;
        public string? FfprobePath => _ffprobe;
        public bool IsAvailable => _ffmpeg is not null && _ffprobe is not null;
        public HardwareCapabilities HardwareCapabilities { get; } = new() { HasHlsMuxer = true, HasH264Encoder = true, HasAacEncoder = true };

        public FFmpegDetection Detection => new(
            IsAvailable ? FFmpegDetectionSource.ExplicitPath : FFmpegDetectionSource.None, false, null);

        public bool Redetect()
        {
            var settings = configuration.LoadTranscoding();
            _ffmpeg = File.Exists(settings.FfmpegBinaryPath) ? settings.FfmpegBinaryPath : null;
            _ffprobe = File.Exists(settings.FfprobeBinaryPath) ? settings.FfprobeBinaryPath : null;
            return IsAvailable;
        }

        public Task<MediaProbeResult?> ProbeAsync(string filePath, CancellationToken ct = default) =>
            Task.FromResult<MediaProbeResult?>(null);

        public Task<(int ExitCode, string Output, string Error)> RunAsync(string arguments, CancellationToken ct = default) =>
            Task.FromResult((0, "ffmpeg version 7.1", string.Empty));
    }

    private sealed class RequestBodyDetectionFeature : IHttpRequestBodyDetectionFeature
    {
        public bool CanHaveBody => true;
    }

    private sealed class SpyOcr : IOcrService
    {
        public int Invalidations { get; private set; }
        public string LanguageDataDirectory => "d";
        public string TemporaryRoot => "t";
        public void Invalidate() => Invalidations++;

        public Task<OcrCapability> GetCapabilityAsync(CancellationToken ct = default) =>
            Task.FromResult(OcrCapability.Unavailable("Text recognition is not installed."));

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
