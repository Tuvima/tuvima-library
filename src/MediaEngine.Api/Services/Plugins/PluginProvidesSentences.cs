using MediaEngine.Contracts.Plugins;

namespace MediaEngine.Api.Services.Plugins;

/// <summary>
/// The one plain sentence for each thing a plugin can provide (spec plugins-and-integrations-screens-v5 D53). It lives here once, so every
/// plugin reads alike; the Dashboard receives it through the plugin summary and never writes its own.
/// </summary>
public static class PluginProvidesSentences
{
    private static readonly IReadOnlyDictionary<string, string> Sentences = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        [PluginProvides.Identification] = "Helps Library recognise what a file is.",
        [PluginProvides.Details] = "Fills in descriptions, dates, genres and other facts.",
        [PluginProvides.Artwork] = "Supplies covers, posters and backgrounds.",
        [PluginProvides.People] = "Adds the people behind it, such as cast, authors and artists.",
        [PluginProvides.SeriesOrder] = "Puts the parts of a Series in reading or watching order.",
        [PluginProvides.Lyrics] = "Brings in song lyrics.",
        [PluginProvides.Subtitles] = "Finds subtitles to show while you watch.",
        [PluginProvides.SkipMarkers] = "Marks intros and credits so you can skip them.",
        [PluginProvides.Insights] = "Adds extra context and recommendations.",
        [PluginProvides.ListsAndRatings] = "Brings in lists and ratings kept elsewhere.",
        [PluginProvides.AccountSync] = "Keeps your watching and listening in step with an online account.",
    };

    /// <summary>The sentence for one kind, or an empty string for a word that is not on the fixed list.</summary>
    public static string For(string kind) => Sentences.TryGetValue(kind, out var sentence) ? sentence : "";

    /// <summary>The cards for a plugin's words (known kinds only, standard order).</summary>
    public static List<PluginProvidesCardDto> Cards(IEnumerable<string>? provides) =>
        PluginProvides.Resolve(provides).Select(kind => new PluginProvidesCardDto { Kind = kind, Sentence = For(kind) }).ToList();
}
