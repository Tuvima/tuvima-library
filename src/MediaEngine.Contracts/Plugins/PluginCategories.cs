namespace MediaEngine.Contracts.Plugins;

/// <summary>
/// The tabs and media filters of the Plugins page. A plugin names its own category and media types in its description;
/// anything missing or unknown falls back to "enrichment" and "all media" so a hand-added plugin is still listed.
/// The three categories are named for when Library uses the plugin (Plugins and Integrations screens v4 D31): <c>intake</c>
/// (while identifying files), <c>enrichment</c> (adds to items already imported) and <c>accounts</c> (connects a person's outside account).
/// </summary>
public static class PluginCategories
{
    public const string Intake = "intake";
    public const string Enrichment = "enrichment";
    public const string Accounts = "accounts";

    private static readonly string[] KnownCategories = [Intake, Enrichment, Accounts];
    private static readonly string[] KnownMediaTypes = ["books", "comics", "audiobooks", "movies", "tv", "music", "photos"];

    /// <summary>
    /// The plugin's category word, always one of the three known ones. The old words map once (metadata to intake;
    /// media-processing and utilities to enrichment; services to accounts); anything unknown is enrichment.
    /// </summary>
    public static string Resolve(string? category)
    {
        var word = category?.Trim().ToLowerInvariant();
        return word switch
        {
            "metadata" => Intake,
            "media-processing" or "utilities" => Enrichment,
            "services" => Accounts,
            _ => KnownCategories.Contains(word) ? word! : Enrichment,
        };
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
