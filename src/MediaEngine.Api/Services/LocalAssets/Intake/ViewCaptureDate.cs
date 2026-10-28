using System.Globalization;
using System.Text.RegularExpressions;

namespace MediaEngine.Api.Services.LocalAssets.Intake;

/// <summary>Where a photo's date came from, strongest first (Photo and video filing spec D2).</summary>
public enum ViewCaptureDateSource
{
    /// <summary>The camera, phone or video's own date.</summary>
    Metadata,

    /// <summary>A date written in the file's name, such as <c>IMG_20240315_142233</c>.</summary>
    FileName,

    /// <summary>The date the file was last changed. The last resort.</summary>
    FileDate,
}

/// <summary>A capture date and where it came from.</summary>
public readonly record struct ViewCaptureDate(DateTimeOffset Value, ViewCaptureDateSource Source);

/// <summary>
/// Finds the date a photo or video was taken from the date in its file name (Photo and video filing spec D2): phone and camera
/// names such as <c>IMG_20240315_142233</c>, <c>PXL_20240315_142233123</c>, <c>VID-20240315-WA0001</c>,
/// <c>WhatsApp Image 2024-03-15 at 14.22.33</c> and <c>Screenshot_2024-03-15-14-22-33</c>. A phone that rewrites file dates on copy
/// would otherwise file every photo under the day it was copied.
/// </summary>
public static class ViewCaptureDates
{
    private const int EarliestYear = 1990;

    // A date: year, then month, then day, each optionally split by - _ or . (so 20240315, 2024-03-15 and 2024_03_15 all read).
    // It must not sit inside a longer run of digits (a 13-digit timestamp is not a date).
    private static readonly Regex DatePattern = new(
        @"(?<![0-9])(?<y>(?:19|20)\d{2})[-_.]?(?<m>0[1-9]|1[0-2])[-_.]?(?<d>0[1-9]|[12]\d|3[01])(?![0-9])" +
        @"(?:(?:\s+at\s+|[\sT_\-.]+)(?<h>[01]\d|2[0-3])[-_.:]?(?<mi>[0-5]\d)[-_.:]?(?<s>[0-5]\d)(?![0-9]))?",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>
    /// The date written in a file name, or null when there is none that makes sense (a month 13, a day that does not exist, a year
    /// before 1990 or after <paramref name="today"/>'s next day). The time of day is used when the name has one.
    /// </summary>
    public static DateTimeOffset? FromFileName(string fileName, DateTimeOffset today)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return null;
        }

        var stem = Path.GetFileNameWithoutExtension(fileName);
        foreach (Match match in DatePattern.Matches(stem))
        {
            var year = int.Parse(match.Groups["y"].Value, CultureInfo.InvariantCulture);
            var month = int.Parse(match.Groups["m"].Value, CultureInfo.InvariantCulture);
            var day = int.Parse(match.Groups["d"].Value, CultureInfo.InvariantCulture);
            if (year < EarliestYear || day > DateTime.DaysInMonth(year, month))
            {
                continue;
            }

            var hour = match.Groups["h"].Success ? int.Parse(match.Groups["h"].Value, CultureInfo.InvariantCulture) : 0;
            var minute = match.Groups["mi"].Success ? int.Parse(match.Groups["mi"].Value, CultureInfo.InvariantCulture) : 0;
            var second = match.Groups["s"].Success ? int.Parse(match.Groups["s"].Value, CultureInfo.InvariantCulture) : 0;
            var local = new DateTime(year, month, day, hour, minute, second, DateTimeKind.Unspecified);
            var value = new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
            if (value > today.AddDays(1))
            {
                continue;
            }

            return value;
        }

        return null;
    }

    /// <summary>
    /// The date to file by: the file's own metadata date when it has one, then a date in the name, then the file date. A file with an
    /// unusable date is never refused; it is filed under whichever date applies.
    /// </summary>
    public static ViewCaptureDate Resolve(DateTimeOffset? metadataDate, string fileName, DateTimeOffset fileDate, DateTimeOffset today)
    {
        if (metadataDate is { } taken && taken.Year >= EarliestYear && taken <= today.AddDays(1))
        {
            return new ViewCaptureDate(taken, ViewCaptureDateSource.Metadata);
        }

        return FromFileName(fileName, today) is { } named
            ? new ViewCaptureDate(named, ViewCaptureDateSource.FileName)
            : new ViewCaptureDate(fileDate, ViewCaptureDateSource.FileDate);
    }
}
