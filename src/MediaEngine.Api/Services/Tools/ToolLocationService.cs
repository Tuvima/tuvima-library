using MediaEngine.Api.Services.LocalAssets.Ocr;
using MediaEngine.Api.Services.Settings;
using MediaEngine.Contracts.Settings;
using MediaEngine.Domain.Contracts;

namespace MediaEngine.Api.Services.Tools;

/// <summary>What happened when a person pointed the Library at a tool's folder.</summary>
public enum ToolLocationOutcome
{
    /// <summary>The folder was saved and the tools were checked again.</summary>
    Saved,

    /// <summary>The id is not a tool the person can locate.</summary>
    UnknownTool,

    /// <summary>The folder cannot be used, or does not contain the tool. <see cref="ToolLocationResult.Message"/> says which, in plain words.</summary>
    Rejected,
}

public sealed record ToolLocationResult(ToolLocationOutcome Outcome, ToolRequirementsDto? Tools = null, string? Message = null);

/// <summary>
/// Saves the folder a person chose as a tool's home, as that tool's explicit path in its existing settings file,
/// then checks the tools again. An explicit path is authoritative, so nothing is saved unless the folder really
/// holds the tool.
/// </summary>
public sealed class ToolLocationService(
    ServerFolderBrowserService folders,
    IConfigurationLoader configuration,
    IOcrService ocr,
    IToolRequirementsService tools,
    ILogger<ToolLocationService> logger)
{
    public async Task<ToolLocationResult> SetAsync(string toolId, ToolLocationRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var isFfmpeg = string.Equals(toolId, ToolRequirementIds.Ffmpeg, StringComparison.Ordinal);
        var isTesseract = string.Equals(toolId, ToolRequirementIds.Tesseract, StringComparison.Ordinal);
        if (!isFfmpeg && !isTesseract)
        {
            return new ToolLocationResult(ToolLocationOutcome.UnknownTool);
        }

        var name = isFfmpeg ? "FFmpeg" : "Tesseract";
        var folder = ResolveFolder(request);
        if (folder is null)
        {
            return Rejected("That folder can't be used.");
        }

        var suffix = OperatingSystem.IsWindows() ? ".exe" : string.Empty;
        if (isFfmpeg)
        {
            var ffmpeg = Path.Combine(folder, "ffmpeg" + suffix);
            var ffprobe = Path.Combine(folder, "ffprobe" + suffix);
            if (!File.Exists(ffmpeg) || !File.Exists(ffprobe))
            {
                return Rejected($"That folder doesn't contain {name}.");
            }

            var settings = configuration.LoadTranscoding();
            settings.FfmpegBinaryPath = ffmpeg;
            settings.FfprobeBinaryPath = ffprobe;
            configuration.SaveTranscoding(settings);
        }
        else
        {
            var tesseract = Path.Combine(folder, "tesseract" + suffix);
            if (!File.Exists(tesseract))
            {
                return Rejected($"That folder doesn't contain {name}.");
            }

            var settings = configuration.LoadOcr();
            settings.TesseractBinaryPath = tesseract;
            configuration.SaveOcr(settings);
            ocr.Invalidate();
        }

        logger.LogInformation("The location of {Tool} was chosen by an administrator and saved.", name);
        return new ToolLocationResult(ToolLocationOutcome.Saved, await tools.CheckAsync(ct).ConfigureAwait(false));
    }

    private string? ResolveFolder(ToolLocationRequest request)
    {
        try
        {
            var validation = folders.Validate(new ValidateServerFolderRequest
            {
                StorageLocationId = request.StorageLocationId ?? string.Empty,
                RelativePath = request.RelativePath,
                ManualPath = request.ManualPath,
                SelectionMode = ServerFolderSelectionModes.ToolLocation,
            });
            return validation is { CanSelect: true, Exists: true, HasRead: true } && !string.IsNullOrWhiteSpace(validation.Path)
                ? validation.Path
                : null;
        }
        catch (ServerFolderAccessException ex)
        {
            logger.LogInformation(ex, "A tool location was refused.");
            return null;
        }
    }

    private static ToolLocationResult Rejected(string message) => new(ToolLocationOutcome.Rejected, null, message);
}
