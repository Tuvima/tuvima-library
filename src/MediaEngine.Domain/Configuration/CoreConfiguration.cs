using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Models;

namespace MediaEngine.Domain.Configuration;

/// <summary>
/// Core application settings loaded from <c>config/core.json</c>.
///
/// Contains path configuration, schema version, and organization template.
/// These are the "where is everything" settings that apply globally.
/// </summary>
public sealed class CoreConfiguration
{
    /// <summary>
    /// Configuration format version. Increment when the shape of any config file changes.
    /// </summary>
    [JsonPropertyName("schema_version")]
    public string SchemaVersion { get; set; } = "2.0";

    /// <summary>
    /// SQLite database file path. Relative paths are resolved from the config directory.
    /// </summary>
    [JsonPropertyName("database_path")]
    public string DatabasePath { get; set; } = "library.db";

    /// <summary>
    /// Root directory for all media file storage (no BLOBs in DB).
    /// </summary>
    [JsonPropertyName("data_root")]
    public string DataRoot { get; set; } = "./media";

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnmappedProperties { get; set; }

    /// <summary>
    /// Organised media library root.
    /// Overrides <c>appsettings.json:Ingestion:LibraryRoot</c> at startup.
    /// </summary>
    [JsonPropertyName("library_root")]
    public string LibraryRoot { get; set; } = string.Empty;

    /// <summary>
    /// Tokenised path template for file organisation (e.g.
    /// <c>{Category}/{Author}/{Title}/{Title}{Ext}</c>).
    /// Used as the "default" template when no media-type-specific template
    /// matches in <see cref="OrganizationTemplates"/>.
    /// </summary>
    [JsonPropertyName("organization_template")]
    public string OrganizationTemplate { get; set; } = string.Empty;

    /// <summary>
    /// Per-media-type organisation templates.  Keys are media type names
    /// (e.g. "Books", "Audiobooks", "Movies", "TV", "Comics", "Music")
    /// or "default".  Values are tokenised path templates.
    /// Fallback chain: media-type-specific → "default" → <see cref="OrganizationTemplate"/>
    /// → hardcoded <c>{Category}/{Title}/{Title}{Ext}</c>.
    /// </summary>
    [JsonPropertyName("organization_templates")]
    public Dictionary<string, string> OrganizationTemplates { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Ordered list of provider names defining the priority order for metadata harvesting.
    /// Providers are called in this order; first provider to return data wins for each field.
    /// When empty or null, the default registration order is used.
    /// </summary>
    // ── Server identity & regional settings ───────────────────────────────────

    /// <summary>
    /// Human-readable name for this server instance, used for network discovery
    /// (mDNS/Bonjour broadcasting). Defaults to the machine name.
    /// </summary>
    [JsonPropertyName("server_name")]
    public string ServerName { get; set; } = Environment.MachineName;

    /// <summary>
    /// Structured language preferences for UI display, metadata, and content languages.
    /// </summary>
    [JsonPropertyName("language")]
    public LanguagePreferences Language { get; set; } = new();

    /// <summary>
    /// ISO 3166-1 alpha-2 country code (e.g. "US", "GB") for regional metadata
    /// storefronts (e.g. Apple Books country parameter). Defaults to the host OS region.
    /// </summary>
    [JsonPropertyName("country")]
    public string Country { get; set; } = GetDefaultCountry();

    /// <summary>
    /// Date display format for the Dashboard.
    /// Values: "system" (locale default), "short", "medium", "long", "iso8601".
    /// </summary>
    [JsonPropertyName("date_format")]
    public string DateFormat { get; set; } = "system";

    /// <summary>
    /// Time display format for the Dashboard clock and timestamps.
    /// Values: "system" (locale default), "12h", "24h".
    /// </summary>
    [JsonPropertyName("time_format")]
    public string TimeFormat { get; set; } = "system";

    /// <summary>
    /// Rate limiting policy parameters for the Engine API.
    /// Controls per-IP request limits for key generation, streaming, and general endpoints.
    /// </summary>
    [JsonPropertyName("rate_limiting")]
    public RateLimitingSettings RateLimiting { get; set; } = new();

    /// <summary>
    /// User sign-in and external identity-provider settings.
    /// </summary>
    [JsonPropertyName("auth")]
    public AuthSettings Auth { get; set; } = new();

    /// <summary>
    /// Identity pipeline tuning parameters (lease batch sizes for the three workers).
    /// Centralises cross-file batching policy in one config section instead of
    /// scattered <c>const int BatchSize</c> values inside each worker.
    /// </summary>
    [JsonPropertyName("pipeline")]
    public PipelineSettings Pipeline { get; set; } = new();

    /// <summary>
    /// Storage policy for manager-owned assets such as provider artwork and derived artifacts.
    /// </summary>
    [JsonPropertyName("storage_policy")]
    public LibraryStoragePolicy StoragePolicy { get; set; } = new();

    /// <summary>
    /// Approved plugin catalogue source used by Settings > Plugins.
    /// The Engine fetches this read-only list from GitHub so admins can discover
    /// vetted third-party plugins without enabling arbitrary install sources.
    /// </summary>
    [JsonPropertyName("plugin_catalog")]
    public PluginCatalogSettings PluginCatalog { get; set; } = new();

    private static string GetDefaultCountry()
    {
        try { return RegionInfo.CurrentRegion.TwoLetterISORegionName; }
        catch { return "US"; }
    }
}

public sealed class PluginCatalogSettings
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    [JsonPropertyName("approved_plugins_url")]
    public string ApprovedPluginsUrl { get; set; } =
        "https://raw.githubusercontent.com/Tuvima/tuvima_library/main/docs/reference/approved-plugins.json";
}

