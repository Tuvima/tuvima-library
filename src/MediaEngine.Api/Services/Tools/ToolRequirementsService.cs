using System.Text.RegularExpressions;
using MediaEngine.AI.Configuration;
using MediaEngine.Api.Services.LocalAssets.Ocr;
using MediaEngine.Contracts.Settings;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Models;

namespace MediaEngine.Api.Services.Tools;

/// <summary>
/// Builds the tools catalogue: FFmpeg and its streaming features, Tesseract, the image engine, the local AI
/// runtime and one row per AI model. Every check is plain data in, plain wording out; technical detail goes to the
/// log and never into the result.
/// </summary>
public sealed partial class ToolRequirementsService : IToolRequirementsService
{
    private static readonly string[] MediaTypesThatNeedFfmpeg = ["Movies", "TV", "Music", "Audiobooks"];

    private readonly IFFmpegService _ffmpeg;
    private readonly IOcrService _ocr;
    private readonly IConfigurationLoader _configuration;
    private readonly IModelDownloadManager _models;
    private readonly IToolRuntimeProbe _probe;
    private readonly IToolWaitingWorkCounter _waiting;
    private readonly IReadOnlyList<IMediaToolStatusListener> _listeners;
    private readonly IContainerProbe _container;
    private readonly ILogger<ToolRequirementsService> _logger;
    private readonly Func<string, string?> _readEnvironment;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ToolRequirementsDto? _last;

    public ToolRequirementsService(
        IFFmpegService ffmpeg,
        IOcrService ocr,
        IConfigurationLoader configuration,
        IModelDownloadManager models,
        IToolRuntimeProbe probe,
        IToolWaitingWorkCounter waiting,
        IEnumerable<IMediaToolStatusListener> listeners,
        IContainerProbe container,
        ILogger<ToolRequirementsService> logger,
        Func<string, string?>? readEnvironment = null)
    {
        _ffmpeg = ffmpeg;
        _ocr = ocr;
        _configuration = configuration;
        _models = models;
        _probe = probe;
        _waiting = waiting;
        _listeners = listeners.ToList();
        _container = container;
        _logger = logger;
        _readEnvironment = readEnvironment ?? Environment.GetEnvironmentVariable;
    }

    public async Task<ToolRequirementsDto> GetAsync(CancellationToken ct) =>
        Volatile.Read(ref _last) ?? await CheckAsync(ct).ConfigureAwait(false);

    public async Task<ToolRequirementsDto> CheckAsync(CancellationToken ct)
    {
        ToolRequirementsDto result;
        MediaToolAvailability availability;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            (result, availability) = await BuildAsync(ct).ConfigureAwait(false);
            Volatile.Write(ref _last, result);
        }
        finally
        {
            _gate.Release();
        }

