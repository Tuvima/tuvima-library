using System.Text.Json;
using System.Text.Json.Serialization;
using MediaEngine.Contracts.Settings;

namespace MediaEngine.Web.Models.ViewDTOs;

/// <summary>
/// Fully-resolved UI configuration received from <c>GET /settings/ui/resolved</c>.
/// Every field has a concrete value (no nulls). Drives all structural and visual
/// decisions in the Dashboard.
///
/// <para>
/// Mirrors the Engine's <c>ResolvedUISettings</c> JSON shape. Kept as a separate
/// ViewModel to honour the Feature-Sliced boundary: the Dashboard never references
/// Engine models directly.
/// </para>
/// </summary>
public sealed class ResolvedUISettingsViewModel
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [JsonPropertyName("device_class")]
    public string DeviceClass { get; set; } = "web";

    // ── Theme ──────────────────────────────────────────────────────────

    [JsonPropertyName("dark_mode")]
    public bool DarkMode { get; set; } = true;

    [JsonPropertyName("accent_color")]
    public string AccentColor { get; set; } = "var(--tl-accent-primary)";

    // ── Layout ─────────────────────────────────────────────────────────

    [JsonPropertyName("content_padding")]
    public string ContentPadding { get; set; } = "tl-sp-pa-4";

    [JsonPropertyName("content_max_width")]
    public string ContentMaxWidth { get; set; } = "Full";

    [JsonPropertyName("border_radius")]
    public int BorderRadius { get; set; } = 12;

    // ── Constraints ────────────────────────────────────────────────────

    [JsonPropertyName("constraints")]
    public UIDeviceConstraintsDto Constraints { get; set; } = new();

    // ── Feature flags ──────────────────────────────────────────────────

    [JsonPropertyName("features")]
    public UIFeatureFlagsDto Features { get; set; } = new();

    // ── Shell ──────────────────────────────────────────────────────────

    [JsonPropertyName("shell")]
    public UIShellSettingsDto Shell { get; set; } = new();

    // ── Pages ──────────────────────────────────────────────────────────

    [JsonPropertyName("pages")]
    public UIPageSettingsDto Pages { get; set; } = new();

    // ── Convenience accessors ──────────────────────────────────────────

    /// <summary>Returns <c>true</c> if the named feature is disabled by device constraints.</summary>
    public bool IsFeatureDisabled(string feature) =>
        Constraints.FeaturesDisabled.Contains(feature, StringComparer.OrdinalIgnoreCase);

    /// <summary>Returns <c>true</c> if the named page is disabled by device constraints.</summary>
    public bool IsPageDisabled(string page) =>
        Constraints.PagesDisabled.Contains(page, StringComparer.OrdinalIgnoreCase);

    public static ResolvedUISettingsViewModel FromContract(ResolvedUISettingsDto settings) =>
        JsonSerializer.Deserialize<ResolvedUISettingsViewModel>(
            JsonSerializer.Serialize(settings, JsonOptions),
            JsonOptions)
        ?? throw new InvalidOperationException("Could not map resolved UI settings for presentation.");
}

/// <summary>Device-level hard constraints that profile preferences cannot override.</summary>
public sealed class UIDeviceConstraintsDto
{
    [JsonPropertyName("features_disabled")]
    public List<string> FeaturesDisabled { get; set; } = [];

    [JsonPropertyName("pages_disabled")]
    public List<string> PagesDisabled { get; set; } = [];

    [JsonPropertyName("allow_text_input")]
    public bool AllowTextInput { get; set; } = true;

    [JsonPropertyName("min_touch_target_px")]
    public int MinTouchTargetPx { get; set; } = 48;

    [JsonPropertyName("force_dark_mode")]
    public bool ForceDarkMode { get; set; }
}

/// <summary>Boolean flags for individually toggleable UI features.</summary>
public sealed class UIFeatureFlagsDto
{
    [JsonPropertyName("command_palette")]
    public bool CommandPalette { get; set; } = true;

    [JsonPropertyName("search_button")]
    public bool SearchButton { get; set; } = true;

    [JsonPropertyName("theme_toggle")]
    public bool ThemeToggle { get; set; } = true;

    [JsonPropertyName("avatar_menu")]
    public bool AvatarMenu { get; set; } = true;

