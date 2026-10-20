using System.Text.Json;
using MediaEngine.Plugins;

namespace MediaEngine.Plugin.MediaSegments;

public sealed class IntroSkipPlugin : ITuvimaPlugin
{
    public PluginManifest Manifest { get; } = SegmentManifestFactory.Create(
        id: "tuvima.intro-skip",
        name: "Intro Skip",
        description: "Detects likely episode intro windows using lightweight FFmpeg cues.",
        capabilityName: "Intro detector",
        settings: new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase)
        {
            ["auto_install_tools"] = JsonSerializer.SerializeToElement(false),
            ["ffmpeg_tool_path"] = JsonSerializer.SerializeToElement(""),
            ["ffprobe_tool_path"] = JsonSerializer.SerializeToElement(""),
            ["scheduled_batch_size"] = JsonSerializer.SerializeToElement(20),
            ["minimum_intro_seconds"] = JsonSerializer.SerializeToElement(20),
            ["maximum_intro_seconds"] = JsonSerializer.SerializeToElement(120),
            ["intro_search_window_seconds"] = JsonSerializer.SerializeToElement(360),
        });

    public IReadOnlyList<IPluginCapability> CreateCapabilities() =>
    [
        new IntroSkipSegmentDetector(Manifest),
        new ToolRequirementHealthCheck(
            Manifest,
            healthyMessage: "Required media tools are available.",
            degradedMessage: "Some media tools are unavailable."),
    ];
}

public sealed class CreditsDetectionPlugin : ITuvimaPlugin
{
    public PluginManifest Manifest { get; } = SegmentManifestFactory.Create(
        id: "tuvima.credits-detection",
        name: "Credits Detection",
        description: "Finds likely credit starts using low-cost end-of-file video/audio cues.",
        capabilityName: "Credits detector",
        settings: new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase)
        {
            ["auto_install_tools"] = JsonSerializer.SerializeToElement(false),
            ["ffmpeg_tool_path"] = JsonSerializer.SerializeToElement(""),
            ["ffprobe_tool_path"] = JsonSerializer.SerializeToElement(""),
            ["scheduled_batch_size"] = JsonSerializer.SerializeToElement(25),
            ["minimum_credit_seconds"] = JsonSerializer.SerializeToElement(30),
            ["credits_tail_window_seconds"] = JsonSerializer.SerializeToElement(900),
        });

    public IReadOnlyList<IPluginCapability> CreateCapabilities() =>
    [
        new CreditsSegmentDetector(Manifest),
        new ToolRequirementHealthCheck(
            Manifest,
            healthyMessage: "Required media tools are available.",
            degradedMessage: "Some media tools are unavailable."),
    ];
}

public sealed class RecapDetectionPlugin : ITuvimaPlugin
{
    public PluginManifest Manifest { get; } = SegmentManifestFactory.Create(
        id: "tuvima.recap-detection",
        name: "Recap Detection",
        description: "Marks likely recap windows near the beginning of TV episodes.",
        capabilityName: "Recap detector",
        settings: new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase)
        {
            ["scheduled_batch_size"] = JsonSerializer.SerializeToElement(20),
            ["minimum_recap_seconds"] = JsonSerializer.SerializeToElement(20),
            ["maximum_recap_seconds"] = JsonSerializer.SerializeToElement(180),
        },
        tools: []);

    public IReadOnlyList<IPluginCapability> CreateCapabilities() =>
    [
        new RecapSegmentDetector(),
    ];
}

public sealed class AiVisualVerifierPlugin : ITuvimaPlugin
{
    public PluginManifest Manifest { get; } = SegmentManifestFactory.Create(
        id: "tuvima.ai-visual-segment-verifier",
        name: "AI Visual Segment Verifier",
        description: "Reserved optional verifier for ambiguous segment candidates once local multimodal runtime support is enabled.",
        capabilityName: "AI verifier",
        settings: new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase)
        {
            ["scheduled_batch_size"] = JsonSerializer.SerializeToElement(10),
            ["allow_on_demand_ai"] = JsonSerializer.SerializeToElement(false),
        },
        tools: [],
        ai:
        [
            new PluginAiPermission
            {
                Role = "vision",
                MaxTokens = 256,
                Schedule = "off-peak",
                ResourceClass = "ai-heavy",
            },
        ]);

    public IReadOnlyList<IPluginCapability> CreateCapabilities() =>
    [
        new AiVisualVerifierHealthCheck(),
    ];
}

internal static class SegmentManifestFactory
{
    public static PluginManifest Create(
        string id,
        string name,
        string description,
        string capabilityName,
        Dictionary<string, JsonElement> settings,
        IReadOnlyList<PluginToolRequirement>? tools = null,
        IReadOnlyList<PluginAiPermission>? ai = null) => new()
        {
            Id = id,
            Name = name,
            Version = "0.1.0",
            Kind = PluginKind.Official,
            MinimumLibraryVersion = "1.0.0",
            Description = description,
            Capabilities =
        [
            new PluginCapabilityDescriptor
            {
                Kind = "playback-segment-detector",
                Name = capabilityName,
                Description = "Produces playback markers for the shared Library player.",
            },
        ],
            Permissions = BuildPermissions(tools, ai),
            ToolRequirements = tools ?? FfmpegTools(),
            AiPermissions = ai ?? [],
            SupportedPlatforms = [], // managed code and FFmpeg only: runs on every platform
            DefaultSettings = settings,
        };

    private static IReadOnlyList<string> BuildPermissions(
        IReadOnlyList<PluginToolRequirement>? tools,
        IReadOnlyList<PluginAiPermission>? ai)
    {
        var permissions = new List<string> { PluginPermissionIds.MediaRead };
        if ((tools ?? FfmpegTools()).Count > 0)
        {
            permissions.Add(PluginPermissionIds.ProcessExecute);
            permissions.Add(PluginPermissionIds.PluginStorage);
        }
        if (ai is { Count: > 0 })
        {
            permissions.Add(PluginPermissionIds.AiInfer);
        }

        return permissions;
    }

    private static IReadOnlyList<PluginToolRequirement> FfmpegTools() =>
    [
        new()
        {
            Id = "ffmpeg",
            Version = "system",
            ExecutableName = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg",
            License = "LGPL/GPL depending on build",
            SourceUrl = "https://ffmpeg.org/",
        },
        new()
        {
            Id = "ffprobe",
            Version = "system",
            ExecutableName = OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe",
            License = "LGPL/GPL depending on build",
            SourceUrl = "https://ffmpeg.org/",
        },
    ];
}