        await NotifyListenersAsync(availability, ct).ConfigureAwait(false);
        return result;
    }

    private async Task<(ToolRequirementsDto Result, MediaToolAvailability Availability)> BuildAsync(CancellationToken ct)
    {
        var installKind = ResolveInstallKind();
        var ffmpegRequired = IsFfmpegRequired();
        var rows = new List<ToolRequirementDto>();

        // ── FFmpeg and its streaming features ────────────────────────────────
        _ffmpeg.Redetect();
        var (ffmpegState, ffmpegReason, ffmpegVersion) = await CheckFfmpegAsync(ct).ConfigureAwait(false);
        rows.Add(Row(
            ToolRequirementIds.Ffmpeg, "FFmpeg", "tool",
            ffmpegRequired ? ToolRequirementLevels.Required : ToolRequirementLevels.Optional,
            ffmpegState,
            ffmpegRequired ? Copy.FfmpegRequiredEffect : Copy.FfmpegOptionalEffect,
            ffmpegReason,
            Copy.ToolFix(installKind, "FFmpeg", Copy.FfmpegPackages),
            ffmpegVersion,
            canLocate: true));

        var (streamingState, streamingReason) = CheckAdaptiveStreaming(ffmpegState);
        rows.Add(Row(
            ToolRequirementIds.AdaptiveStreaming, "Smooth streaming", "tool", ToolRequirementLevels.Optional,
            streamingState, Copy.AdaptiveStreamingEffect, streamingReason,
            Copy.ToolFix(installKind, "FFmpeg", Copy.FfmpegPackages), null, canLocate: false));

        // ── Tesseract ────────────────────────────────────────────────────────
        _ocr.Invalidate();
        var capability = await _ocr.GetCapabilityAsync(ct).ConfigureAwait(false);
        var (tesseractState, tesseractReason) = ClassifyTesseract(capability);
        rows.Add(Row(
            ToolRequirementIds.Tesseract, "Tesseract", "tool", ToolRequirementLevels.Optional,
            tesseractState, Copy.TesseractEffect, tesseractReason,
            Copy.ToolFix(installKind, "Tesseract", Copy.TesseractPackages),
            tesseractState == ToolRequirementStates.Ready ? capability.Version : null,
            canLocate: true));

        // ── Image engine and local AI runtime ────────────────────────────────
        rows.Add(Row(
            ToolRequirementIds.ImageEngine, "Image engine", "tool", ToolRequirementLevels.Required,
            _probe.ImageEngineWorks() ? ToolRequirementStates.Ready : ToolRequirementStates.Problem,
            Copy.ImageEngineEffect, Copy.BundledProblemReason("The image engine"), Copy.ReinstallFix, null, canLocate: false));
        rows.Add(Row(
            ToolRequirementIds.AiRuntime, "Local AI engine", "tool", ToolRequirementLevels.Optional,
            _probe.AiRuntimeLoads() ? ToolRequirementStates.Ready : ToolRequirementStates.Problem,
            Copy.AiRuntimeEffect, Copy.BundledProblemReason("The local AI engine"), Copy.ReinstallFix, null, canLocate: false));

        // ── AI models ────────────────────────────────────────────────────────
        _probe.RefreshModelFiles();
        foreach (var model in _models.GetAllStatuses())
        {
            rows.Add(ModelRow(model));
        }

        for (var index = 0; index < rows.Count; index++)
        {
            var row = rows[index];
            if (row.State != ToolRequirementStates.Ready)
            {
                rows[index] = row with { WaitingFiles = await CountWaitingAsync(row.Id, ct).ConfigureAwait(false) };
            }
        }

        var result = new ToolRequirementsDto(
            installKind,
            DateTimeOffset.UtcNow,
            rows.Any(row => row.Requirement == ToolRequirementLevels.Required && row.State != ToolRequirementStates.Ready),
            rows);
        return (result, new MediaToolAvailability(
            ffmpegState == ToolRequirementStates.Ready,
            tesseractState == ToolRequirementStates.Ready));
    }

    // ── Individual checks ────────────────────────────────────────────────────

    private async Task<(string State, string? Reason, string? Version)> CheckFfmpegAsync(CancellationToken ct)
    {
        if (!_ffmpeg.IsAvailable)
        {
            return (ToolRequirementStates.Missing,
                _ffmpeg.Detection.ExplicitPathMissing ? Copy.ChosenLocationGone("FFmpeg") : null,
                null);
        }

        try
        {
            var result = await _ffmpeg.RunAsync("-version", ct).ConfigureAwait(false);
            if (result.ExitCode == 0)
            {
                return (ToolRequirementStates.Ready, null, ParseFfmpegVersion(result.Output));
            }

            _logger.LogWarning("FFmpeg was found but its version check exited with code {ExitCode}.", result.ExitCode);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "FFmpeg was found but could not be started.");
        }

        return (ToolRequirementStates.Problem, Copy.FoundButNotWorking("FFmpeg"), null);
    }

    private (string State, string? Reason) CheckAdaptiveStreaming(string ffmpegState)
    {
        if (ffmpegState != ToolRequirementStates.Ready)
        {
            return (ffmpegState, Copy.NeedsFfmpegFirst);
        }

        return _ffmpeg.HardwareCapabilities.AdaptiveHlsReady
            ? (ToolRequirementStates.Ready, null)
            : (ToolRequirementStates.Problem, Copy.FfmpegLacksStreaming);
    }

    private static (string State, string? Reason) ClassifyTesseract(OcrCapability capability)
    {
        if (capability.IsAvailable)
        {
            return (ToolRequirementStates.Ready, null);
        }

        if (capability.ExplicitPathMissing)
        {
            return (ToolRequirementStates.Missing, Copy.ChosenLocationGone("Tesseract"));
        }

        return capability.ExecutablePath is not null
            ? (ToolRequirementStates.Problem, Copy.FoundButNotWorking("Tesseract"))
            : (ToolRequirementStates.Missing, null);
    }

    private static ToolRequirementDto ModelRow(AiModelStatus model)
    {
        var (name, feature) = Copy.ModelNames(model.Role);
        var state = model.State switch
        {
            AiModelState.Downloading => ToolRequirementStates.Downloading,
            AiModelState.Error => ToolRequirementStates.Problem,
            AiModelState.NotDownloaded => ToolRequirementStates.Missing,
            _ => ToolRequirementStates.Ready,
        };
        var notReady = state is ToolRequirementStates.Missing or ToolRequirementStates.Problem;
        return new ToolRequirementDto(
            ToolRequirementIds.AiModelPrefix + AiModelDefinitions.ToRoleKey(model.Role),
            name,
            "ai_model",
            ToolRequirementLevels.Optional,
            state,
            $"{feature} waits until this model is downloaded.",
            state == ToolRequirementStates.Problem ? Copy.ModelDownloadFailed : null,
            state switch
            {
                ToolRequirementStates.Missing => Copy.DownloadFix,
                ToolRequirementStates.Problem => Copy.DownloadAgainFix,
                _ => string.Empty,
            },
            null,
            0,
            CanLocate: false,
            CanInstall: false,
            CanDownloadAgain: notReady,
            DownloadPercent: state == ToolRequirementStates.Downloading ? Math.Clamp(model.DownloadProgressPercent, 0, 100) : null);
    }

    private static ToolRequirementDto Row(
        string id, string name, string kind, string requirement, string state, string effect,
        string? reason, string fix, string? version, bool canLocate) =>
        new(id, name, kind, requirement, state, effect,
            state == ToolRequirementStates.Ready ? null : reason,
            state == ToolRequirementStates.Ready ? string.Empty : fix,
            version, 0,
            CanLocate: canLocate && state != ToolRequirementStates.Ready,
            CanInstall: false,
            CanDownloadAgain: false,
            DownloadPercent: null);

    private async Task<int> CountWaitingAsync(string toolId, CancellationToken ct)
    {
        try
        {
            return await _waiting.CountWaitingAsync(toolId, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not count the files waiting for {ToolId}.", toolId);
            return 0;
        }
    }

    // ── Install kind and requirement level ───────────────────────────────────

    private string ResolveInstallKind()
    {
        var configured = _readEnvironment("TUVIMA_INSTALL_KIND")?.Trim();
        foreach (var kind in new[] { ToolInstallKinds.WindowsInstaller, ToolInstallKinds.Docker, ToolInstallKinds.Source })
        {
            if (string.Equals(configured, kind, StringComparison.OrdinalIgnoreCase))
            {
                return kind;
            }
        }

        return _container.IsContainer() ? ToolInstallKinds.Docker : ToolInstallKinds.Source;
    }

    private bool IsFfmpegRequired()
    {
        try
        {
            return _configuration.LoadLibraries().Libraries
                .SelectMany(library => library.MediaTypes)
                .Any(type => MediaTypesThatNeedFfmpeg.Contains(type, StringComparer.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "The library list could not be read, so FFmpeg is treated as optional.");
            return false;
        }
    }

    // ── Listeners ────────────────────────────────────────────────────────────

    private async Task NotifyListenersAsync(MediaToolAvailability availability, CancellationToken ct)
    {
        foreach (var listener in _listeners)
        {
            try
            {
                await listener.OnMediaToolsCheckedAsync(availability, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "{Listener} failed after a tools check.", listener.GetType().Name);
            }
        }
    }

    private static string? ParseFfmpegVersion(string output)
    {
        var firstLine = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (firstLine is null)
        {
            return null;
        }

        var match = VersionNumber().Match(firstLine);
        return match.Success ? match.Groups[1].Value : null;
    }

    [GeneratedRegex(@"version\s+n?(\d+(?:\.\d+)*)", RegexOptions.IgnoreCase)]
    private static partial Regex VersionNumber();

    /// <summary>
    /// Every sentence a person can read in the tools list, in one place. Plain language: what it affects, then the
    /// fix. Never "Tuvima", never a file path, never error text (those go to the log).
    /// </summary>
    internal static class Copy
    {
        public const string FfmpegRequiredEffect = "Videos, music and audiobooks wait until FFmpeg is available.";
        public const string FfmpegOptionalEffect = "Only needed if you add videos, music or audiobooks.";
        public const string AdaptiveStreamingEffect = "Smooth streaming to phones and TVs is off; files still play directly.";
        public const string TesseractEffect = "Scanned pages and photos of paper won't be searchable.";
        public const string ImageEngineEffect = "Covers and thumbnails can't be created.";
        public const string AiRuntimeEffect = "Local AI features are off.";

        public const string ReinstallFix = "Reinstall the Library.";
        public const string DownloadFix = "Download it to turn these features on.";
        public const string DownloadAgainFix = "Download it again.";
        public const string NeedsFfmpegFirst = "It needs FFmpeg first.";
        public const string FfmpegLacksStreaming = "This copy of FFmpeg lacks some streaming features.";
        public const string ModelDownloadFailed = "The last download didn't finish.";

        public static readonly (string Windows, string Linux, string Mac) FfmpegPackages =
            ("winget install Gyan.FFmpeg", "sudo apt install ffmpeg", "brew install ffmpeg");

        public static readonly (string Windows, string Linux, string Mac) TesseractPackages =
            ("winget install UB-Mannheim.TesseractOCR", "sudo apt install tesseract-ocr", "brew install tesseract");

        public static string ChosenLocationGone(string tool) => $"The location you chose for {tool} no longer has it.";

        public static string FoundButNotWorking(string tool) => $"{tool} was found but isn't working.";

        public static string BundledProblemReason(string part) => $"{part} isn't working.";

        public static string ToolFix(string installKind, string tool, (string Windows, string Linux, string Mac) packages) =>
            installKind switch
            {
                ToolInstallKinds.WindowsInstaller =>
                    $"Reinstall the Library with the Windows installer; it includes {tool}.",
                ToolInstallKinds.Docker =>
                    $"Update to the latest Library image; it includes {tool}.",
                _ => $"Run {(OperatingSystem.IsWindows() ? packages.Windows : OperatingSystem.IsMacOS() ? packages.Mac : packages.Linux)}, then press Check again.",
            };

        public static (string Name, string Feature) ModelNames(AiModelRole role) => role switch
        {
            AiModelRole.Audio => ("Local AI audio model", "Audio transcription"),
            AiModelRole.TextScholar => ("Local AI deep text model", "Deep description analysis"),
            AiModelRole.TextCjk => ("Local AI East Asian text model", "Chinese, Japanese and Korean text analysis"),
            _ => ("Local AI text model", "Smart text analysis"),
        };
    }
}
