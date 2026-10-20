using System.Text.RegularExpressions;

namespace MediaEngine.Providers.Models;

/// <summary>
/// LibraryItem of named value-transform functions used by both the Wikidata SPARQL
/// adapter and the config-driven universal adapter.
///
/// <para>
/// Transform functions are <b>behaviour</b> — they live in code. Which property
/// uses which transform is <b>data</b> — it lives in the configuration.
/// This separation means new transforms require a code change (rare), but
/// reassigning transforms to different properties is a configuration edit.
/// </para>
///
/// <para>
/// Two overloads: <see cref="Apply(string?, string)"/> for simple transforms
/// (used by Wikidata), and <see cref="Apply(string?, string, string?)"/> for
/// parameterised transforms (used by config-driven adapters).
/// </para>
/// </summary>
public static partial class ValueTransformCatalog
{
    // ── Simple transforms (no args) ──────────────────────────────────────────

    private static readonly IReadOnlyDictionary<string, Func<string, string?>> Transforms =
        new Dictionary<string, Func<string, string?>>(StringComparer.OrdinalIgnoreCase)
        {
            // ── Wikidata transforms (existing) ───────────────────────────────

            // Extract 4-digit year from ISO date (e.g. "1965-06-01T00:00:00Z" → "1965")
            ["year_from_iso"] = raw => raw.Length >= 4 ? raw[..4] : raw,

            // Extract the numeric portion from an ordinal string (e.g. "Book 3" → "3")
            ["numeric_portion"] = ExtractNumericPortion,

            // Strip Wikidata entity URI prefix
            ["strip_entity_uri"] = raw =>
                raw.StartsWith("http://www.wikidata.org/entity/", StringComparison.Ordinal)
                    ? raw[(raw.LastIndexOf('/') + 1)..]
                    : raw,

            // Convert Wikimedia Commons filename to a thumbnail URL
            ["commons_url"] = raw =>
            {
                var commonsName = Uri.EscapeDataString(raw.Replace(' ', '_'));
                return $"https://commons.wikimedia.org/wiki/Special:FilePath/{commonsName}?width=300";
            },

            // Extract plain numeric value from a Wikidata quantity string (e.g. "+142" → "142")
            ["duration_from_quantity"] = value =>
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    return null;
                }

