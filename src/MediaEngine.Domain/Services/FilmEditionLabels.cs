using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MediaEngine.Domain.Services;

/// <summary>
/// Reads the cut ("Director's Cut", "Final Cut") and the picture quality ("4K", "1080p") a film's file name
/// carries, and joins them into the label of the film's Edition. Pure text rules: no file or data store access.
/// </summary>
public static class FilmEditionLabels
{
    /// <summary>Claim key the film's cut is carried under (the display form, e.g. "Director's Cut").</summary>
    public const string CutKey = "edition_cut";

    /// <summary>Claim key the film's picture quality is carried under (e.g. "4K HDR", "1080p").</summary>
    public const string QualityKey = "edition_quality";

    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(100);

    private static readonly Regex Resolution = new(
        @"(?<![A-Za-z0-9])(?<r>2160p|4k|uhd|1440p|1080p|1080i|720p|576p|480p)(?![A-Za-z0-9])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, Timeout);

    private static readonly Regex HighDynamicRange = new(
        @"(?<![A-Za-z0-9])(?:hdr10\+?|hdr|dolby[ ._-]?vision|dv)(?![A-Za-z0-9])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, Timeout);

    /// <summary>"directors cut" or "Director's cut" becomes "Director's Cut"; other markers are title-cased.</summary>
    public static string CutDisplay(string marker)
    {
        var text = marker.Trim().Replace("’", "'", StringComparison.Ordinal);
        text = Regex.Replace(text, @"\bdirectors\b", "director's", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, Timeout);
        return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(text.ToLowerInvariant());
    }

    /// <summary>The quality a file name states ("Movie.2160p.HDR.mkv" gives "4K HDR"), or null.</summary>
    public static string? QualityFromName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        try
        {
            var match = Resolution.Match(name);
            if (!match.Success)
            {
                return null;
            }

            var label = Resolution_Label(match.Groups["r"].Value);
            return HighDynamicRange.IsMatch(name) && label is ("4K" or "1440p") ? label + " HDR" : label;
        }
        catch (RegexMatchTimeoutException)
        {
            return null;
        }
    }

    /// <summary>The quality the picture itself has, from its height (or, for a widescreen crop, its width) in pixels, or null when unknown.</summary>
    public static string? QualityFromHeight(int? height, int? width = null) => (width is > 0 && height is > 0 ? Math.Max(height.Value, (int)(width.Value * 9L / 16)) : height) switch
    {
        null or <= 0 => null,
        >= 2000 => "4K",
        >= 1300 => "1440p",
        >= 1000 => "1080p",
        >= 700 => "720p",
        _ => "SD",
    };

    /// <summary>"Final Cut · 4K", "Final Cut", "4K" or null.</summary>
    public static string? Build(string? cut, string? quality)
    {
        var parts = new[] { cut, quality }.Where(part => !string.IsNullOrWhiteSpace(part)).Select(part => part!.Trim()).ToArray();
        return parts.Length == 0 ? null : string.Join(" · ", parts);
    }

    /// <summary>Lower-case letters and digits only, accents removed, so "Blade Runner: Final" and "blade_runner final" compare equal.</summary>
    public static string IdentityKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length);
        foreach (var ch in value.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(char.ToLowerInvariant(ch));
            }
        }

        return builder.ToString();
    }

    /// <summary>The key ingestion locks on while it files one film, so two cuts arriving together cannot both start a new film.</summary>
    public static string? LockKey(string? title)
    {
        var key = IdentityKey(title);
        return key.Length == 0 ? null : $"film:{key}";
    }

    /// <summary>Key the pipeline adds to a film's metadata so grouping stays inside the file's own library (never across the Vault).</summary>
    public const string LibraryKey = "library_id";

    /// <summary>The metadata with the file's library id added, or the metadata itself when there is none.</summary>
    public static IReadOnlyDictionary<string, string>? WithLibrary(IReadOnlyDictionary<string, string>? metadata, string? libraryId)
        => string.IsNullOrWhiteSpace(libraryId)
            ? metadata
            : new Dictionary<string, string>(metadata ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase) { [LibraryKey] = libraryId };

    private static string Resolution_Label(string token) => token.ToLowerInvariant() switch
    {
        "2160p" or "4k" or "uhd" => "4K",
        "1440p" => "1440p",
        "1080p" or "1080i" => "1080p",
        "720p" => "720p",
        _ => "SD",
    };
}
