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
        ShortDescription = "Adds story lore from approved Fandom wikis to Universes.",
        Provides = ["Details"],
        Category = "enrichment",
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

    /// <summary>The settings this plugin shows are described in <c>fandom-lore.settings.json</c>, shipped beside the code.</summary>
    private static JsonElement SettingsSchema() =>
        ShippedPluginSettings.Read(typeof(FandomLorePlugin).Assembly, "fandom-lore.settings.json");
}