    [JsonPropertyName("server_settings")]
    public bool ServerSettings { get; set; } = true;

    [JsonPropertyName("pending_files_alert")]
    public bool PendingFilesAlert { get; set; } = true;

    [JsonPropertyName("view_toggle")]
    public bool ViewToggle { get; set; } = true;

    [JsonPropertyName("profile_section")]
    public bool ProfileSection { get; set; } = true;

    [JsonPropertyName("color_picker")]
    public bool ColorPicker { get; set; } = true;
}

/// <summary>Shell-level layout configuration (TopBar, logo, dock).</summary>
public sealed class UIShellSettingsDto
{
    [JsonPropertyName("appbar_style")]
    public string AppBarStyle { get; set; } = "full";

    [JsonPropertyName("logo_variant")]
    public string LogoVariant { get; set; } = "wordmark";

    [JsonPropertyName("intent_dock_items")]
    public List<string> IntentDockItems { get; set; } = ["Home", "Libraries", "Search", "Downloads", "Settings"];

    [JsonPropertyName("intent_dock_style")]
    public string IntentDockStyle { get; set; } = "normal";

    [JsonPropertyName("top_bar_visible")]
    public bool TopBarVisible { get; set; } = true;

    [JsonPropertyName("top_bar_style")]
    public string TopBarStyle { get; set; } = "full";

    [JsonPropertyName("dock_visible")]
    public bool DockVisible { get; set; } = true;
}

/// <summary>Per-page layout settings (Home, Preferences, Server Settings).</summary>
public sealed class UIPageSettingsDto
{
    [JsonPropertyName("home")]
    public UIHomePageSettingsDto Home { get; set; } = new();

    [JsonPropertyName("preferences")]
    public UIPreferencesPageSettingsDto Preferences { get; set; } = new();

    [JsonPropertyName("server_settings")]
    public UIServerSettingsPageSettingsDto ServerSettings { get; set; } = new();
}

/// <summary>Home page layout settings.</summary>
public sealed class UIHomePageSettingsDto
{
    [JsonPropertyName("collection_hero_enabled")]
    public bool CollectionHeroEnabled { get; set; } = true;

    [JsonPropertyName("collection_hero_layout")]
    public string CollectionHeroLayout { get; set; } = "two-column";

    [JsonPropertyName("progress_cards_layout")]
    public string ProgressCardsLayout { get; set; } = "row";

    [JsonPropertyName("bento_columns")]
    public int BentoColumns { get; set; } = 3;

    [JsonPropertyName("bento_tile_style")]
    public string BentoTileStyle { get; set; } = "normal";

    [JsonPropertyName("pending_files_display")]
    public string PendingFilesDisplay { get; set; } = "expandable";

    [JsonPropertyName("swimlane_card_width")]
    public int SwimlaneCardWidth { get; set; } = 180;
}

/// <summary>Preferences page layout settings.</summary>
public sealed class UIPreferencesPageSettingsDto
{
    [JsonPropertyName("page_enabled")]
    public bool PageEnabled { get; set; } = true;

    [JsonPropertyName("tab_bar_layout")]
    public string TabBarLayout { get; set; } = "horizontal";

    [JsonPropertyName("general_tab_layout")]
    public string GeneralTabLayout { get; set; } = "full";

    [JsonPropertyName("color_swatch_count")]
    public int ColorSwatchCount { get; set; } = 8;

    [JsonPropertyName("playback_tab_enabled")]
    public bool PlaybackTabEnabled { get; set; } = true;
}

/// <summary>Server Settings page layout settings.</summary>
public sealed class UIServerSettingsPageSettingsDto
{
    [JsonPropertyName("page_enabled")]
    public bool PageEnabled { get; set; } = true;

    [JsonPropertyName("tab_bar_layout")]
    public string TabBarLayout { get; set; } = "horizontal";

    [JsonPropertyName("tab_content_layout")]
    public string TabContentLayout { get; set; } = "full";

    /// <summary>
    /// Sidebar layout for the unified Settings page.
    /// Values: "persistent" (web), "drawer" (mobile), "focus-nav" (television).
    /// </summary>
    [JsonPropertyName("sidebar_layout")]
    public string SidebarLayout { get; set; } = "persistent";
}
