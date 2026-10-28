using MediaEngine.Contracts.Settings;

namespace MediaEngine.Web.Components.Settings;

/// <summary>
/// The Lookup tab of a library page (Library pages D15, WP7): which kinds of library have lookup settings of their own, and the fixed words
/// and names of the YouTube key setting, and the Vault case (WP9): a Vault library of an ordinary kind has the per-library "Look up details online"
/// switch (Vault sections D34), off by default. One small place, so the library page and the tab agree. The key itself is saved by the existing
/// provider credentials actions; this class never holds or returns one.
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

    /// <summary>The kind whose Vault library looks details up on a Stash-box server, not online (the Vault's own pages hold that setting).</summary>
    public const string AdultContentKindKey = "adult_content";

    /// <summary>
    /// The note shown beside the Vault library's online lookup switch. Online lookup for the Vault is not running yet (it arrives with a later update, which is
    /// when titles would be sent to online services), so the note says exactly that: the choice is kept, and nothing is sent in the meantime.
    /// </summary>
    public const string VaultPrivacyNote = "Online lookup for this section turns on with a later update. Your choice is kept, and nothing is sent to online services until then.";

    /// <summary>What the switch says while it is off.</summary>
    public const string VaultOffNote = "Off: nothing about this library leaves this machine.";

    /// <summary>True when a library of this kind has lookup settings, so its page shows a Lookup tab.</summary>
    public static bool HasLookupTab(string? kindKey) =>
        string.Equals(kindKey?.Trim(), YouTubeKindKey, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True when a library page shows a Lookup tab: the YouTube kind, or a Vault library of an ordinary kind that looks details up at all
    /// (not YouTube, which never joins the Vault, and not the adult kind, whose own source is Stash-box).
    /// </summary>
    public static bool HasLookupTab(LibraryFolderDto library, LibraryTypeOption? kind) =>
        HasLookupTab(library.KindKey) || HasVaultSwitch(library, kind);

    /// <summary>True for a Vault library whose content type looks details up online, so the per-library switch applies.</summary>
    public static bool HasVaultSwitch(LibraryFolderDto library, LibraryTypeOption? kind) =>
        IsVaultLibrary(library)
        && kind is { LookupProviders.Count: > 0 }
        && !string.Equals(kind.Key, YouTubeKindKey, StringComparison.OrdinalIgnoreCase)
        && !string.Equals(kind.Key, AdultContentKindKey, StringComparison.OrdinalIgnoreCase);

    /// <summary>True when the library is Vault-protected.</summary>
    public static bool IsVaultLibrary(LibraryFolderDto library) =>
        string.Equals(library.Access?.Trim(), LibraryListing.Vault, StringComparison.OrdinalIgnoreCase);

    /// <summary>True when the Vault library's online lookup is switched on (its metadata policy is not local-only).</summary>
    public static bool VaultLookupIsOn(LibraryFolderDto library) =>
        !string.Equals(library.MetadataPolicy, "local_only", StringComparison.OrdinalIgnoreCase)
        && !string.Equals(library.MetadataPolicy, "manual", StringComparison.OrdinalIgnoreCase);
}
