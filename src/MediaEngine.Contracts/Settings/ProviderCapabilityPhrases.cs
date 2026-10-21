namespace MediaEngine.Contracts.Settings;

/// <summary>
/// The plain words for what a metadata source supplies ("Movie artwork", "Book matching"), shared by the Engine (the
/// Plugins page, removal warnings, swap comparison) and the Dashboard (first-run setup) so both say the same thing.
/// </summary>
public static class ProviderCapabilityPhrases
{
    /// <summary>The kinds of media a source with no stated media types is taken to work with.</summary>
    public static readonly IReadOnlyList<string> AllMediaTypes = ["Books", "Audiobooks", "Movies", "TV", "Music", "Comics"];

    private static readonly Dictionary<string, string> MediaLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Books"] = "Book",
        ["Audiobooks"] = "Audiobook",
        ["Movies"] = "Movie",
        ["TV"] = "TV",
        ["Music"] = "Music",
        ["Comics"] = "Comic",
        ["Videos"] = "Video",
    };

    private static readonly Dictionary<string, string> CapabilityLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["identity"] = "matching",
        ["metadata"] = "details",
        ["artwork"] = "artwork",
        ["ratings"] = "ratings",
        ["people"] = "cast and crew",
        ["relationships"] = "related titles",
        ["lyrics"] = "lyrics",
        ["subtitles"] = "subtitles",
    };

    /// <summary>One media type in the singular, as a person would say it ("Movies" becomes "Movie").</summary>
    public static string MediaLabel(string mediaType) => MediaLabels.TryGetValue(mediaType, out var label) ? label : mediaType;

    /// <summary>One capability as a plain word ("people" becomes "cast and crew").</summary>
    public static string CapabilityLabel(string capability) => CapabilityLabels.TryGetValue(capability, out var label) ? label : capability;

    /// <summary>The thing a source supplies for one kind of media: "Movie artwork".</summary>
    public static string Phrase(string mediaType, string capability) => $"{MediaLabel(mediaType)} {CapabilityLabel(capability)}";
}
