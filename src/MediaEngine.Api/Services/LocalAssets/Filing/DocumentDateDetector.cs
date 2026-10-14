using System.Globalization;
using System.Text.RegularExpressions;

namespace MediaEngine.Api.Services.LocalAssets.Filing;

/// <summary>Which number comes first in a date such as 03/04/2026, when a person's preference is known.</summary>
public enum DocumentDateOrder
{
    /// <summary>Not known: a date that could be read either way is not used.</summary>
    Unspecified,
    DayFirst,
    MonthFirst,
}

/// <summary>
/// Finds the date a document carries in its words. Understands 2026-03-04, 4.3.2026, 4 March 2026 and March 4, 2026
/// (English month names). Dates near words such as "invoice date" or "issued" win; dates near "due", "expires" or
/// "born" are never used. Anything more than 50 years back or a year ahead is ignored.
/// </summary>
public static partial class DocumentDateDetector
{
    private const string MonthNames =
        "January|February|March|April|May|June|July|August|September|October|November|December|Jan|Feb|Mar|Apr|Jun|Jul|Aug|Sept|Sep|Oct|Nov|Dec";

    private static readonly Dictionary<string, int> Months = new(StringComparer.OrdinalIgnoreCase)
    {
        ["jan"] = 1,
        ["january"] = 1,
        ["feb"] = 2,
        ["february"] = 2,
        ["mar"] = 3,
        ["march"] = 3,
        ["apr"] = 4,
        ["april"] = 4,
        ["may"] = 5,
        ["jun"] = 6,
        ["june"] = 6,
        ["jul"] = 7,
        ["july"] = 7,
        ["aug"] = 8,
        ["august"] = 8,
        ["sep"] = 9,
        ["sept"] = 9,
        ["september"] = 9,
        ["oct"] = 10,
        ["october"] = 10,
        ["nov"] = 11,
        ["november"] = 11,
        ["dec"] = 12,
        ["december"] = 12,
    };

    private static readonly string[] Positive = ["invoice date", "date of issue", "issue date", "issued", "dated", "statement date", "bill date", "order date", "letter date", "date:", "date "];

    private static readonly string[] Negative = ["due", "pay by", "payable by", "expires", "expiry", "expiration", "valid until", "valid through", "birth", "dob", "born", "deadline", "renewal"];

    private const int ContextCharacters = 48;

    [GeneratedRegex(@"(?<![\d./\-])(?<y>(?:19|20)\d{2})[-/.](?<m>\d{1,2})[-/.](?<d>\d{1,2})(?!\d)", RegexOptions.CultureInvariant)]
    private static partial Regex IsoDate();

    [GeneratedRegex(@"(?<![\d./\-])(?<a>\d{1,2})[-/.](?<b>\d{1,2})[-/.](?<y>(?:19|20)\d{2})(?![\d])", RegexOptions.CultureInvariant)]
    private static partial Regex NumericDate();

