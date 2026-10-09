using System.Text.Json.Serialization;

namespace MediaEngine.Contracts.Settings;

public sealed class ResolvedUISettingsDto
{
    [JsonPropertyName("device_class")]
    public string DeviceClass { get; set; } = "web";

    [JsonPropertyName("dark_mode")]
    public bool DarkMode { get; set; } = true;

    [JsonPropertyName("accent_color")]
    public string AccentColor { get; set; } = "#8852FC";

    [JsonPropertyName("content_padding")]
    public string ContentPadding { get; set; } = "pa-4";

    [JsonPropertyName("content_max_width")]
    public string ContentMaxWidth { get; set; } = "Full";

    [JsonPropertyName("border_radius")]
    public int BorderRadius { get; set; } = 12;

    [JsonPropertyName("constraints")]
    public UIDeviceConstraintsContract Constraints { get; set; } = new();

    [JsonPropertyName("features")]
    public UIFeatureFlagsContract Features { get; set; } = new();

    [JsonPropertyName("shell")]
    public UIShellSettingsContract Shell { get; set; } = new();

    [JsonPropertyName("pages")]
    public UIPageSettingsContract Pages { get; set; } = new();
}

public sealed class UIGlobalSettingsDto
{
    [JsonPropertyName("schema_version")]
    public string SchemaVersion { get; set; } = "1.0";

    [JsonPropertyName("dark_mode")]
    public bool DarkMode { get; set; } = true;

    [JsonPropertyName("accent_color")]
    public string AccentColor { get; set; } = "#8852FC";

    [JsonPropertyName("content_padding")]
    public string ContentPadding { get; set; } = "pa-4";

    [JsonPropertyName("content_max_width")]
    public string ContentMaxWidth { get; set; } = "Full";

    [JsonPropertyName("border_radius")]
    public int BorderRadius { get; set; } = 12;

    [JsonPropertyName("features")]
    public UIFeatureFlagsContract Features { get; set; } = new();

    [JsonPropertyName("shell")]
    public UIShellSettingsContract Shell { get; set; } = new();

    [JsonPropertyName("pages")]
    public UIPageSettingsContract Pages { get; set; } = new();
}

public sealed class UIDeviceProfileDto
{
    [JsonPropertyName("device_class")]
    public string DeviceClass { get; set; } = "web";

    [JsonPropertyName("display_name")]
    public string DisplayName { get; set; } = "Desktop Web";

    [JsonPropertyName("constraints")]
    public UIDeviceConstraintsContract Constraints { get; set; } = new();

    [JsonPropertyName("dark_mode")]
    public bool? DarkMode { get; set; }

    [JsonPropertyName("content_padding")]
    public string? ContentPadding { get; set; }

    [JsonPropertyName("content_max_width")]
    public string? ContentMaxWidth { get; set; }

    [JsonPropertyName("border_radius")]
    public int? BorderRadius { get; set; }

    [JsonPropertyName("features")]
    public UIFeatureFlagsContract? Features { get; set; }

    [JsonPropertyName("shell")]
    public UIShellSettingsContract? Shell { get; set; }

    [JsonPropertyName("pages")]
    public UIPageSettingsContract? Pages { get; set; }
}

public sealed class UIProfileSettingsDto
{
    [JsonPropertyName("profile_id")]
    public string ProfileId { get; set; } = string.Empty;

    [JsonPropertyName("dark_mode")]
    public bool? DarkMode { get; set; }

    [JsonPropertyName("accent_color")]
    public string? AccentColor { get; set; }

    [JsonPropertyName("border_radius")]
    public int? BorderRadius { get; set; }
}

public sealed class UIDeviceConstraintsContract
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

public sealed class UIFeatureFlagsContract
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

public sealed class UIShellSettingsContract
{
    [JsonPropertyName("appbar_style")]
    public string AppBarStyle { get; set; } = "full";

    [JsonPropertyName("logo_variant")]
    public string LogoVariant { get; set; } = "wordmark";

    [JsonPropertyName("intent_dock_items")]
    public List<string> IntentDockItems { get; set; } = ["Home", "Read", "Watch", "Listen", "View", "Collections"];

    [JsonPropertyName("intent_dock_style")]
    public string IntentDockStyle { get; set; } = "normal";
}

public sealed class UIPageSettingsContract
{
    [JsonPropertyName("home")]
    public UIHomePageSettingsContract Home { get; set; } = new();

    [JsonPropertyName("preferences")]
    public UIPreferencesPageSettingsContract Preferences { get; set; } = new();

    [JsonPropertyName("server_settings")]
    public UIServerSettingsPageSettingsContract ServerSettings { get; set; } = new();
}

public sealed class UIHomePageSettingsContract
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
}

public sealed class UIPreferencesPageSettingsContract
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

public sealed class UIServerSettingsPageSettingsContract
{
    [JsonPropertyName("page_enabled")]
    public bool PageEnabled { get; set; } = true;

    [JsonPropertyName("tab_bar_layout")]
    public string TabBarLayout { get; set; } = "horizontal";

    [JsonPropertyName("tab_content_layout")]
    public string TabContentLayout { get; set; } = "full";
}
