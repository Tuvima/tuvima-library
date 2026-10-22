using MediaEngine.Contracts.Settings;
using MediaEngine.Web.Services.Formatting;

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

    /// <summary>The classes of a kind's icon tile: the area's colour for a kind that looks details up, a quiet grey for a plain kind.</summary>
    public static string TileClass(LibraryTypeOption type) => LooksUp(type)
        ? $"lib-kind-tile lib-kind-tile--lookup lib-area--{type.Area.ToLowerInvariant()}"
        : "lib-kind-tile";

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

    /// <summary>The tooltip of the amber Unmatched count.</summary>
    public static string UnmatchedText(int count) => count == 1
        ? "1 file couldn't be matched. It stays where it is and is tried again."
        : $"{DisplayFormat.FormatCount(count)} files couldn't be matched. They stay where they are and are tried again.";
}
