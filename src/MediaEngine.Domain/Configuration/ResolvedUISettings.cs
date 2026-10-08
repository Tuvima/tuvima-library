using System.Text.Json.Serialization;

namespace MediaEngine.Domain.Configuration;

/// <summary>
/// The fully cascaded UI configuration. Every field has a concrete value (no nulls).
///
/// <para>
/// Produced by <see cref="UISettingsCascadeResolver"/> from the three-tier cascade
/// (Global → Device → Profile). This is the single object the Dashboard receives
/// from <c>GET /settings/ui/resolved</c> and uses to drive all structural and
/// visual decisions.
/// </para>
/// </summary>
public sealed class ResolvedUISettings
{
    /// <summary>The active device class for this resolution.</summary>
    [JsonPropertyName("device_class")]
    public string DeviceClass { get; set; } = "web";

    // ── Theme ──────────────────────────────────────────────────────────

    [JsonPropertyName("dark_mode")]
    public bool DarkMode { get; set; } = true;

    [JsonPropertyName("accent_color")]
    public string AccentColor { get; set; } = "#8852FC";

    // ── Layout ─────────────────────────────────────────────────────────

    [JsonPropertyName("content_padding")]
    public string ContentPadding { get; set; } = "pa-4";

    [JsonPropertyName("content_max_width")]
    public string ContentMaxWidth { get; set; } = "Full";

    [JsonPropertyName("border_radius")]
    public int BorderRadius { get; set; } = 12;

    // ── Constraints ────────────────────────────────────────────────────

    /// <summary>Device constraints (carried through for component-level checks).</summary>
    [JsonPropertyName("constraints")]
    public UIDeviceConstraints Constraints { get; set; } = new();

    // ── Feature flags ──────────────────────────────────────────────────

    [JsonPropertyName("features")]
    public UIFeatureFlags Features { get; set; } = new();

    // ── Shell ──────────────────────────────────────────────────────────

    [JsonPropertyName("shell")]
    public UIShellSettings Shell { get; set; } = new();

    // ── Pages ──────────────────────────────────────────────────────────

    [JsonPropertyName("pages")]
    public UIPageSettings Pages { get; set; } = new();

    // ── Convenience accessors ──────────────────────────────────────────

    /// <summary>Returns <c>true</c> if the named feature is disabled by device constraints.</summary>
    public bool IsFeatureDisabled(string feature) =>
        Constraints.FeaturesDisabled.Contains(feature, StringComparer.OrdinalIgnoreCase);

    /// <summary>Returns <c>true</c> if the named page is disabled by device constraints.</summary>
    public bool IsPageDisabled(string page) =>
        Constraints.PagesDisabled.Contains(page, StringComparer.OrdinalIgnoreCase);
}
