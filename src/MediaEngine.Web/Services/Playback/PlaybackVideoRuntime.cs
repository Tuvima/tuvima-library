using System.Globalization;

namespace MediaEngine.Web.Services.Playback;

public static class PlaybackVideoRuntime
{
    /// <summary>
    /// Library detail runtime values use bare positive numbers as minutes. Store
    /// them in an explicit clock form before they enter the playback queue.
    /// </summary>
    public static string? NormalizeLibraryDetailRuntime(string? runtime)
    {
        if (string.IsNullOrWhiteSpace(runtime)) return null;

        var value = runtime.Trim();
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var minutes))
        {
            if (!double.IsFinite(minutes) || minutes <= 0) return null;
            return $"{minutes.ToString("0.###", CultureInfo.InvariantCulture)}:00";
        }

        var seconds = PlaybackTimeParser.TryParseDurationSeconds(value);
        return seconds is > 0 ? value : null;
    }

    public static string? FormatQueueRuntime(string? runtime)
    {
        var seconds = PlaybackTimeParser.TryParseDurationSeconds(runtime);
        if (seconds is not > 0) return null;
        return seconds.Value < 60
            ? PlaybackTimeParser.FormatDuration(seconds.Value)
            : $"{Math.Round(seconds.Value / 60d, MidpointRounding.AwayFromZero):0} min";
    }
}
