using System.Globalization;
using MediaEngine.Contracts.LocalAssets;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Storage;

/// <summary>
/// A rule keeps a custom field's value as text, written the way a person would type it: words for text and choices,
/// a plain number, <c>12.50 EUR</c> for an amount, <c>2026-03-01</c> for a date and <c>true</c> or <c>false</c> for yes or no.
/// </summary>
public static class DocumentRuleFieldValues
{
    /// <summary>Reads the text for a field of the given kind. Returns null (with the reason) when it does not fit.</summary>
    public static ViewDocumentFieldWrite? TryCreateWrite(Guid fieldId, string kind, string? text, out string? error)
    {
        error = null;
        var value = text?.Trim();
        if (string.IsNullOrEmpty(value))
        {
            error = "A rule that sets a field needs a value.";
            return null;
        }

        switch (kind)
        {
            case ViewDocumentFieldKinds.Text:
            case ViewDocumentFieldKinds.Choice:
                return new ViewDocumentFieldWrite(fieldId, Text: value);
            case ViewDocumentFieldKinds.Number:
                if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number))
                {
                    return new ViewDocumentFieldWrite(fieldId, Number: number);
                }

                error = "That is not a number.";
                return null;
            case ViewDocumentFieldKinds.Money:
                var parts = value.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (parts.Length == 2
                    && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var amount)
                    && double.IsFinite(amount)
                    && parts[1].Length == 3 && parts[1].All(char.IsAsciiLetter))
                {
                    return new ViewDocumentFieldWrite(fieldId, Text: parts[1].ToUpperInvariant(), Number: amount);
                }

                error = "An amount must look like 12.50 EUR.";
                return null;
            case ViewDocumentFieldKinds.Date:
                if (DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                {
                    return new ViewDocumentFieldWrite(fieldId, Date: date);
                }

                error = "A date must look like 2026-03-01.";
                return null;
            case ViewDocumentFieldKinds.Bool:
                if (bool.TryParse(value, out var flag))
                {
                    return new ViewDocumentFieldWrite(fieldId, Flag: flag);
                }

                error = "Use true or false.";
                return null;
            default:
                error = "That field has an unknown kind.";
                return null;
        }
    }

    /// <summary>Writes an amount in the form <see cref="TryCreateWrite"/> reads.</summary>
    public static string FormatMoney(double amount, string currency) =>
        amount.ToString("0.00", CultureInfo.InvariantCulture) + " " + currency.ToUpperInvariant();
}
