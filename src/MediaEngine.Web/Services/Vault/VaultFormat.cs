using System.Globalization;

namespace MediaEngine.Web.Services.Vault;

/// <summary>Plain-words formatting for the Vault's lists: a length, a file size and a date added.</summary>
public static class VaultFormat
{
    /// <summary>"42:07" or "1:02:03"; an empty string when the length is not known.</summary>
    public static string Duration(double? seconds)
    {
        if (seconds is not { } value || value <= 0 || double.IsNaN(value) || double.IsInfinity(value))
        {
            return string.Empty;
        }

        var span = TimeSpan.FromSeconds(Math.Min(value, TimeSpan.FromDays(30).TotalSeconds));
        return span.TotalHours >= 1
            ? string.Create(CultureInfo.InvariantCulture, $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{span.Minutes}:{span.Seconds:00}");
    }

    /// <summary>"850 MB" or "1.4 GB"; an empty string when the size is not known.</summary>
    public static string Size(long? bytes)
    {
        if (bytes is not { } value || value <= 0)
        {
            return string.Empty;
        }

        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var size = (double)value;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        var format = unit >= 3 ? "0.0" : "0";
        return size.ToString(format, CultureInfo.InvariantCulture) + " " + units[unit];
    }

    /// <summary>"21 Oct 2026"; an empty string when it is not known.</summary>
    public static string Date(DateTimeOffset? value) =>
        value is { } date ? date.ToLocalTime().ToString("d MMM yyyy", CultureInfo.InvariantCulture) : string.Empty;
}
