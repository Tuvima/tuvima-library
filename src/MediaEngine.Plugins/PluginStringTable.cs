using System.Text.Json;

namespace MediaEngine.Plugins;

/// <summary>
/// A plugin's own words, read from <c>strings/&lt;tag&gt;.json</c> files (a flat object of key to text) in its folder.
/// Lookup follows the server display language: the full tag (<c>fr-CA</c>), then the language (<c>fr</c>), then English.
/// A missing key shows the English text, and a key missing everywhere shows the key itself, so a screen is never blank.
/// </summary>
public sealed class PluginStringTable
{
    private readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> _byTag;

    public PluginStringTable(IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> byTag) => _byTag = byTag;

    /// <summary>Reads every <c>strings/*.json</c> file the manifest lists in <see cref="PluginManifest.SupportedLanguages"/> (and English).</summary>
    public static PluginStringTable Load(PluginManifest manifest, string pluginFolder)
    {
        var tags = manifest.SupportedLanguages.Append("en").Distinct(StringComparer.OrdinalIgnoreCase);
        var tables = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var tag in tags)
        {
            if (tag.IndexOfAny(['/', '\\', '.']) >= 0)
            {
                continue; // a tag is never a path
            }
            var file = Path.Combine(pluginFolder, "strings", tag + ".json");
            if (!File.Exists(file))
            {
                continue;
            }
            try
            {
                var map = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(file));
                if (map is not null)
                {
                    tables[tag] = new Dictionary<string, string>(map, StringComparer.Ordinal);
                }
            }
            catch (JsonException)
            {
                // A broken translation file is skipped; the plugin falls back to English rather than failing to load.
            }
        }
        return new PluginStringTable(tables);
    }

    public string Get(string key, string cultureTag)
    {
        foreach (var tag in Chain(cultureTag))
        {
            if (_byTag.TryGetValue(tag, out var table) && table.TryGetValue(key, out var text))
            {
                return text;
            }
        }
        return key;
    }

    public string Get(string key, string cultureTag, params object[] args) =>
        string.Format(System.Globalization.CultureInfo.InvariantCulture, Get(key, cultureTag), args);

    private static IEnumerable<string> Chain(string cultureTag)
    {
        var tag = string.IsNullOrWhiteSpace(cultureTag) ? "en" : cultureTag.Trim();
        yield return tag;
        var dash = tag.IndexOf('-');
        if (dash > 0)
        {
            yield return tag[..dash];
        }
        yield return "en";
    }
}
