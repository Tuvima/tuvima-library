using System.Text.Json;
using MediaEngine.Plugins;

namespace MediaEngine.Plugin.CommercialSkip;

public sealed class CommercialSkipPlugin : ITuvimaPlugin
{
    public PluginManifest Manifest { get; } = new()
    {
        Id = "tuvima.commercial-skip",
        Name = "Commercial Skip",
        Version = "0.1.0",
        Kind = PluginKind.Official,
        MinimumLibraryVersion = "1.0.0",
        Description = "Detects likely commercial breaks in TV recordings using Comskip when available, with an FFmpeg-based fallback.",
        Capabilities =
        [
            new PluginCapabilityDescriptor
            {
                Kind = "playback-segment-detector",
                Name = "Commercial detector",
                Description = "Produces commercial playback segments that the player can skip.",
            },
        ],
        Permissions = [PluginPermissionIds.MediaRead, PluginPermissionIds.ProcessExecute, PluginPermissionIds.ToolDownload, PluginPermissionIds.PluginStorage],
        ToolRequirements =
        [
            new PluginToolRequirement
            {
                Id = "comskip",
                Version = "0.82",
                ExecutableName = OperatingSystem.IsWindows() ? "comskip.exe" : "comskip",
                License = "GPL-2.0",
                SourceUrl = "https://github.com/erikkaashoek/Comskip",
            },
            new PluginToolRequirement
            {
                Id = "ffmpeg",
                Version = "system",
                ExecutableName = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg",
                License = "LGPL/GPL depending on build",
                SourceUrl = "https://ffmpeg.org/",
            },
        ],
        SupportedPlatforms = ["win-x64", "linux-x64"],
        DefaultSettings = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase)
        {
            ["auto_install_tools"] = JsonSerializer.SerializeToElement(true),
            ["use_comskip"] = JsonSerializer.SerializeToElement(true),
            ["use_ffmpeg_fallback"] = JsonSerializer.SerializeToElement(true),
            ["minimum_commercial_seconds"] = JsonSerializer.SerializeToElement(30),
            ["maximum_commercial_seconds"] = JsonSerializer.SerializeToElement(600),
            ["scheduled_batch_size"] = JsonSerializer.SerializeToElement(25),
            ["tool_path"] = JsonSerializer.SerializeToElement(""),
            ["comskip_tool_path"] = JsonSerializer.SerializeToElement(""),
            ["ffmpeg_tool_path"] = JsonSerializer.SerializeToElement(""),
        },
    };

    public IReadOnlyList<IPluginCapability> CreateCapabilities() =>
    [
        new CommercialSkipSegmentDetector(Manifest),
        new ToolRequirementHealthCheck(
            Manifest,
            healthyMessage: "Comskip/FFmpeg tools are available.",
            degradedMessage: "One or more commercial detection tools are unavailable."),
    ];
}
