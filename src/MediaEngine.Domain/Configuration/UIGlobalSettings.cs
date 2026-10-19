using System.Text.Json.Serialization;

namespace MediaEngine.Domain.Configuration;

/// <summary>
/// Application-wide UI defaults loaded from <c>config/ui/global.json</c>.
///
/// <para>
/// This is the base tier of the three-tier cascade (Global → Device → Profile).
/// Every field has a concrete default value. Device and Profile tiers override
/// individual properties; properties not overridden inherit from this global config.
/// </para>
/// </summary>
public sealed class UIGlobalSettings
{
    /// <summary>Configuration format version for the UI settings schema.</summary>
    [JsonPropertyName("schema_version")]
    public string SchemaVersion { get; set; } = "1.0";

    // ── Theme defaults ─────────────────────────────────────────────────

    /// <summary>Dark mode enabled by default (per UI ASD Section 1.2).</summary>
    [JsonPropertyName("dark_mode")]
    public bool DarkMode { get; set; } = true;

    /// <summary>Default accent colour (deep violet).</summary>
    [JsonPropertyName("accent_color")]
    public string AccentColor { get; set; } = "#8852FC";

    // ── Layout defaults ────────────────────────────────────────────────

    /// <summary>First-party native-utilities.css spacing class for the main content area.</summary>
    [JsonPropertyName("content_padding")]
    public string ContentPadding { get; set; } = "tl-sp-pa-4";

    /// <summary>
    /// First-party <c>AppMaxWidth</c> enum name for the content container.
    /// Values: <c>ExtraSmall</c>, <c>Small</c>, <c>Medium</c>, <c>Large</c>,
    /// <c>ExtraLarge</c>, <c>Full</c>.
    /// </summary>
    [JsonPropertyName("content_max_width")]
    public string ContentMaxWidth { get; set; } = "Full";

    /// <summary>Global border radius in pixels (32 = glassmorphic Spatial Bento design).</summary>
    [JsonPropertyName("border_radius")]
    public int BorderRadius { get; set; } = 12;

    // ── Feature flags ──────────────────────────────────────────────────

    /// <summary>Feature visibility flags. All enabled at global level.</summary>
    [JsonPropertyName("features")]
    public UIFeatureFlags Features { get; set; } = new();

    // ── Shell settings ─────────────────────────────────────────────────

    /// <summary>App shell (AppBar + Intent Dock) configuration.</summary>
    [JsonPropertyName("shell")]
    public UIShellSettings Shell { get; set; } = new();

    // ── Page settings ──────────────────────────────────────────────────

    /// <summary>Per-page layout settings.</summary>
    [JsonPropertyName("pages")]
    public UIPageSettings Pages { get; set; } = new();

    // ── App store links ────────────────────────────────────────────────

    /// <summary>Where each Tuvima app can be installed. Empty until an app is published; the Apps page shows only the stores that have a link.</summary>
    [JsonPropertyName("app_store_links")]
    public UIAppStoreLinks AppStoreLinks { get; set; } = new();
}

/// <summary>The store pages of the Tuvima apps. A blank value means the app is not published there yet.</summary>
public sealed class UIAppStoreLinks
{
    [JsonPropertyName("app_store")]
    public string AppStore { get; set; } = string.Empty;

    [JsonPropertyName("google_play")]
    public string GooglePlay { get; set; } = string.Empty;

    [JsonPropertyName("roku")]
    public string Roku { get; set; } = string.Empty;
}
