using System.Text.Json;
using System.Text.RegularExpressions;
using MediaEngine.AI.Configuration;
using MediaEngine.Api.Services.LocalAssets.Ocr;
using MediaEngine.Api.Services.Tools.ExifTool;
using MediaEngine.Contracts.Settings;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Models;
using Microsoft.Extensions.Hosting;

namespace MediaEngine.Api.Services.Tools;

/// <summary>
/// Builds the tools catalogue: FFmpeg and its streaming features, Tesseract, ExifTool, the image engine, the local AI
/// runtime and one row per AI model. Every check is plain data in, plain wording out; technical detail goes to the
/// log and never into the result.
/// </summary>
public sealed partial class ToolRequirementsService : IToolRequirementsService
{
    /// <summary>A result older than this is replaced by a fresh check, so a tool appearing or disappearing is noticed.</summary>
    internal static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(5);

    private readonly IFFmpegService _ffmpeg;
    private readonly IOcrService _ocr;
    private readonly IModelDownloadManager _models;
    private readonly IToolRuntimeProbe _probe;
    private readonly IToolWaitingWorkCounter _waiting;
    private readonly IReadOnlyList<IMediaToolStatusListener> _listeners;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly IContainerProbe _container;
    private readonly ILogger<ToolRequirementsService> _logger;
    private readonly Func<string, string?> _readEnvironment;
    private readonly TimeProvider _time;
    private readonly IToolInstallerService? _installer;
    private readonly ISystemActivityRepository? _activity;
    private readonly IExifToolRunner? _exifTool;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ToolRequirementsDto? _last;

    public ToolRequirementsService(
        IFFmpegService ffmpeg,
        IOcrService ocr,
        IModelDownloadManager models,
        IToolRuntimeProbe probe,
        IToolWaitingWorkCounter waiting,
        IEnumerable<IMediaToolStatusListener> listeners,
        IHostApplicationLifetime lifetime,
        IContainerProbe container,
        ILogger<ToolRequirementsService> logger,
        Func<string, string?>? readEnvironment = null,
        TimeProvider? time = null,
        IToolInstallerService? installer = null,
        ISystemActivityRepository? activity = null,
        IExifToolRunner? exifTool = null)
    {
        _ffmpeg = ffmpeg;
        _ocr = ocr;
        _models = models;
        _probe = probe;
        _waiting = waiting;
        _listeners = listeners.ToList();
        _lifetime = lifetime;
        _container = container;
        _logger = logger;
        _readEnvironment = readEnvironment ?? Environment.GetEnvironmentVariable;
        _time = time ?? TimeProvider.System;
        _installer = installer;
        _activity = activity;
        _exifTool = exifTool;
    }

    /// <summary>
    /// The last result with the live parts brought up to date: AI model rows (so download progress shows) and the
    /// count of files waiting for each tool. A result older than <see cref="StaleAfter"/> is replaced by a full check.
    /// </summary>
    public async Task<ToolRequirementsDto> GetAsync(CancellationToken ct)
    {
        var last = Volatile.Read(ref _last);
        if (last is null || _time.GetUtcNow() - last.CheckedAt > StaleAfter)
        {
            return await CheckAsync(ct).ConfigureAwait(false);
        }

        return Overlay(await RefreshLiveRowsAsync(last, ct).ConfigureAwait(false));
    }