    [GeneratedRegex(@"(?<!\d)(?<d>\d{1,2})(?:st|nd|rd|th)?[ \t.\-/]+(?:of[ \t]+)?\b(?<mon>" + MonthNames + @")\b\.?,?[ \t.\-/]+(?<y>(?:19|20)\d{2})(?!\d)",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex DayMonthName();

    [GeneratedRegex(@"\b(?<mon>" + MonthNames + @")\b\.?[ \t]+(?<d>\d{1,2})(?:st|nd|rd|th)?,?[ \t]+(?<y>(?:19|20)\d{2})(?!\d)",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex MonthNameDay();

    private readonly record struct Candidate(DateOnly Date, int Position);

    /// <summary>The best date in the text, or null when there is none worth using.</summary>
    public static DateOnly? Detect(string? text, DateOnly today, DocumentDateOrder order = DocumentDateOrder.Unspecified)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var earliest = today.AddYears(-50);
        var latest = today.AddYears(1);
        var candidates = new List<Candidate>();
        foreach (Match match in IsoDate().Matches(text))
        {
            Add(candidates, match, Parse(match, "y", "m", "d"), earliest, latest);
        }

        foreach (Match match in NumericDate().Matches(text))
        {
            Add(candidates, match, ParseNumeric(match, order), earliest, latest);
        }

        foreach (Match match in DayMonthName().Matches(text))
        {
            Add(candidates, match, ParseNamed(match), earliest, latest);
        }

        foreach (Match match in MonthNameDay().Matches(text))
        {
            Add(candidates, match, ParseNamed(match), earliest, latest);
        }

        if (candidates.Count == 0)
        {
            return null;
        }

        // A due, expiry or birth date is never the date of the document, even when it is the only one in the words.
        return candidates
            .Select(candidate => (candidate.Date, candidate.Position, Score: Score(text, candidate.Position)))
            .Where(candidate => candidate.Score >= 0)
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.Position)
            .Select(candidate => (DateOnly?)candidate.Date)
            .FirstOrDefault();
    }

    private static void Add(List<Candidate> candidates, Match match, DateOnly? date, DateOnly earliest, DateOnly latest)
    {
        if (date is { } value && value >= earliest && value <= latest)
        {
            candidates.Add(new Candidate(value, match.Index));
        }
    }

    /// <summary>Plus for a date introduced as an issue date, minus for a due, expiry or birth date.</summary>
    private static int Score(string text, int position)
    {
        var start = Math.Max(0, position - ContextCharacters);
        var context = text[start..position];
        var lineBreak = context.LastIndexOfAny(['\r', '\n']);
        if (lineBreak >= 0)
        {
            context = context[(lineBreak + 1)..];
        }

        context = context.ToLowerInvariant();
        if (Negative.Any(word => ContainsWord(context, word)))
        {
            return -20;
        }

        return Positive.Any(word => ContainsWord(context, word)) ? 20 : 0;
    }

    private static bool ContainsWord(string context, string word)
    {
        var index = context.IndexOf(word, StringComparison.Ordinal);
        while (index >= 0)
        {
            var before = index == 0 || !char.IsLetter(context[index - 1]);
            if (before)
            {
                return true;
            }

            index = context.IndexOf(word, index + 1, StringComparison.Ordinal);
        }

        return false;
    }

    private static DateOnly? Parse(Match match, string year, string month, string day) =>
        Build(int.Parse(match.Groups[year].Value, CultureInfo.InvariantCulture),
            int.Parse(match.Groups[month].Value, CultureInfo.InvariantCulture),
            int.Parse(match.Groups[day].Value, CultureInfo.InvariantCulture));

    private static DateOnly? ParseNumeric(Match match, DocumentDateOrder order)
    {
        var a = int.Parse(match.Groups["a"].Value, CultureInfo.InvariantCulture);
        var b = int.Parse(match.Groups["b"].Value, CultureInfo.InvariantCulture);
        var year = int.Parse(match.Groups["y"].Value, CultureInfo.InvariantCulture);
        if (a > 12 && b <= 12)
        {
            return Build(year, b, a);
        }

        if (b > 12 && a <= 12)
        {
            return Build(year, a, b);
        }

        if (a == b)
        {
            return Build(year, a, b);
        }

        // Both readings are real dates: only a known preference can choose between them.
        return order switch
        {
            DocumentDateOrder.DayFirst => Build(year, b, a),
            DocumentDateOrder.MonthFirst => Build(year, a, b),
            _ => null,
        };
    }

    private static DateOnly? ParseNamed(Match match) =>
        Months.TryGetValue(match.Groups["mon"].Value.TrimEnd('.'), out var month)
            ? Build(int.Parse(match.Groups["y"].Value, CultureInfo.InvariantCulture), month,
                int.Parse(match.Groups["d"].Value, CultureInfo.InvariantCulture))
            : null;

    private static DateOnly? Build(int year, int month, int day) =>
        month is >= 1 and <= 12 && day >= 1 && day <= DateTime.DaysInMonth(year, month)
            ? new DateOnly(year, month, day)
            : null;
}
