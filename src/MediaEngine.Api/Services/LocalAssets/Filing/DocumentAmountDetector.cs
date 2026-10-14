using System.Globalization;
using System.Text.RegularExpressions;

namespace MediaEngine.Api.Services.LocalAssets.Filing;

/// <param name="Currency">An ISO 4217 code when the document shows one (or a euro or pound sign), otherwise null.</param>
public sealed record DetectedAmount(double Amount, string? Currency);

/// <summary>
/// Finds the one amount a bill or receipt is about, and is deliberately cautious: it only reads a number written
/// right after "amount due", "balance due", "total due", "grand total", "total amount", "balance" or "total" (never
/// "subtotal"), it needs either two decimals or a currency to call something an amount, and when the strongest label
/// shows two different numbers it says nothing at all. A dollar sign is read as US dollars.
/// </summary>
public static partial class DocumentAmountDetector
{
    private static readonly string[] Labels =
    [
        "amount due", "balance due", "total due", "grand total", "total amount", "amount payable", "balance", "total",
    ];

    private static readonly HashSet<string> CurrencyCodes = new(StringComparer.Ordinal)
    {
        "USD", "EUR", "GBP", "CAD", "AUD", "NZD", "CHF", "JPY", "CNY", "INR", "SEK", "NOK", "DKK", "PLN", "CZK", "MXN", "BRL", "ZAR", "SGD", "HKD",
    };

    private const string Number = @"\d{1,3}(?:[.,]\d{3})+(?:[.,]\d{1,2})?|\d+(?:[.,]\d{1,2})?";

    [GeneratedRegex(@"(?<![A-Za-z])(?<label>amount\s+due|balance\s+due|total\s+due|grand\s+total|total\s+amount|amount\s+payable|balance|total)\b(?<gap>[^\r\n\d]{0,30}?)(?<num>" + Number + @")(?<after>[ \t]{0,2}[A-Za-z]{0,3})",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex LabelledNumber();

    /// <summary>The amount the document is about, or null when it is unclear.</summary>
    public static DetectedAmount? Detect(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var found = new List<(int Rank, double Amount, string? Currency)>();
        foreach (Match match in LabelledNumber().Matches(text))
        {
            var rank = Array.IndexOf(Labels, Regex.Replace(match.Groups["label"].Value.ToLowerInvariant(), @"\s+", " "));
            var number = match.Groups["num"].Value;
            var currency = CurrencyFrom(match.Groups["gap"].Value, match.Groups["after"].Value);
            if (rank < 0 || !TryParse(number, out var amount) || !(currency is not null || HasTwoDecimals(number)))
            {
                continue;
            }

            found.Add((rank, amount, currency));
        }

        if (found.Count == 0)
        {
            return null;
        }

        // The strongest label decides; if it shows more than one number the document is ambiguous.
        var best = found.Min(item => item.Rank);
        var strongest = found.Where(item => item.Rank == best).ToList();
        if (strongest.Select(item => item.Amount).Distinct().Count() != 1)
        {
            return null;
        }

        var currencies = strongest.Select(item => item.Currency).Where(code => code is not null).Distinct().ToList();
        return new DetectedAmount(strongest[0].Amount, currencies.Count == 1 ? currencies[0] : null);
    }

    private static bool HasTwoDecimals(string number)
    {
        var separator = number.LastIndexOfAny(['.', ',']);
        return separator >= 0 && number.Length - separator - 1 == 2;
    }

    private static string? CurrencyFrom(string gap, string after)
    {
        if (gap.Contains('€', StringComparison.Ordinal))
        {
            return "EUR";
        }

        if (gap.Contains('£', StringComparison.Ordinal))
        {
            return "GBP";
        }

        if (gap.Contains('$', StringComparison.Ordinal))
        {
            return "USD";
        }

        foreach (var word in Regex.Matches(gap, "[A-Za-z]{3}").Select(match => match.Value.ToUpperInvariant()))
        {
            if (CurrencyCodes.Contains(word))
            {
                return word;
            }
        }

        var trailing = after.Trim().ToUpperInvariant();
        return CurrencyCodes.Contains(trailing) ? trailing : null;
    }

    /// <summary>Reads 1,234.56 and 1.234,56 alike; a lone separator before exactly three digits is a thousands mark.</summary>
    public static bool TryParse(string number, out double amount)
    {
        amount = 0;
        var text = number.Replace(" ", string.Empty, StringComparison.Ordinal);
        var lastDot = text.LastIndexOf('.');
        var lastComma = text.LastIndexOf(',');
        var decimalAt = Math.Max(lastDot, lastComma);
        string normalised;
        if (decimalAt < 0)
        {
            normalised = text;
        }
        else
        {
            var digitsAfter = text.Length - decimalAt - 1;
            var bothKinds = lastDot >= 0 && lastComma >= 0;
            var startsWithZero = text.Length > 1 && text[0] == '0' && decimalAt == 1;
            var isDecimal = bothKinds || digitsAfter != 3 || startsWithZero;
            normalised = isDecimal
                ? text[..decimalAt].Replace(".", string.Empty, StringComparison.Ordinal).Replace(",", string.Empty, StringComparison.Ordinal)
                  + "." + text[(decimalAt + 1)..]
                : text.Replace(".", string.Empty, StringComparison.Ordinal).Replace(",", string.Empty, StringComparison.Ordinal);
        }

        return double.TryParse(normalised, NumberStyles.Float, CultureInfo.InvariantCulture, out amount)
            && double.IsFinite(amount) && amount > 0 && amount < 1_000_000_000;
    }
}
