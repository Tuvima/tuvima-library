using System.Text.Json;
using MediaEngine.Domain.Configuration;
using MediaEngine.Plugins;

namespace MediaEngine.Plugin.MediaSegments;

/// <summary>
/// Finds the intro, the credits and the recap of an episode or film and offers them to the player as skip buttons. One plugin
/// with a switch for each kind of marker (they used to be three plugins). Commercial Skip stays separate: it is heavy and only
/// useful for recordings.
/// </summary>
public sealed class SkipMarkersPlugin : ITuvimaPlugin
{
    public const string PluginId = "tuvima.skip-markers";
    public const string DetectIntroKey = "detect_intro";
    public const string DetectCreditsKey = "detect_credits";
    public const string DetectRecapKey = "detect_recap";

    public PluginManifest Manifest { get; } = SegmentManifestFactory.Create(
        id: PluginId,
        name: "Skip markers",
        description: "Finds the intro, the credits and the recap of an episode or film, so the player can offer Skip buttons. Uses light FFmpeg checks and each kind of marker can be switched off.",
        capabilityName: "Skip marker detector",
        settings: new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase)
        {
            [DetectIntroKey] = JsonSerializer.SerializeToElement(true),
            [DetectCreditsKey] = JsonSerializer.SerializeToElement(true),
            [DetectRecapKey] = JsonSerializer.SerializeToElement(true),
            ["auto_install_tools"] = JsonSerializer.SerializeToElement(false),
            ["ffmpeg_tool_path"] = JsonSerializer.SerializeToElement(""),
            ["ffprobe_tool_path"] = JsonSerializer.SerializeToElement(""),
            ["scheduled_batch_size"] = JsonSerializer.SerializeToElement(20),
            ["minimum_intro_seconds"] = JsonSerializer.SerializeToElement(20),
            ["maximum_intro_seconds"] = JsonSerializer.SerializeToElement(120),
            ["intro_search_window_seconds"] = JsonSerializer.SerializeToElement(360),
            ["minimum_credit_seconds"] = JsonSerializer.SerializeToElement(30),
            ["credits_tail_window_seconds"] = JsonSerializer.SerializeToElement(900),
            ["minimum_recap_seconds"] = JsonSerializer.SerializeToElement(20),
            ["maximum_recap_seconds"] = JsonSerializer.SerializeToElement(180),
        },
        settingsSchema: SettingsSchema(),
        declaration: new PluginDeclaration
        {
            Audience = "everyone",
            WeightClass = "medium",
            OnFailure = "skip_and_retry",
            Notify = "quiet",
            Requires =
            [
                new PluginRequirement { Kind = "tool", Id = "ffmpeg", Optional = true },
                new PluginRequirement { Kind = "tool", Id = "ffprobe", Optional = true },
            ],
            Stores = new PluginStores { SavedOutput = "skip markers", OnRemoval = "keep" },
            Changelog = "Intro Skip, Credits Detection and Recap Detection are now one plugin with a switch for each.",
        });

    public IReadOnlyList<IPluginCapability> CreateCapabilities() =>
    [
        new IntroSkipSegmentDetector(Manifest),
        new CreditsSegmentDetector(Manifest),
        new RecapSegmentDetector(),
        new ToolRequirementHealthCheck(
            Manifest,
            healthyMessage: "Required media tools are available.",
            degradedMessage: "Some media tools are unavailable."),
    ];

    private static JsonElement SettingsSchema()
    {
        using var document = JsonDocument.Parse("""
        {
          "properties": {
            "detect_intro": { "type": "boolean", "title": "Find intros", "description": "Mark where the intro of an episode is.", "default": true },
            "detect_credits": { "type": "boolean", "title": "Find credits", "description": "Mark where the end credits start.", "default": true },
            "detect_recap": { "type": "boolean", "title": "Find recaps", "description": "Mark the recap of the last episode at the start of an episode.", "default": true },
            "scheduled_batch_size": { "type": "integer", "title": "Files per pass", "description": "How many files are checked in one background pass.", "minimum": 1, "maximum": 500, "default": 20 },
            "minimum_intro_seconds": { "type": "integer", "title": "Shortest intro", "description": "Seconds. Anything shorter is not marked.", "minimum": 5, "maximum": 300, "default": 20, "advanced": true },
            "maximum_intro_seconds": { "type": "integer", "title": "Longest intro", "description": "Seconds.", "minimum": 20, "maximum": 600, "default": 120, "advanced": true },
            "intro_search_window_seconds": { "type": "integer", "title": "Intro search window", "description": "Seconds from the start in which an intro is looked for.", "minimum": 60, "maximum": 1800, "default": 360, "advanced": true },
            "minimum_credit_seconds": { "type": "integer", "title": "Shortest credits", "description": "Seconds. Anything shorter is not marked.", "minimum": 5, "maximum": 600, "default": 30, "advanced": true },
            "credits_tail_window_seconds": { "type": "integer", "title": "Credits search window", "description": "Seconds from the end in which credits are looked for.", "minimum": 60, "maximum": 3600, "default": 900, "advanced": true },
            "minimum_recap_seconds": { "type": "integer", "title": "Shortest recap", "description": "Seconds.", "minimum": 5, "maximum": 300, "default": 20, "advanced": true },
            "maximum_recap_seconds": { "type": "integer", "title": "Longest recap", "description": "Seconds.", "minimum": 20, "maximum": 600, "default": 180, "advanced": true },
            "auto_install_tools": { "type": "boolean", "title": "Download missing tools", "description": "Allow the Library to download FFmpeg if it is missing.", "default": false, "advanced": true },
            "ffmpeg_tool_path": { "type": "string", "title": "FFmpeg location", "description": "Leave empty to use the one the Library finds.", "default": "", "advanced": true },
            "ffprobe_tool_path": { "type": "string", "title": "FFprobe location", "description": "Leave empty to use the one the Library finds.", "default": "", "advanced": true }
          },
          "required": ["detect_intro", "detect_credits", "detect_recap"]
        }
        """);
        return document.RootElement.Clone();
    }
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
        IReadOnlyList<PluginAiPermission>? ai = null,
        JsonElement? settingsSchema = null,
        PluginDeclaration? declaration = null) => new()
        {
            Id = id,
            Name = name,
            Version = "0.1.0",
            Kind = PluginKind.Official,
            MinimumLibraryVersion = "1.0.0",
            Description = description,
            Category = "media-processing",
            MediaTypes = ["movies", "tv"],
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
            SettingsSchema = settingsSchema,
            Declaration = declaration,
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
