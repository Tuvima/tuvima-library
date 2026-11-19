using System.Globalization;
using System.Text.RegularExpressions;
using MediaEngine.Contracts.Details;
using MediaEngine.Web.Resources;
using MediaEngine.Web.Services.Playback;
using Microsoft.Extensions.Localization;

namespace MediaEngine.Web.Components.Details;

/// <summary>
/// The length under an episode's tile (issue 1171): "42 min" under an hour, "1h 5m" from an hour, the same wording as the hero's
/// movie length. The file's real length is used when it is known, otherwise the provider's runtime; nothing is drawn when
/// neither is known.
/// </summary>
public static partial class EpisodeRuntimeLabel
{
    public static string? For(SequenceItemViewModel item, IStringLocalizer<SharedStrings> localizer)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(localizer);
        var seconds = item.DurationSeconds is > 0 ? item.DurationSeconds : ProviderRuntimeSeconds(item.Duration);
        return seconds is > 0 ? Format(localizer, seconds.Value) : null;
    }

    /// <summary>"42 min" under an hour, "1h 5m" from an hour and "2h" on the hour; null for a length under half a minute.</summary>
    public static string? Format(IStringLocalizer<SharedStrings> localizer, double seconds)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        if (!double.IsFinite(seconds) || seconds < 30)
        {
            return null;
        }

        var minutes = (int)Math.Round(seconds / 60d, MidpointRounding.AwayFromZero);
        if (minutes < 60)
        {
            return localizer["VideoPausedDetails_Minutes", Math.Max(1, minutes)].Value;
        }

        return minutes % 60 == 0
            ? $"{minutes / 60}h"
            : localizer["VideoPausedDetails_HoursMinutes", minutes / 60, minutes % 60].Value;
    }

    /// <summary>
    /// A stored runtime as seconds: minutes as a bare number ("42"), as the Engine words it ("42m", "1h 5m", "1h") or as a clock
    /// ("42:00", "1:05:00"). Anything else is not a length.
    /// </summary>
    public static double? ProviderRuntimeSeconds(string? runtime)
    {
        if (string.IsNullOrWhiteSpace(runtime))
        {
            return null;
        }

        var text = runtime.Trim();
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var minutes))
        {
            return double.IsFinite(minutes) && minutes > 0 ? minutes * 60d : null;
        }

        if (text.Contains(':', StringComparison.Ordinal))
        {
            return PlaybackTimeParser.TryParseDurationSeconds(text);
        }

        var match = HoursMinutes().Match(text);
        if (!match.Success || (!match.Groups["h"].Success && !match.Groups["m"].Success))
        {
            return null;
        }

        var hours = match.Groups["h"].Success ? int.Parse(match.Groups["h"].Value, CultureInfo.InvariantCulture) : 0;
        var mins = match.Groups["m"].Success ? int.Parse(match.Groups["m"].Value, CultureInfo.InvariantCulture) : 0;
        return (hours * 60 + mins) * 60d;
    }

    [GeneratedRegex(@"^(?:(?<h>\d{1,3})\s*h)?\s*(?:(?<m>\d{1,4})\s*m(?:in)?)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HoursMinutes();
}
