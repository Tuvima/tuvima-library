using System.Globalization;

namespace MediaEngine.Web.Services.Editing;

/// <summary>How the Match tab's lists write the facts a provider sends as plain data.</summary>
public static class EditorMatchFormat
{
    /// <summary>An air date the provider sends as <c>2025-10-14</c>, written the way the person's culture writes a short date ("14 Oct 2025"); the text as sent when it is not a date.</summary>
    public static string AirDate(string? iso)
    {
        if (string.IsNullOrWhiteSpace(iso))
        {
            return string.Empty;
        }

        var text = iso.Trim();
        return DateOnly.TryParseExact(text.Length >= 10 ? text[..10] : text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date.ToString("d MMM yyyy", CultureInfo.CurrentCulture)
            : text;
    }
}
