using System.Globalization;

namespace MediaEngine.Web.Services.Localization;

/// <summary>
/// The languages and regional spellings the Dashboard can show, and the rules for picking one.
/// A household chooses one display language (for example "en-GB" for UK spelling or "fr-CA" for Canadian French);
/// everything in the Dashboard follows it. English with no region means US English, the master copy of every word.
/// </summary>
public static class AppCultures
{
    /// <summary>The language used when nothing else is known: US English, the master copy of every word.</summary>
    public const string Default = "en-US";

    /// <summary>
    /// Every display culture the Dashboard accepts. Plain language codes (such as "fr") are the base wording;
    /// regional tags (such as "fr-CA") only override the words that differ. Missing words fall back along
    /// en-GB to en to the master English copy, so a person never sees a blank or a key.
    /// </summary>
    public static readonly IReadOnlyList<string> Tags =
    [
        "ar", "zh", "zh-TW", "cs", "da", "nl", "en", "en-US", "en-GB", "en-CA", "en-AU", "fi", "fr", "fr-CA", "de",
        "el", "he", "hi", "hu", "id", "it", "ja", "ko", "ms", "no",
        "pl", "pt", "pt-BR", "ro", "ru", "es", "sv", "th", "tr", "uk", "vi",
    ];

    private static readonly Lazy<IReadOnlyList<CultureInfo>> Cultures = new(() => Tags.Select(t => new CultureInfo(t)).ToList());

    /// <summary>The supported cultures as .NET culture objects, for request localization.</summary>
    public static IReadOnlyList<CultureInfo> Supported => Cultures.Value;

    /// <summary>
    /// Turns any language tag into the closest supported tag. "en" and "en-NZ" become "en-US" or the nearest English
    /// region; "fr-BE" becomes "fr"; anything unknown becomes <see cref="Default"/>.
    /// </summary>
    public static string Resolve(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
        {
            return Default;
        }

        var normalized = tag.Trim().Replace('_', '-');
        var exact = Tags.FirstOrDefault(t => string.Equals(t, normalized, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
        {
            return exact == "en" ? Default : exact;
        }

        var language = normalized.Split('-')[0].ToLowerInvariant();
        if (language == "en")
        {
            return Default;
        }

        var baseTag = Tags.FirstOrDefault(t => string.Equals(t, language, StringComparison.OrdinalIgnoreCase));
        return baseTag ?? Default;
    }

    /// <summary>The language part of a tag: "en-GB" gives "en".</summary>
    public static string LanguageOf(string tag) => tag.Split('-', '_')[0].ToLowerInvariant();

    /// <summary>The regional tags offered for a language (for example "en" offers US, UK, Canada and Australia); empty when none.</summary>
    public static IReadOnlyList<string> RegionsFor(string language)
    {
        var lang = LanguageOf(language);
        return Tags.Where(t => t.Contains('-') && string.Equals(LanguageOf(t), lang, StringComparison.OrdinalIgnoreCase)
                               && t != "zh-TW" && t != "pt-BR").ToList();
    }

    /// <summary>The languages shown in a language picker: one entry per language, in the language's own name.</summary>
    public static IReadOnlyList<string> LanguageTags { get; } =
        Tags.Where(t => !t.Contains('-') || t is "zh-TW" or "pt-BR").Where(t => t != "en").Prepend("en-US").ToList();

    /// <summary>
    /// First-time default: the browser's language when it is supported, otherwise US English. Used once, on first setup;
    /// the administrator can change it afterwards.
    /// </summary>
    public static string FirstRunDefault(string? storedTag, CultureInfo? browser)
    {
        var stored = string.IsNullOrWhiteSpace(storedTag) ? null : storedTag.Trim();
        if (stored is not null && !string.Equals(stored, "en", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(stored, "en-US", StringComparison.OrdinalIgnoreCase))
        {
            return Resolve(stored);
        }
        if (browser is not null && !string.IsNullOrEmpty(browser.Name))
        {
            return Resolve(browser.Name);
        }
        return Default;
    }

    /// <summary>The name of a tag in its own language, for pickers.</summary>
    public static string NativeName(string tag)
    {
        var culture = CultureInfo.GetCultureInfo(tag);
        var name = culture.NativeName;
        return name.Length == 0 ? name : char.ToUpper(name[0], culture) + name[1..];
    }
}
