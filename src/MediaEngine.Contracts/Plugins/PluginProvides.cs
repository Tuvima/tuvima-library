namespace MediaEngine.Contracts.Plugins;

/// <summary>
/// The fixed list of things a plugin can provide to Library (Plugins and Integrations screens v4 D34). A plugin's file names which
/// ones it provides; the Dashboard never composes the sentence from field names, so every plugin reads alike.
/// </summary>
public static class PluginProvides
{
    public const string Identification = "Identification";
    public const string Details = "Details";
    public const string Artwork = "Artwork";
    public const string People = "People";
    public const string SeriesOrder = "Series order";
    public const string Lyrics = "Lyrics";
    public const string Subtitles = "Subtitles";
    public const string SkipMarkers = "Skip markers";
    public const string Insights = "Insights";
    public const string ListsAndRatings = "Lists & ratings";
    public const string AccountSync = "Account sync";

    /// <summary>Every allowed word, in the order the tile and page show them.</summary>
    public static readonly IReadOnlyList<string> All =
    [
        Identification, Details, Artwork, People, SeriesOrder, Lyrics, Subtitles, SkipMarkers, Insights, ListsAndRatings, AccountSync,
    ];

    /// <summary>The plugin's words, known ones only, de-duplicated, in the standard order.</summary>
    public static IReadOnlyList<string> Resolve(IEnumerable<string>? provides)
    {
        var wanted = new HashSet<string>(provides ?? [], StringComparer.OrdinalIgnoreCase);
        return All.Where(wanted.Contains).ToList();
    }
}
