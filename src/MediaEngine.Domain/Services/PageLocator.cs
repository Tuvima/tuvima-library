namespace MediaEngine.Domain.Services;

/// <summary>
/// Where a page bookmark sits, written the way a bookmark file carries it: <c>page:12</c> (PDF and comic reader spec D14). One place
/// reads and writes it so the reader, the editor and the bookmark files always agree.
/// </summary>
public static class PageLocator
{
    public const string Prefix = "page:";

    /// <summary>The most pages a file can have; larger numbers are not pages.</summary>
    public const int MaximumPage = 100_000;

    public static string Format(int page) => Prefix + page.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public static int? TryParse(string? locator)
    {
        if (string.IsNullOrWhiteSpace(locator))
        {
            return null;
        }

        var text = locator.Trim();
        return text.StartsWith(Prefix, StringComparison.Ordinal)
            && int.TryParse(text.AsSpan(Prefix.Length), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var page)
            && page is >= 1 and <= MaximumPage
            ? page
            : null;
    }
}
