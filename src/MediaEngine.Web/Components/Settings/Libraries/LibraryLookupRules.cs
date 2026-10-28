namespace MediaEngine.Web.Components.Settings;

/// <summary>
/// The Lookup tab of a library page (Library pages D15, WP7): which kinds of library have lookup settings of their own, and the fixed words
/// and names of the YouTube key setting. One small place, so the library page and the tab agree and the Vault's own lookup switch joins
/// with the Vault work (WP9) by adding a case here. The key itself is saved by the existing provider credentials actions; this class never
/// holds or returns one.
/// </summary>
public static class LibraryLookupRules
{
    /// <summary>The provider whose credentials hold the optional personal YouTube key (config/providers/youtube.json).</summary>
    public const string YouTubeProvider = "youtube";

    /// <summary>The name of the key among that provider's credentials.</summary>
    public const string YouTubeKeyField = "api_key";

    /// <summary>The key of the YouTube kind of library.</summary>
    public const string YouTubeKindKey = "youtube";

    /// <summary>The Google Cloud console page where a person creates a YouTube Data API key.</summary>
    public const string GoogleKeyConsoleUrl = "https://console.cloud.google.com/apis/credentials";

    /// <summary>The line that says what a lookup sends.</summary>
    public const string PrivacyLine = "Only video IDs are sent to YouTube.";

    /// <summary>The four plain steps to get a personal key.</summary>
    public static readonly IReadOnlyList<string> KeySteps =
    [
        "Sign in to the Google Cloud console with a Google account and create a project. Any name will do.",
        "Open YouTube Data API v3 for that project and choose Enable.",
        "Open Credentials, choose Create credentials, then API key.",
        "Copy the key, paste it below and choose Save. Library checks it before keeping it.",
    ];

    /// <summary>True when a library of this kind has lookup settings, so its page shows a Lookup tab.</summary>
    public static bool HasLookupTab(string? kindKey) =>
        string.Equals(kindKey?.Trim(), YouTubeKindKey, StringComparison.OrdinalIgnoreCase);
}
