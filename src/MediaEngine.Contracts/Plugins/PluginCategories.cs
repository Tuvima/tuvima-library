namespace MediaEngine.Contracts.Plugins;

/// <summary>
/// The tabs and media filters of the Plugins page. A plugin names its own category and media types in its description;
/// anything missing or unknown falls back to Utilities and "all media" so a hand-added plugin is still listed.
/// </summary>
public static class PluginCategories
{
    public const string Metadata = "metadata";
    public const string MediaProcessing = "media-processing";
    public const string Services = "services";
    public const string Utilities = "utilities";

    private static readonly string[] KnownCategories = [Metadata, MediaProcessing, Services, Utilities];
    private static readonly string[] KnownMediaTypes = ["books", "comics", "audiobooks", "movies", "tv", "music", "photos"];

    /// <summary>The plugin's category word, always one of the four known ones.</summary>
    public static string Resolve(string? category)
    {
        var word = category?.Trim().ToLowerInvariant();
        return KnownCategories.Contains(word) ? word! : Utilities;
    }

    /// <summary>The plugin's media types, known ones only, de-duplicated; empty means every type.</summary>
    public static IReadOnlyList<string> ResolveMediaTypes(IEnumerable<string>? mediaTypes) =>
        (mediaTypes ?? [])
            .Select(value => value?.Trim().ToLowerInvariant())
            .Where(value => value is not null && KnownMediaTypes.Contains(value))
            .Select(value => value!)
            .Distinct()
            .ToList();
}
