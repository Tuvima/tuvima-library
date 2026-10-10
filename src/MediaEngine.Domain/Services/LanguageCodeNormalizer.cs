using System.Globalization;

namespace MediaEngine.Domain.Services;

/// <summary>
/// Normalises the many shapes a language value takes in real libraries
/// ("English", "eng", "en", "en-US", "pt_BR", "Français") to an ISO 639-1
/// two-letter code. Used wherever a language value is sent to a provider or
/// compared against another language value.
/// </summary>
public static class LanguageCodeNormalizer
{
    // ISO 639-2/B bibliographic codes that differ from the ISO 639-2/T codes
    // exposed by CultureInfo.ThreeLetterISOLanguageName.
    private static readonly (string Code, string TwoLetter)[] BibliographicCodes =
    [
        ("fre", "fr"), ("ger", "de"), ("chi", "zh"), ("dut", "nl"), ("cze", "cs"),
        ("gre", "el"), ("per", "fa"), ("rum", "ro"), ("slo", "sk"), ("wel", "cy"),
        ("arm", "hy"), ("baq", "eu"), ("bur", "my"), ("geo", "ka"), ("ice", "is"),
        ("mac", "mk"), ("mao", "mi"), ("may", "ms"), ("alb", "sq"), ("tib", "bo"),
    ];

    private static readonly Lazy<IReadOnlyDictionary<string, string>> Lookup = new(BuildLookup);

    /// <summary>
    /// Returns the ISO 639-1 two-letter code for <paramref name="value"/>, or
    /// <c>null</c> when the value is blank or not a recognised language.
    /// </summary>
    public static string? ToIso6391(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        var lookup = Lookup.Value;

        // Whole-value match first (language names, 3-letter codes, "zh-Hans").
        if (lookup.TryGetValue(trimmed.ToLowerInvariant(), out var direct))
        {
            return direct;
        }

        // Culture-style values: "en-US", "pt_BR". Use only the language subtag.
        var separator = trimmed.IndexOfAny(['-', '_']);
        if (separator > 0
            && lookup.TryGetValue(trimmed[..separator].ToLowerInvariant(), out var language))
        {
            return language;
        }

        return null;
    }

    /// <summary>
    /// Normalises each value to its ISO 639-1 code (keeping the trimmed original when it is not a
    /// recognised language), drops blanks, and removes duplicates while preserving first-seen order.
    /// </summary>
    public static IReadOnlyList<string> NormalizeDistinct(IEnumerable<string?> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            var normalized = ToIso6391(value) ?? value.Trim();
            if (seen.Add(normalized))
            {
                result.Add(normalized);
            }
        }

        return result;
    }

    private static IReadOnlyDictionary<string, string> BuildLookup()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);

        var cultures = CultureInfo.GetCultures(CultureTypes.NeutralCultures)
            .Where(culture => !string.IsNullOrEmpty(culture.Name)
                && culture.TwoLetterISOLanguageName.Length == 2
                && !string.Equals(culture.TwoLetterISOLanguageName, "iv", StringComparison.OrdinalIgnoreCase))
            .ToList();

        // Pass order matters: codes win over names when two cultures collide.
        foreach (var culture in cultures)
        {
            map.TryAdd(culture.TwoLetterISOLanguageName.ToLowerInvariant(), culture.TwoLetterISOLanguageName.ToLowerInvariant());
        }

        foreach (var culture in cultures)
        {
            var two = culture.TwoLetterISOLanguageName.ToLowerInvariant();
            if (culture.ThreeLetterISOLanguageName.Length == 3)
            {
                map.TryAdd(culture.ThreeLetterISOLanguageName.ToLowerInvariant(), two);
            }
        }

        foreach (var (code, twoLetter) in BibliographicCodes)
        {
            map.TryAdd(code, twoLetter);
        }

        foreach (var culture in cultures)
        {
            var two = culture.TwoLetterISOLanguageName.ToLowerInvariant();
            AddName(map, culture.EnglishName, two);
            AddName(map, culture.NativeName, two);
            AddName(map, culture.Name, two);
        }

        return map;
    }

    private static void AddName(Dictionary<string, string> map, string name, string twoLetter)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        var key = name.Trim().ToLowerInvariant();
        map.TryAdd(key, twoLetter);

        // "Portuguese (Brazil)" / "português (Brasil)" style names: also accept the leading word(s).
        var paren = key.IndexOf(" (", StringComparison.Ordinal);
        if (paren > 0)
        {
            map.TryAdd(key[..paren], twoLetter);
        }
    }
}
