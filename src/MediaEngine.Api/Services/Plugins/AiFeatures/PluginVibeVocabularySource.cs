using System.Text.Json;
using MediaEngine.AI.Configuration;
using MediaEngine.AI.Infrastructure;

namespace MediaEngine.Api.Services.Plugins.AiFeatures;

/// <summary>
/// The words a vibe tag may be, read from the Description insights plugin's settings. A list the plugin does not have, or one that
/// is empty or not a list of words, falls back to the built-in list for that kind of media so tagging never runs without words.
/// </summary>
internal sealed class PluginVibeVocabularySource(IServiceProvider services) : IVibeVocabularySource
{
    private PluginCatalog? _catalog;

    private PluginCatalog Catalog => _catalog ??= services.GetRequiredService<PluginCatalog>();

    public AiVibeVocabulary Current
    {
        get
        {
            var defaults = new AiVibeVocabulary();
            var settings = Catalog.Get(AiFeaturePluginIds.DescriptionInsights)?.Settings;
            if (settings is null)
            {
                return defaults;
            }

            return new AiVibeVocabulary
            {
                Books = Words(settings, DescriptionInsightsPlugin.BooksKey) ?? defaults.Books,
                MoviesTv = Words(settings, DescriptionInsightsPlugin.MoviesTvKey) ?? defaults.MoviesTv,
                Music = Words(settings, DescriptionInsightsPlugin.MusicKey) ?? defaults.Music,
                Comics = Words(settings, DescriptionInsightsPlugin.ComicsKey) ?? defaults.Comics,
            };
        }
    }

    private static List<string>? Words(IReadOnlyDictionary<string, JsonElement> settings, string key)
    {
        if (!settings.TryGetValue(key, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var words = value.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString()!.Trim())
            .Where(word => word.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return words.Count == 0 ? null : words;
    }
}