/// <summary>
/// Structured language preferences supporting multi-language media libraries.
/// </summary>
public sealed class LanguagePreferences
{
    /// <summary>
    /// UI language — controls .resx resource resolution and Wikidata label display.
    /// BCP-47 two-letter code (e.g. "en", "fr").
    /// </summary>
    [JsonPropertyName("display")]
    public string Display { get; set; } = string.IsNullOrEmpty(CultureInfo.CurrentUICulture.Name) ? "en" : CultureInfo.CurrentUICulture.Name;

    /// <summary>
    /// Primary metadata language — provider queries default to this.
    /// BCP-47 two-letter code.
    /// </summary>
    [JsonPropertyName("metadata")]
    public string Metadata { get; set; } = string.IsNullOrEmpty(CultureInfo.CurrentUICulture.Name) ? "en" : CultureInfo.CurrentUICulture.Name;

    /// <summary>
    /// Additional languages the user consumes content in.
    /// Files in these languages are NOT treated as mismatches.
    /// </summary>
    [JsonPropertyName("additional")]
    public List<string> Additional { get; set; } = [];

    /// <summary>
    /// When true, no file is ever flagged LanguageMismatch regardless of its language.
    /// Default is true — files in any language ingest without friction.
    /// </summary>
    [JsonPropertyName("accept_any")]
    public bool AcceptAny { get; set; } = true;

    /// <summary>
    /// Returns true if the given BCP-47 language code is accepted by these preferences.
    /// A language is accepted if <see cref="AcceptAny"/> is true, or if it matches
    /// <see cref="Metadata"/> or any entry in <see cref="Additional"/>.
    /// </summary>
    public bool IsLanguageAccepted(string? languageCode)
    {
        if (AcceptAny || string.IsNullOrWhiteSpace(languageCode))
        {
            return true;
        }

        var normalized = languageCode.Split('-', '_')[0].ToLowerInvariant().Trim();
        if (string.IsNullOrEmpty(normalized))
        {
            return true;
        }

        var metaNorm = Metadata.Split('-', '_')[0].ToLowerInvariant().Trim();
        if (string.Equals(normalized, metaNorm, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return Additional.Any(a =>
            string.Equals(a.Split('-', '_')[0].ToLowerInvariant().Trim(), normalized, StringComparison.OrdinalIgnoreCase));
    }
}