    public async Task<ToolRequirementsDto> CheckAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var (result, availability) = await BuildAsync(ct).ConfigureAwait(false);
            var previous = Volatile.Read(ref _last);
            Volatile.Write(ref _last, result);
            await RecordChangesAsync(previous, result, ct).ConfigureAwait(false);
            // Inside the gate, so two checks can never tell the listeners out of order. The token belongs to the Engine,
            // not to the request that asked, so a closed browser tab never cuts off work the listeners start.
            await NotifyListenersAsync(availability, _lifetime.ApplicationStopping).ConfigureAwait(false);
            return Overlay(result);
        }
        finally
        {
            _gate.Release();
        }
    }

    // ── What the Activity log hears about tools ──────────────────────────────

    /// <summary>
    /// Tells the Activity log when a tool goes missing or comes back. The first check reports only the Required tools that
    /// are not ready; later checks report each change, so a tool that stays missing is written once, not every check.
    /// A failed write is logged and never fails the check.
    /// </summary>
    private async Task RecordChangesAsync(ToolRequirementsDto? previous, ToolRequirementsDto current, CancellationToken ct)
    {
        if (_activity is null)
        {
            return;
        }

        // Smooth streaming follows FFmpeg: while FFmpeg is not ready, or has only just come back, FFmpeg's own entry says it.
        static bool FfmpegReady(ToolRequirementsDto? result) =>
            result is null || result.Tools.Any(tool => tool.Id == ToolRequirementIds.Ffmpeg && tool.State == ToolRequirementStates.Ready);
        var followFfmpeg = FfmpegReady(current) && FfmpegReady(previous);
        foreach (var row in current.Tools.Where(tool => tool.Kind != "ai_model"
            && (followFfmpeg || tool.Id != ToolRequirementIds.AdaptiveStreaming)))
        {
            var isBad = row.State is ToolRequirementStates.Missing or ToolRequirementStates.Problem;
            string? actionType = null;
            if (previous is null)
            {
                if (isBad && row.Requirement == ToolRequirementLevels.Required)
                {
                    actionType = SystemActionType.ToolMissing;
                }
            }
            else
            {
                var before = previous.Tools.FirstOrDefault(tool => tool.Id == row.Id);
                if (before is not null)
                {
                    var wasBad = before.State is ToolRequirementStates.Missing or ToolRequirementStates.Problem;
                    if (isBad && before.State == ToolRequirementStates.Ready)
                    {
                        actionType = SystemActionType.ToolMissing;
                    }
                    else if (wasBad && row.State == ToolRequirementStates.Ready)
                    {
                        actionType = SystemActionType.ToolFound;
                    }
                }
            }

            if (actionType is null)
            {
                continue;
            }

            try
            {
                await _activity.LogAsync(new SystemActivityEntry
                {
                    ActionType = actionType,
                    EntityType = "Tool",
                    CollectionName = row.Name,
                    Detail = actionType == SystemActionType.ToolMissing
                        ? row.Effect
                        : string.IsNullOrWhiteSpace(row.Version) ? "It's ready." : $"Version {row.Version} is ready.",
                    ChangesJson = JsonSerializer.Serialize(new { tool_id = row.Id, tool = row.Name, state = row.State }),
                }, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "The Activity log could not be told about {Tool}.", row.Id);
            }
        }
    }

    // ── What an install in progress adds to the rows ─────────────────────────

    /// <summary>
    /// Adds the live install state (progress, offers, failures) to a result. The stored result stays as the checks
    /// found it, so the overlay is always worked out from the present moment.
    /// </summary>
    private ToolRequirementsDto Overlay(ToolRequirementsDto result)
    {
        if (_installer is null)
        {
            return result;
        }

        var rows = result.Tools.Select(row => OverlayRow(row, result.InstallKind)).ToList();
        return result with { Tools = rows };
    }

    private ToolRequirementDto OverlayRow(ToolRequirementDto row, string installKind)
    {
        if (_installer is null || row.Id is not (ToolRequirementIds.Ffmpeg or ToolRequirementIds.Tesseract or ToolRequirementIds.ExifTool)
            || row.State == ToolRequirementStates.Ready)
        {
            return row;
        }

        var snapshot = _installer.GetSnapshot(row.Id);
        switch (snapshot.Phase)
        {
            case ToolInstallPhase.Downloading:
                return row with
                {
                    State = ToolRequirementStates.Downloading,
                    DownloadPercent = snapshot.Percent,
                    StateReason = null,
                    Fix = Copy.InstallDownloading,
                    CanLocate = false,
                    CanInstall = false,
                    CanDownloadAgain = false,
                };
            case ToolInstallPhase.InstallerReady:
                return row with
                {
                    State = ToolRequirementStates.InstallerReady,
                    StateReason = snapshot.FailureReason,
                    Fix = snapshot.InstallerRunning ? Copy.TesseractInstallerOpen : Copy.TesseractInstallerReady,
                    CanInstall = false,
                    CanDownloadAgain = false,
                };
            default:
                var offer = _installer.GetOffer(row.Id, installKind);
                return row with
                {
                    StateReason = snapshot.Phase == ToolInstallPhase.Failed ? snapshot.FailureReason : row.StateReason,
                    Fix = offer.FixText ?? row.Fix,
                    CanInstall = offer.Offered && row.State == ToolRequirementStates.Missing,
                    CanDownloadAgain = offer.Offered && row.State == ToolRequirementStates.Problem,
                };
        }
    }

    private async Task<ToolRequirementsDto> RefreshLiveRowsAsync(ToolRequirementsDto last, CancellationToken ct)
    {
        var rows = last.Tools.Where(tool => tool.Kind != "ai_model").ToList();
        rows.AddRange(_models.GetAllStatuses().Select(ModelRow));
        for (var index = 0; index < rows.Count; index++)
        {
            var row = rows[index];
            rows[index] = row.State == ToolRequirementStates.Ready
                ? row with { WaitingFiles = 0 }
                : row with { WaitingFiles = await CountWaitingAsync(row.Id, ct).ConfigureAwait(false) };
        }

        return last with { Tools = rows };
    }

    private async Task<(ToolRequirementsDto Result, MediaToolAvailability Availability)> BuildAsync(CancellationToken ct)
    {
        var installKind = ResolveInstallKind();
        var rows = new List<ToolRequirementDto>();

        // ── FFmpeg and its streaming features ────────────────────────────────
        _ffmpeg.Redetect();
        var (ffmpegState, ffmpegReason, ffmpegVersion) = await CheckFfmpegAsync(ct).ConfigureAwait(false);
        rows.Add(Row(
            ToolRequirementIds.Ffmpeg, "FFmpeg", "tool", ToolRequirementLevels.Required,
            ffmpegState,
            Copy.FfmpegRequiredEffect,
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

        // ── ExifTool ─────────────────────────────────────────────────────────
        var exifTool = _exifTool is null
            ? new ExifToolStatus(ToolRequirementStates.Missing, null, PerlMissing: false)
            : await _exifTool.CheckAsync(ct).ConfigureAwait(false);
        rows.Add(Row(
            ToolRequirementIds.ExifTool, "ExifTool", "tool", ToolRequirementLevels.Optional,
            exifTool.State, Copy.ExifToolEffect,
            exifTool.State == ToolRequirementStates.Problem ? Copy.FoundButNotWorking("ExifTool") : null,
            exifTool.PerlMissing ? Copy.ExifToolPerlFix : Copy.ExifToolFix(installKind),
            exifTool.State == ToolRequirementStates.Ready ? exifTool.Version : null,
            canLocate: false));

        // ── Image engine and local AI runtime ────────────────────────────────
        rows.Add(Row(
            ToolRequirementIds.ImageEngine, "Image engine", "tool", ToolRequirementLevels.Required,
            _probe.ImageEngineWorks() ? ToolRequirementStates.Ready : ToolRequirementStates.Problem,
            Copy.ImageEngineEffect, Copy.BundledProblemReason("The image engine"), Copy.ReinstallFix, null, canLocate: false));
        rows.Add(Row(
            ToolRequirementIds.AiRuntime, "Local AI engine", "tool", ToolRequirementLevels.Optional,
            _probe.AiRuntimeLoads() ? ToolRequirementStates.Ready : ToolRequirementStates.Problem,
            Copy.AiRuntimeEffect, Copy.BundledProblemReason("The local AI engine"), Copy.AiRuntimeFix(installKind), null, canLocate: false));

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
            _time.GetUtcNow(),
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

    // ── Install kind ─────────────────────────────────────────────────────────

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

    // ── Listeners ────────────────────────────────────────────────────────────

    private async Task NotifyListenersAsync(MediaToolAvailability availability, CancellationToken ct)
    {
        foreach (var listener in _listeners)
        {
            try
            {
                await listener.OnMediaToolsCheckedAsync(availability, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // The Engine is stopping; the remaining listeners have nothing left to do.
                return;
            }
            catch (Exception ex)
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
        public const string AdaptiveStreamingEffect = "Smooth streaming to phones and TVs is off; files still play directly.";
        public const string TesseractEffect = "Scanned pages and photos of paper won't be searchable.";
        public const string ExifToolEffect = "Photos, videos and PDFs in View keep the profile name in Library only until ExifTool is ready.";
        public const string ExifToolPerlFix = "Install Perl (for example `apt install perl`), then press Check again.";
        public const string ImageEngineEffect = "Covers and thumbnails can't be created.";
        public const string AiRuntimeEffect = "Local AI features are off.";

        public const string ReinstallFix = "Reinstall the Library.";

        // A run from source has no bundled local AI engine; it is provisioned with the developer script instead.
        public static string AiRuntimeFix(string installKind) => installKind == ToolInstallKinds.Source
            ? "Install the local AI engine with the Install-AiRuntime script in the tools folder, then press Check again."
            : ReinstallFix;
        public const string DownloadFix = "Download it to turn these features on.";
        public const string DownloadAgainFix = "Download it again.";
        public const string NeedsFfmpegFirst = "It needs FFmpeg first.";
        public const string FfmpegLacksStreaming = "This copy of FFmpeg lacks some streaming features.";
        public const string ModelDownloadFailed = "The last download didn't finish.";

        // Installing FFmpeg or Tesseract on request (Install for me, Download again, Run installer).
        public const string InstallNotOffered = "The Library can't install this one for you here. Follow the steps shown for it instead.";
        public const string InstallDownloading = "Downloading now. Files that need it wait until it finishes.";
        public const string InstallFailed = "The download didn't finish. Check the connection and try again.";
        public const string InstallChecksum = "The download didn't match what the Library expected, so it wasn't used. Try again later.";
        public const string InstallNoSpace = "There isn't enough free space to download this.";
        public const string InstallFolderBusy = "The old copy is in use and couldn't be replaced. Try again in a moment.";
        public const string InstallAddressRefused = "The download address in settings isn't allowed, so nothing was downloaded.";
        public const string InstallArchiveUnexpected = "The download didn't contain what the Library expected, so it wasn't used.";
        public const string TesseractInstallerReady = "Tesseract's installer is ready. Run it to finish.";
        public const string TesseractInstallerOpen = "Finish Tesseract's installer in the window that opened, then press Check again.";
        public const string InstallerDidNotStart = "The installer didn't start. Run it again to finish.";
        public const string TesseractNotReadyYet = "Tesseract isn't ready yet. Run the installer again, or press Check again.";
        public const string NoInstallerReady = "There's no installer ready to run.";
        public const string InstallerCouldNotBeChecked = "The installer couldn't be checked just now. Try again in a moment.";
        public const string InstallerChecksum = "The installer no longer matches what the Library downloaded, so it wasn't run. Download it again.";

        public static readonly (string Windows, string Linux, string Mac) FfmpegPackages =
            ("winget install Gyan.FFmpeg", "sudo apt install ffmpeg", "brew install ffmpeg");

        public static readonly (string Windows, string Linux, string Mac) TesseractPackages =
            ("winget install UB-Mannheim.TesseractOCR", "sudo apt install tesseract-ocr", "brew install tesseract");

        public static readonly (string Windows, string Linux, string Mac) ExifToolPackages =
            ("winget install OliverBetz.ExifTool", "sudo apt install libimage-exiftool-perl", "brew install exiftool");

        // The Windows installer does not carry ExifTool (the Library fetches it by itself), so only a container is told it is included.
        public static string ExifToolFix(string installKind) => ToolFix(
            installKind == ToolInstallKinds.Docker ? ToolInstallKinds.Docker : ToolInstallKinds.Source, "ExifTool", ExifToolPackages);

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
