using System.Text.Json;
using MediaEngine.Domain.Configuration;
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
        ShortDescription = "Finds commercial breaks in TV recordings so the player can skip them.",
        Provides = ["Skip markers"],
        Category = "enrichment",
        MediaTypes = ["tv"],
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
        SettingsSchema = SettingsSchema(),
        Declaration = new PluginDeclaration
        {
            Audience = "everyone",
            WeightClass = "heavy",
            OnFailure = "skip_and_retry",
            Notify = "quiet",
            Requires =
            [
                new PluginRequirement { Kind = "tool", Id = "comskip", Optional = true },
                new PluginRequirement { Kind = "tool", Id = "ffmpeg", Optional = true },
            ],
            Stores = new PluginStores { SavedOutput = "commercial markers", OnRemoval = "keep" },
            Changelog = "For recordings: heavy, so it works when the machine is idle. Now has a settings form.",
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

    private static JsonElement SettingsSchema()
    {
        using var document = JsonDocument.Parse("""
        {
          "properties": {
            "use_comskip": { "type": "boolean", "title": "Use Comskip", "description": "Use Comskip when it is available. It is the more accurate of the two.", "default": true },
            "use_ffmpeg_fallback": { "type": "boolean", "title": "Fall back to FFmpeg", "description": "Use FFmpeg to find likely breaks when Comskip is not available.", "default": true },
            "minimum_commercial_seconds": { "type": "integer", "title": "Shortest break", "description": "Seconds. Anything shorter is not marked.", "minimum": 5, "maximum": 300, "default": 30 },
            "maximum_commercial_seconds": { "type": "integer", "title": "Longest break", "description": "Seconds. Anything longer is not marked.", "minimum": 60, "maximum": 1800, "default": 600 },
            "scheduled_batch_size": { "type": "integer", "title": "Files per pass", "description": "How many recordings are checked in one background pass.", "minimum": 1, "maximum": 500, "default": 25 },
            "auto_install_tools": { "type": "boolean", "title": "Download missing tools", "description": "Allow the Library to download Comskip and FFmpeg if they are missing.", "default": true, "advanced": true },
            "tool_path": { "type": "string", "title": "Tool folder", "description": "Leave empty to use the tools the Library finds.", "default": "", "advanced": true },
            "comskip_tool_path": { "type": "string", "title": "Comskip location", "description": "Leave empty to use the one the Library finds.", "default": "", "advanced": true },
            "ffmpeg_tool_path": { "type": "string", "title": "FFmpeg location", "description": "Leave empty to use the one the Library finds.", "default": "", "advanced": true }
          },
          "required": ["use_comskip", "use_ffmpeg_fallback"]
        }
        """);
        return document.RootElement.Clone();
    }
}