                var numeric = new string(value.TrimStart('+').TakeWhile(c => char.IsDigit(c) || c == '.').ToArray());
                return string.IsNullOrEmpty(numeric) ? null : numeric;
            },

            // Preserve date precision from Wikidata ISO dates.
            // Wikidata year-precision dates use -01-01T00:00:00Z as a sentinel.
            // Returns bare year ("1965") for year-precision, full date ("1965-06-15") otherwise.
            ["date_with_precision"] = value =>
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    return null;
                }
                // Already a bare year
                if (value.Length == 4 && value.All(char.IsDigit))
                {
                    return value;
                }
                // Parse ISO date — Wikidata convention: year-precision dates use -01-01T00:00:00Z
                if (value.Length >= 10)
                {
                    var monthDay = value.Substring(5, 5); // "MM-DD"
                    if (monthDay == "01-01")
                    {
                        return value.Substring(0, 4); // Year only
                    }

                    return value.Substring(0, 10); // Full date YYYY-MM-DD
                }
                return value;
            },

            // ── Config-driven transforms (new) ──────────────────────────────

            // Convert each word's first letter to uppercase (title case).
            // Preserves all-caps acronyms (e.g. "BBC", "USA").
            // Handles hyphenated words (e.g. "lord-of-the-rings" → "Lord-Of-The-Rings").
            ["title_case"] = raw =>
            {
                if (string.IsNullOrWhiteSpace(raw))
                {
                    return raw;
                }

                static string CapitalizeSegment(string segment)
                {
                    if (segment.Length == 0)
                    {
                        return segment;
                    }
                    // Preserve all-caps acronyms (2+ chars, all uppercase letters)
                    if (segment.Length > 1 && segment.All(c => char.IsLetter(c) && char.IsUpper(c)))
                    {
                        return segment;
                    }

                    return char.ToUpperInvariant(segment[0]) + segment[1..];
                }

                var words = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                for (int i = 0; i < words.Length; i++)
                {
                    var word = words[i];
                    // Handle hyphenated words: capitalize each hyphen-separated part
                    if (word.Contains('-'))
                    {
                        var parts = word.Split('-');
                        for (int j = 0; j < parts.Length; j++)
                        {
                            parts[j] = CapitalizeSegment(parts[j]);
                        }

                        words[i] = string.Join('-', parts);
                    }
                    else
                    {
                        words[i] = CapitalizeSegment(word);
                    }
                }
                return string.Join(' ', words);
            },

            // Pass value through unchanged (useful for int→string coercion in JSON)
            ["to_string"] = raw => raw,

            // First plausible four-digit year (1900-2099) in a longer text, e.g. "Sept 2012" -> "2012".
            ["first_year"] = raw => MediaEngine.Providers.Adapters.ResultTextRules.ExtractYear(raw)?.ToString(System.Globalization.CultureInfo.InvariantCulture),

            // Normalize provider durations expressed in milliseconds to the
            // canonical duration field's minutes contract.
            ["milliseconds_to_minutes"] = raw =>
                double.TryParse(raw, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var milliseconds)
                    && milliseconds >= 0
                        ? (milliseconds / 60000d).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)
                        : null,

            // Remove all HTML tags and decode HTML entities (e.g. &amp; → &, &#39; → ')
            // Block-level tags (br, p, div) are converted to newlines before stripping
            // so paragraph structure survives in plain-text descriptions.
            ["strip_html"] = raw => System.Net.WebUtility.HtmlDecode(
                HtmlTagRegex().Replace(
                    BlockTagRegex().Replace(raw, "\n"),
                    string.Empty).Trim()),

            // Sanitize HTML by preserving a safe allow-list of tags and stripping the rest.
            // Allowed tags are a fixed security policy: b, i, em, strong, p, br.
            // HTML entities are decoded after stripping. <br> variants are normalised to <br />.
            ["sanitize_html"] = raw =>
            {
                if (string.IsNullOrWhiteSpace(raw))
                {
                    return raw;
                }

                // Replace non-allowed tags with empty string; keep allowed ones intact.
                var result = Regex.Replace(raw, @"</?[^>]+>", match =>
                    Regex.IsMatch(match.Value, @"</?(?:b|i|em|strong|p|br)\s*/?>",
                        RegexOptions.IgnoreCase)
                        ? match.Value
                        : string.Empty);

                // Decode HTML entities (&amp; → &, &lt; → <, &#39; → ', etc.)
                result = System.Net.WebUtility.HtmlDecode(result);

                // Normalise <br> and <br/> → <br />
                result = Regex.Replace(result, @"<br\s*/?>", "<br />", RegexOptions.IgnoreCase);

                return result.Trim();
            },

            // Join array elements with ", " (default). For JsonArray values, see Apply(name, raw, args).
            ["array_join"] = raw => raw,
        };

    // ── Parameterised transforms (with args) ─────────────────────────────────

    private static readonly IReadOnlyDictionary<string, Func<string, string?, string?>> ArgsTransforms =
        new Dictionary<string, Func<string, string?, string?>>(StringComparer.OrdinalIgnoreCase)
        {
            // Plug value into a URL template (e.g. "https://covers.openlibrary.org/b/id/{value}-L.jpg")
            ["url_template"] = (raw, args) =>
                !string.IsNullOrEmpty(args)
                    ? args.Replace("{value}", raw, StringComparison.Ordinal)
                    : raw,

            // Emit a constant value when the source JSON path exists.
            ["literal"] = (_, args) => args,

            // Regex find/replace. Args format: "pattern|replacement" (pipe-delimited)
            ["regex_replace"] = (raw, args) =>
            {
                if (string.IsNullOrEmpty(args))
                {
                    return raw;
                }

                var parts = args.Split('|', 2);
                return parts.Length == 2
                    ? Regex.Replace(raw, parts[0], parts[1])
                    : raw;
            },

            // From a JSON array string representation, prefer 13-char element (ISBN-13)
            ["prefer_isbn13"] = (raw, _) => raw, // Handled specially in ConfigDrivenAdapter

            // Join array elements with a custom separator (args = separator, default ", ")
            ["array_join"] = (raw, args) => raw, // Handled specially in ConfigDrivenAdapter

            // From array of objects, extract named field and join (args = field name)
            ["array_nested_join"] = (raw, args) => raw, // Handled specially in ConfigDrivenAdapter

            // Extract first N characters (args = N as string)
            ["first_n_chars"] = (raw, args) =>
            {
                if (string.IsNullOrEmpty(args) || !int.TryParse(args, out var n) || n <= 0)
                {
                    return raw;
                }

                return raw.Length > n ? raw[..n] : raw;
            },

            // Navigate alternative JSON path if primary was null (args = alt key)
            ["fallback_key"] = (raw, _) => raw, // Handled specially in ConfigDrivenAdapter
        };

    /// <summary>
    /// Apply a named transform to a raw value (no args).
    /// Returns the raw value unchanged if <paramref name="transformName"/> is <c>null</c>
    /// or not found in the libraryItem.
    /// </summary>
    public static string? Apply(string? transformName, string rawValue)
    {
        if (string.IsNullOrEmpty(transformName))
        {
            return rawValue;
        }

        return Transforms.TryGetValue(transformName, out var fn)
            ? fn(rawValue)
            : rawValue;
    }

    /// <summary>
    /// Apply a named transform to a raw value with optional arguments.
    /// Tries the parameterised libraryItem first, then falls back to the simple libraryItem.
    /// </summary>
    public static string? Apply(string? transformName, string rawValue, string? args)
    {
        if (string.IsNullOrEmpty(transformName))
        {
            return rawValue;
        }

        // Try parameterised transforms first.
        if (ArgsTransforms.TryGetValue(transformName, out var argsFn))
        {
            return argsFn(rawValue, args);
        }

        // Fall back to simple transforms.
        if (Transforms.TryGetValue(transformName, out var fn))
        {
            return fn(rawValue);
        }

        return rawValue;
    }

    /// <summary>
    /// Check whether a transform name is recognised by the libraryItem.
    /// </summary>
    public static bool IsKnown(string? transformName)
    {
        if (string.IsNullOrEmpty(transformName))
        {
            return false;
        }

        return Transforms.ContainsKey(transformName) || ArgsTransforms.ContainsKey(transformName);
    }

    // ── Private Helpers ──────────────────────────────────────────────────────

    /// <summary>Extract the first contiguous numeric portion (with optional decimal point).</summary>
    private static string? ExtractNumericPortion(string value)
    {
        var start = -1;
        for (var i = 0; i < value.Length; i++)
        {
            if (char.IsDigit(value[i]))
            {
                if (start < 0)
                {
                    start = i;
                }
            }
            else if (value[i] == '.' && start >= 0)
            {
                // Allow decimal within number
            }
            else if (start >= 0)
            {
                return value[start..i];
            }
        }

        return start >= 0 ? value[start..] : value;
    }

    [GeneratedRegex("<[^>]+>", RegexOptions.Compiled)]
    private static partial Regex HtmlTagRegex();

    /// <summary>Matches block-level HTML tags that should become newlines in plain text.</summary>
    [GeneratedRegex(@"<br\s*/?>\s*|</p>\s*|</div>\s*|<p[^>]*>\s*|<div[^>]*>\s*", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex BlockTagRegex();
}
