using System.Text.Json.Serialization;

namespace MediaEngine.Domain.Configuration;

/// <summary>
/// Configuration for the app shell — the AppBar and Intent Dock that wrap every page.
///
/// <para>
/// Styles control visual density and sizing. The Intent Dock item list controls which
/// browsing intents are available on the floating dock.
/// </para>
/// </summary>
public sealed class UIShellSettings
{
    /// <summary>
    /// AppBar visual density: <c>full</c> (desktop), <c>compact</c> (mobile),
    /// <c>oversized</c> (television), <c>minimal</c> (automotive).
    /// </summary>
    [JsonPropertyName("appbar_style")]
    public string AppBarStyle { get; set; } = "full";

    /// <summary>
    /// Logo variant displayed in the AppBar centre: <c>wordmark</c> (full logo + text),
    /// <c>icon</c> (mark only), <c>wordmark-large</c> (TV), <c>icon-large</c> (automotive).
    /// </summary>
    [JsonPropertyName("logo_variant")]
    public string LogoVariant { get; set; } = "wordmark";

    /// <summary>
    /// Tab names displayed on the TV dock. Default: Home, Libraries, Search, Downloads and Settings. Read, Watch, Listen, View and Collections remain valid names.
    /// The phone dock does not read this list: it is always Home, Search and My Library.
    /// Automotive restricts this to <c>["Collections", "Listen"]</c>.
    /// </summary>
    [JsonPropertyName("intent_dock_items")]
    public List<string> IntentDockItems { get; set; } = ["Home", "Libraries", "Search", "Downloads", "Settings"];

    /// <summary>
    /// Intent Dock sizing: <c>normal</c> (standard) or <c>oversized</c> (TV/automotive).
    /// </summary>
    [JsonPropertyName("intent_dock_style")]
    public string IntentDockStyle { get; set; } = "normal";
}
