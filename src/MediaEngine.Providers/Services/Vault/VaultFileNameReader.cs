using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using MediaEngine.Domain.Configuration;

namespace MediaEngine.Providers.Services.Vault;

/// <summary>What a Vault file or folder name says (Videos spec D38a). Every part is optional except the title, which falls back to the cleaned name.</summary>
/// <param name="PatternMatched">True when one of the library's name patterns fitted the whole name, so the parts were read rather than guessed.</param>
public sealed record VaultNameParts(
    string Title,
    string? Studio,
    DateOnly? Date,
    IReadOnlyList<string> Performers,
    string? ProductCode,
    bool PatternMatched);

/// <summary>
/// Reads a Vault file name with the library's name patterns, ignored words, space characters and product-code rule from
/// <c>config/vault_naming.json</c> (the same idea as Stash's filename parser). Names are untrusted text: patterns are compiled
/// with a time limit, nothing here is logged, and a pattern that does not compile is skipped.
/// </summary>
public sealed class VaultFileNameReader
{
    private static readonly TimeSpan RegexLimit = TimeSpan.FromMilliseconds(250);
    private const string DatePattern = @"(?<date>\d{4}[-. ]\d{2}[-. ]\d{2}|\d{2}[-. ]\d{2}[-. ]\d{2})";
    private const string CodePattern = @"(?<code>[A-Za-z]{2,6}[-_]?\d{2,5})";

    private readonly VaultNamingConfigurationProvider _config;

    public VaultFileNameReader(VaultNamingConfigurationProvider config) => _config = config;

    public VaultNameParts Read(string filePath)
    {
        var rules = _config.Current;
        var stem = Path.GetFileNameWithoutExtension(filePath) ?? string.Empty;
        var folder = Path.GetFileName(Path.GetDirectoryName(filePath) ?? string.Empty) ?? string.Empty;
        var cleaned = Clean(stem, rules);

        string? title = null;
        string? studio = null;
        DateOnly? date = null;
        var performers = new List<string>();
        string? code = null;
        var matched = false;

        foreach (var pattern in rules.Patterns)
        {
            var regex = Compile(pattern);
            if (regex is null)
            {
                continue;
            }

            Match match;
            try
            {
                match = regex.Match(cleaned);
            }
            catch (RegexMatchTimeoutException)
            {
                continue;
            }

            if (!match.Success)
            {
                continue;
            }

            matched = true;
            title = Tidy(match.Groups["title"].Success ? match.Groups["title"].Value : null);
            studio = Tidy(match.Groups["studio"].Success ? match.Groups["studio"].Value : null);
            date = match.Groups["date"].Success ? ParseDate(match.Groups["date"].Value) : null;
            if (match.Groups["performer"].Success)
            {
                performers.AddRange(SplitNames(match.Groups["performer"].Value));
            }

            if (match.Groups["code"].Success)
            {
                code = NormaliseCode(match.Groups["code"].Value, rules.ProductCode);
            }

            break;
        }

        // A code can stand anywhere in the file name, or failing that in its folder's name (Videos spec D37).
        code ??= FindProductCode(cleaned, rules.ProductCode) ?? FindProductCode(Clean(folder, rules), rules.ProductCode);
        title ??= Tidy(cleaned) ?? string.Empty;
        return new VaultNameParts(Limit(title, 300) ?? string.Empty, Limit(studio, 200), date, performers, code, matched);
    }

