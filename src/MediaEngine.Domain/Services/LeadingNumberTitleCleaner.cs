using System.Text.RegularExpressions;

namespace MediaEngine.Domain.Services;

/// <summary>
/// Removes a leading series or track number from an audiobook title ("5.5 - The Vital Abyss",
/// "03.5 - The Vital Abyss", "02. Dune") so the title can be compared with, or searched for as,
/// the book it belongs to. The number is a position hint, never part of the title.
/// A title that is only a number, or whose remainder would be a single character, is left as is.
/// </summary>
public static class LeadingNumberTitleCleaner
{
    private static readonly Regex Prefix = new(
        @"^\s*(?<n>\d{1,3}(?:\.\d{1,2})?)\s*(?:[-–—:.]|\))\s+(?<rest>\S.*)$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>Returns the title without its leading number; the number is returned in <paramref name="number"/> (or null).</summary>
    public static string Strip(string? title, out string? number)
    {
        number = null;
        if (string.IsNullOrWhiteSpace(title))
        {
            return title ?? string.Empty;
        }

        var match = Prefix.Match(title);
        if (!match.Success)
        {
            return title.Trim();
        }

        var rest = match.Groups["rest"].Value.Trim();
        if (rest.Length < 2)
        {
            return title.Trim();
        }

        number = match.Groups["n"].Value;
        return rest;
    }
}
