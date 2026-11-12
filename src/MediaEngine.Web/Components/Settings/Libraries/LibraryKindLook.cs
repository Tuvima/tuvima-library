using MediaEngine.Contracts.Settings;

namespace MediaEngine.Web.Components.Settings;

/// <summary>
/// How a library kind looks on the Libraries screens (Library setup D22, D39): whether it looks details up, the colour of its icon
/// tile, and the words of its Metadata icons. Every screen asks here, so a kind a plugin adds needs no screen or style changes.
/// </summary>
public static class LibraryKindLook
{
    /// <summary>The tooltip of the "files as-is" icon a plain kind shows in the Metadata column.</summary>
    public const string NoLookupText = "No online lookup. Files are added as they are.";

    // The one mapping from what a provider can supply (its capabilities) to words a person reads; unknown capabilities are left out.
    private static readonly (string Capability, string Words)[] Supplies =
    [
        ("identity", "matching"),
        ("metadata", "titles and descriptions"),
        ("artwork", "posters and covers"),
        ("ratings", "ratings"),
        ("people", "cast and people"),
        ("relationships", "series and links"),
        ("lyrics", "lyrics"),
        ("subtitles", "subtitles"),
    ];

    /// <summary>True when the kind gets details from at least one provider; a plain kind (no provider) is shown as files as they are.</summary>
    public static bool LooksUp(LibraryTypeOption type) => type.LookupProviders is { Count: > 0 };

    /// <summary>True when the folder window offers Managed for the kind: a kind that looks details up, and plain Videos, whose files Library can file by title (Libraries fixes, item 3c).</summary>
    public static bool ManagedOffered(LibraryTypeOption type) => LooksUp(type) || string.Equals(type.Key, "other_videos", StringComparison.Ordinal);

    // The one colour per kind (setup and libraries fixes D12): the kinds that have a colour of their own in the design system, by kind key.
    // Each class is defined in app.css (lib-kind-color--*), where it sets --lib-kind-color to the design-system token named beside it here.
    private static readonly Dictionary<string, string> OwnColours = new(StringComparer.OrdinalIgnoreCase)
    {
        ["movies"] = "--tl-media-movies",
        ["tv"] = "--tl-media-tv",
        ["music"] = "--tl-media-music",
        ["audiobooks"] = "--tl-media-audiobooks",
        ["books"] = "--tl-media-books",
        ["comics"] = "--tl-media-comics",
        ["documents"] = "--tl-media-document",
    };

    private static readonly HashSet<string> Areas = new(StringComparer.OrdinalIgnoreCase) { "watch", "listen", "read", "view", "vault" };

    /// <summary>
    /// The design-system colour token of a kind: its own (movies, TV, music, audiobooks, books, comics, documents), else its area's, so a kind added
    /// through the kind list needs no change here. Null for an area this Library does not know (the style then falls back to the accent).
    /// </summary>
    public static string? ColorToken(LibraryTypeOption type) =>
        OwnColours.TryGetValue(type.Key, out var own) ? own
        : Areas.Contains(type.Area) ? $"--tl-area-{type.Area.ToLowerInvariant()}"
        : null;

    /// <summary>
    /// The class that gives an element its kind's colour (<c>--lib-kind-color</c>): <c>lib-kind-color--movies</c> for a kind with a colour of its own,
    /// else <c>lib-kind-color--area-watch</c> and so on. The card cell and the icon on the kind's row both use this and nothing else.
    /// </summary>
    public static string ColorClass(LibraryTypeOption type) =>
        OwnColours.ContainsKey(type.Key) ? $"lib-kind-color--{type.Key.ToLowerInvariant()}"
        : Areas.Contains(type.Area) ? $"lib-kind-color--area-{type.Area.ToLowerInvariant()}"
        : "lib-kind-color--none";

    /// <summary>
    /// The one line under a kind's name on its row (setup and libraries fixes D5): what belongs there, then what Library does with it, both from the
    /// kind list in config so a new kind brings its own words.
    /// </summary>
    public static string RowLine(LibraryTypeOption type) =>
        string.Join(" ", new[] { type.Explainer, type.LibraryDoes }.Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value.Trim()));

    /// <summary>What a provider supplies, from its capabilities: "matching, titles and descriptions, posters and covers".</summary>
    public static string SuppliesText(IEnumerable<string> capabilities)
    {
        var held = capabilities.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return string.Join(", ", Supplies.Where(entry => held.Contains(entry.Capability)).Select(entry => entry.Words));
    }

    /// <summary>The tooltip and accessible name of a provider icon: "TMDB: matching, titles and descriptions, ...".</summary>
    public static string ProviderText(string name, IEnumerable<string> capabilities)
    {
        var supplies = SuppliesText(capabilities);
        return supplies.Length == 0 ? name : $"{name}: {supplies}";
    }
}
