using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace MediaEngine.Providers.Services.Vault;

/// <summary>
/// The checks every field of a stash-box record passes before it is saved (Videos spec D30): a sensible length, a date in a sensible
/// range, markup and links stripped, and the language of the text noted. A record's text is untrusted: it may carry HTML, links,
/// hidden direction-changing characters or control characters, and none of that is stored.
/// </summary>
public static class VaultFieldValidator
{
    public const int MaxTitleLength = 300;
    public const int MaxDescriptionLength = 4000;
    public const int MaxNameLength = 200;
    public const int MaxTagLength = 100;
    public const int MaxNames = 50;

    private static readonly TimeSpan RegexLimit = TimeSpan.FromMilliseconds(250);

    private static readonly Regex Tags = new(@"<[^>]{0,1000}>", RegexOptions.CultureInvariant, RegexLimit);
    private static readonly Regex MarkdownLink = new(@"!?\[(?<text>[^\]\r\n]{0,300})\]\((?<url>[^)\r\n]{0,2000})\)", RegexOptions.CultureInvariant, RegexLimit);
    private static readonly Regex Links = new(@"(?:(?:https?|ftp|file|data|javascript)://|www\.)\S+", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, RegexLimit);
    private static readonly Regex Spaces = new(@"[ \t]+", RegexOptions.CultureInvariant, RegexLimit);
    private static readonly Regex BlankLines = new(@"\n{3,}", RegexOptions.CultureInvariant, RegexLimit);

    /// <summary>A release or shoot date from 1900 up to a year from today. Anything else is not stored.</summary>
    public static bool IsSensibleDate(DateOnly date) => date.Year >= 1900 && date <= DateOnly.FromDateTime(DateTime.UtcNow).AddYears(1);

    /// <summary>A video between one second and two days. Anything else is treated as not known.</summary>
    public static bool IsSensibleDuration(double? seconds) => seconds is >= 1 and <= 48 * 3600;

    public static string? Title(string? value) => Text(value, MaxTitleLength, keepNewLines: false);

    public static string? Description(string? value) => Text(value, MaxDescriptionLength, keepNewLines: true);

    public static string? Name(string? value, int maxLength = MaxNameLength) => Text(value, maxLength, keepNewLines: false);

    /// <summary>Cleans a list of names: each checked, repeats dropped (ignoring case), at most <see cref="MaxNames"/>.</summary>
    public static IReadOnlyList<string> Names(IEnumerable<string> values, int maxLength = MaxNameLength)
    {
        var result = new List<string>();
        foreach (var value in values)
        {
            if (Name(value, maxLength) is { } name && !result.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                result.Add(name);
                if (result.Count >= MaxNames)
                {
                    break;
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Strips markup and links and hidden or control characters, collapses spaces, and cuts to a length. A value with nothing left,
    /// or that was only markup or a link, is null.
    /// </summary>
    public static string? Text(string? value, int maxLength, bool keepNewLines)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        try
        {
            // Entities are decoded first and markup removed after, repeated, so "&lt;b&gt;" cannot turn into a tag that survives.
            var text = value.Length > 20000 ? value[..20000] : value;
            for (var pass = 0; pass < 3; pass++)
            {
                var decoded = WebUtility.HtmlDecode(text);
                var stripped = Tags.Replace(decoded, " ");
                if (stripped == text)
                {
                    break;
                }

                text = stripped;
            }

            text = MarkdownLink.Replace(text, "${text}");
            text = Links.Replace(text, " ");
            text = text.Replace('<', ' ').Replace('>', ' ');

            var builder = new StringBuilder(Math.Min(text.Length, maxLength + 16));
            foreach (var c in text.Normalize(NormalizationForm.FormC))
            {
                var category = char.GetUnicodeCategory(c);
                if (c == '\n' || c == '\r')
                {
                    builder.Append(keepNewLines ? '\n' : ' ');
                }
                else if (c == '\t')
                {
                    builder.Append(' ');
                }
                else if (char.IsControl(c) || category is UnicodeCategory.Format or UnicodeCategory.PrivateUse or UnicodeCategory.OtherNotAssigned
                    or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator)
                {
                    // Hidden characters (zero-width, direction overrides) are dropped.
                }
                else
                {
                    builder.Append(c);
                }
            }

            var cleaned = Spaces.Replace(builder.ToString(), " ");
            cleaned = Regex.Replace(cleaned, @" ?\n ?", "\n", RegexOptions.CultureInvariant, RegexLimit);
            cleaned = keepNewLines ? BlankLines.Replace(cleaned, "\n\n") : cleaned.Replace('\n', ' ');
            cleaned = cleaned.Trim();
            if (cleaned.Length > maxLength)
            {
                cleaned = cleaned[..maxLength].TrimEnd();
            }

            return cleaned.Length == 0 ? null : cleaned;
        }
        catch (RegexMatchTimeoutException)
        {
            // Text built to make a check run too long is not stored.
            return null;
        }
    }

    /// <summary>
    /// A short note of the language of some text, read from its writing system: "ja" (kana), "ko" (hangul), "zh" (ideographs only),
    /// "ru" (Cyrillic), "ar", "he", "th", "el", otherwise "und-Latn" (Latin script, language not known). Null for no letters.
    /// </summary>
    public static string? NoteLanguage(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        int kana = 0, hangul = 0, han = 0, cyrillic = 0, arabic = 0, hebrew = 0, thai = 0, greek = 0, latin = 0;
        foreach (var c in text)
        {
            if (!char.IsLetter(c))
            {
                continue;
            }

            switch (c)
            {
                case >= '぀' and <= 'ヿ': kana++; break;
                case >= '가' and <= '힯' or >= 'ᄀ' and <= 'ᇿ': hangul++; break;
                case >= '一' and <= '鿿' or >= '㐀' and <= '䶿': han++; break;
                case >= 'Ѐ' and <= 'ӿ': cyrillic++; break;
                case >= '؀' and <= 'ۿ': arabic++; break;
                case >= '֐' and <= '׿': hebrew++; break;
                case >= '฀' and <= '๿': thai++; break;
                case >= 'Ͱ' and <= 'Ͽ': greek++; break;
                default: latin++; break;
            }
        }

        if (kana > 0)
        {
            return "ja";
        }

        var counts = new (string Code, int Count)[]
        {
            ("ko", hangul), ("zh", han), ("ru", cyrillic), ("ar", arabic), ("he", hebrew), ("th", thai), ("el", greek), ("und-Latn", latin),
        };
        var best = counts.MaxBy(c => c.Count);
        return best.Count == 0 ? null : best.Code;
    }

    /// <summary>A product code as stored: upper case, letters and digits only joined by a dash when it has both.</summary>
    public static string? ProductCode(string? value)
    {
        var cleaned = Text(value, 64, keepNewLines: false);
        return cleaned is null || cleaned.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or ' ')) ? null : cleaned.ToUpperInvariant();
    }
}
