using System.Text.Json;
using MediaEngine.Domain.Configuration;
using MediaEngine.Plugins;

namespace MediaEngine.Plugin.FandomLore;

public sealed class FandomLorePlugin : ITuvimaPlugin
{
    public const string PluginId = "tuvima.fandom-lore";

    public PluginManifest Manifest { get; } = new()
    {
        Id = PluginId,
        Name = "Fandom Lore",
        Version = "0.1.0",
        Kind = PluginKind.Official,
        MinimumLibraryVersion = "1.0.0",
        Description = "Supplements Wikidata universe graphs with approved structured Fandom wiki lore.",
        Category = "metadata",
        Capabilities =
        [
            new PluginCapabilityDescriptor
            {
                Kind = "universe-lore-provider",
                Name = "Fandom universe lore",
                Description = "Discovers approved Fandom wiki sources and extracts structured supplemental lore.",
            },
            new PluginCapabilityDescriptor
            {
                Kind = PluginCapabilityKinds.HealthCheckKind,
                Name = "Fandom API health",
                Description = "Reports whether Fandom lore settings are ready for structured extraction.",
            },
        ],
        Permissions = [PluginPermissionIds.NetworkHttp],
        Network =
        [
            new PluginNetworkDestination
            {
                Host = "www.wikidata.org",
                Sends = "Wikidata item IDs (QIDs) of Universes already in your Library, to find their Fandom wiki",
                MaxRequestsPerMinute = 60,
            },
            new PluginNetworkDestination
            {
                Host = "*.fandom.com",
                Sends = "Requests for the page list of a wiki you approved; nothing about your Library",
                MaxRequestsPerMinute = 60,
            },
        ],
        SupportedPlatforms = [], // managed code and FFmpeg only: runs on every platform
        DefaultSettings = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase)
        {
            ["auto_discovery_enabled"] = JsonSerializer.SerializeToElement(true),
            ["source_mode"] = JsonSerializer.SerializeToElement("hybrid_review"),
            ["content_mode"] = JsonSerializer.SerializeToElement("structured_only"),
            ["request_delay_ms"] = JsonSerializer.SerializeToElement(500),
            ["max_pages_per_run"] = JsonSerializer.SerializeToElement(50),
            ["confidence_threshold"] = JsonSerializer.SerializeToElement(0.65),
            ["user_agent_contact"] = JsonSerializer.SerializeToElement(""),
        },
        SettingsSchema = SettingsSchema(),
        Declaration = new PluginDeclaration
        {
            Audience = "everyone",
            Vault = "never",
            WeightClass = "light",
            OnFailure = "skip_and_retry",
            Notify = "quiet",
            Stores = new PluginStores { Records = true, SavedOutput = "lore", OnRemoval = "keep" },
            Changelog = "Everyone can read the lore; setting it up stays with administrators. Never used for Vault items.",
        },
    };

    public IReadOnlyList<IPluginCapability> CreateCapabilities() =>
    [
        new FandomUniverseLoreProvider(),
        new FandomLoreHealthCheck(),
    ];

    private static JsonElement SettingsSchema()
    {
        using var document = JsonDocument.Parse("""
        {
          "properties": {
            "auto_discovery_enabled": {
              "type": "boolean",
              "title": "Suggest Fandom sources",
              "description": "Allow Library to propose Fandom wiki sources for admin approval.",
              "default": true
            },
            "source_mode": {
              "type": "string",
              "title": "Source mode",
              "description": "Hybrid Review proposes sources but requires approval before enrichment.",
              "enum": ["hybrid_review", "manual_only"],
              "default": "hybrid_review"
            },
            "content_mode": {
              "type": "string",
              "title": "Content mode",
              "description": "Structured Only stores page metadata and categories, not article prose.",
              "enum": ["structured_only"],
              "default": "structured_only"
            },
            "request_delay_ms": {
              "type": "integer",
              "title": "Request delay",
              "description": "Delay between external requests in milliseconds.",
              "minimum": 100,
              "maximum": 10000,
              "default": 500,
              "advanced": true
            },
            "max_pages_per_run": {
              "type": "integer",
              "title": "Max pages per run",
              "description": "Maximum Fandom pages inspected during one universe enrichment run.",
              "minimum": 1,
              "maximum": 500,
              "default": 50
            },
            "confidence_threshold": {
              "type": "number",
              "title": "Confidence threshold",
              "description": "Minimum confidence required before supplemental lore is saved.",
              "minimum": 0.1,
              "maximum": 1.0,
              "default": 0.65
            },
            "user_agent_contact": {
              "type": "string",
              "title": "User-Agent contact",
              "description": "Optional admin contact text included in Fandom/Wikidata API requests.",
              "default": "",
              "advanced": true
            }
          },
          "required": ["auto_discovery_enabled", "source_mode", "content_mode", "request_delay_ms", "max_pages_per_run", "confidence_threshold"]
        }
        """);
        return document.RootElement.Clone();
    }
}