    /// <summary>Reads a product code from free text. Null when there is none.</summary>
    public static string? FindProductCode(string text, VaultProductCodeConfiguration rules)
    {
        Regex regex;
        try
        {
            regex = new Regex(rules.Pattern, RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, RegexLimit);
        }
        catch (ArgumentException)
        {
            return null;
        }

        try
        {
            foreach (Match match in regex.Matches(text))
            {
                var letters = match.Groups["letters"];
                var digits = match.Groups["digits"];
                if (!letters.Success || !digits.Success
                    || rules.IgnoredPrefixes.Contains(letters.Value, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                return $"{letters.Value.ToUpperInvariant()}-{digits.Value}";
            }
        }
        catch (RegexMatchTimeoutException)
        {
            return null;
        }

        return null;
    }

    /// <summary>Upper case, letters then digits joined by a dash (ABC-123). Anything that does not look like a code gives null.</summary>
    public static string? NormaliseCode(string? value, VaultProductCodeConfiguration rules)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var compact = new string(value.Where(char.IsLetterOrDigit).ToArray());
        var split = compact.TakeWhile(char.IsLetter).Count();
        if (split is < 2 or > 6 || compact.Length - split is < 2 or > 5 || !compact[split..].All(char.IsDigit))
        {
            return null;
        }

        var letters = compact[..split];
        return rules.IgnoredPrefixes.Contains(letters, StringComparer.OrdinalIgnoreCase)
            ? null
            : $"{letters.ToUpperInvariant()}-{compact[split..]}";
    }

    /// <summary>The same code written two ways (ABC-123, abc123) compares equal.</summary>
    public static bool SameCode(string? a, string? b) =>
        !string.IsNullOrWhiteSpace(a) && !string.IsNullOrWhiteSpace(b)
        && string.Equals(Compact(a), Compact(b), StringComparison.OrdinalIgnoreCase);

    private static string Compact(string value) => new(value.Where(char.IsLetterOrDigit).ToArray());

    private static string Clean(string name, VaultNamingConfiguration rules)
    {
        var builder = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            builder.Append(char.IsControl(c) || rules.SpaceCharacters.Contains(c.ToString()) ? ' ' : c);
        }

        var text = builder.ToString();
        foreach (var word in rules.IgnoredWords.Where(w => !string.IsNullOrWhiteSpace(w)))
        {
            try
            {
                text = Regex.Replace(
                    text, $@"(?<![A-Za-z0-9]){Regex.Escape(word.Trim())}(?![A-Za-z0-9])", " ",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexLimit);
            }
            catch (RegexMatchTimeoutException)
            {
                // A name that makes a pattern run too long is read as it is.
            }
        }

        // Brackets left empty by removed words, then the runs of spaces they leave behind.
        text = Regex.Replace(text, @"[\[\(\{]\s*[\]\)\}]", " ", RegexOptions.CultureInvariant, RegexLimit);
        return Regex.Replace(text, @"\s+", " ", RegexOptions.CultureInvariant, RegexLimit).Trim(' ', '-');
    }

    private static Regex? Compile(string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern) || pattern.Length > 200)
        {
            return null;
        }

        var builder = new StringBuilder("^");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var i = 0;
        while (i < pattern.Length)
        {
            var end = pattern[i] == '[' ? pattern.IndexOf(']', i) : -1;
            if (end > i)
            {
                var name = pattern[(i + 1)..end].Trim().ToLowerInvariant();
                if (!seen.Add(name))
                {
                    return null;
                }

                switch (name)
                {
                    case "studio": builder.Append("(?<studio>.+?)"); break;
                    case "title": builder.Append("(?<title>.+)"); break;
                    case "performer": builder.Append("(?<performer>.+?)"); break;
                    case "date": builder.Append(DatePattern); break;
                    case "code": builder.Append(CodePattern); break;
                    default: return null;
                }

                i = end + 1;
                continue;
            }

            builder.Append(char.IsWhiteSpace(pattern[i]) ? @"\s+" : Regex.Escape(pattern[i].ToString()));
            i++;
        }

        builder.Append('$');
        try
        {
            return new Regex(builder.ToString(), RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, RegexLimit);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static DateOnly? ParseDate(string text)
    {
        var digits = Regex.Replace(text, @"[^0-9]", "-");
        var parts = digits.Split('-', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3 || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var first)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var month)
            || !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var day))
        {
            return null;
        }

        var year = parts[0].Length == 4 ? first : first + (first <= DateTime.UtcNow.Year % 100 ? 2000 : 1900);
        try
        {
            var date = new DateOnly(year, month, day);
            return VaultFieldValidator.IsSensibleDate(date) ? date : null;
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static IEnumerable<string> SplitNames(string text) =>
        Regex.Split(text, @"\s*(?:,|&|\band\b|\bwith\b)\s*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexLimit)
            .Select(name => Tidy(name))
            .Where(name => name is not null)
            .Select(name => Limit(name, 200)!)
            .Take(20);

    private static string? Tidy(string? value)
    {
        var text = value?.Trim(' ', '-', '_', '.');
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static string? Limit(string? value, int max) => value is null || value.Length <= max ? value : value[..max];
}
