namespace MediaEngine.Contracts.Plugins;

/// <summary>
/// The one-sentence description every plugin tile and row shows (Plugins and Integrations screens v4 D35): at most 90 characters,
/// starting with what the plugin does. The longer paragraph is the tooltip and the About card.
/// </summary>
public static class PluginDescriptions
{
    /// <summary>The most characters a tile description may have.</summary>
    public const int ShortLimit = 90;

    /// <summary>
    /// The plugin's own short sentence when it wrote one; otherwise the first sentence of its long description, cut at a word
    /// boundary with an ellipsis if it is still over the limit.
    /// </summary>
    public static string Short(string? shortDescription, string? longDescription)
    {
        var text = string.IsNullOrWhiteSpace(shortDescription) ? FirstSentence(longDescription) : shortDescription.Trim();
        if (text.Length <= ShortLimit)
        {
            return text;
        }

        var cut = text.LastIndexOf(' ', ShortLimit - 1);
        return text[..(cut > 20 ? cut : ShortLimit - 1)].TrimEnd(' ', ',', ';', ':', '.') + "…";
    }

    private static string FirstSentence(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "";
        }

        var trimmed = text.Trim();
        var end = trimmed.IndexOf(". ", StringComparison.Ordinal);
        return end < 0 ? trimmed : trimmed[..(end + 1)];
    }
}
