using System.Globalization;
using System.Text;

namespace MediaEngine.Web.Services.Formatting;

/// <summary>
/// Consolidated Dashboard-only display formatting helpers (duration, count,
/// playback speed, and identifier-to-title text shaping) that were previously
/// copy-pasted as private statics across unrelated components. Each method
/// below maps to exactly one original private copy's visible output; where
/// two copies produced different text for the same input, both are kept as
/// distinct, precisely named methods rather than homogenized. See packet 4F's
/// migration report for the full copy-to-method mapping.
/// </summary>
public static class DisplayFormat
{
    // ---- Duration -------------------------------------------------------

    /// <summary>
    /// "Xh Ym" when the duration is at least an hour, otherwise "Xm Ys".
    /// Normalizes the sign via <see cref="TimeSpan.Duration()"/> first.
    /// Originally duplicated (identical formatting logic) in
    /// Components/Settings/OverviewTab.razor and, via
    /// <see cref="FormatDurationHoursMinutesOrNull"/>, Components/Settings/IngestionBatchHistory.razor.
    /// </summary>
    public static string FormatDurationHoursMinutes(TimeSpan duration)
    {
        duration = duration.Duration();
        return duration.TotalHours >= 1
            ? $"{(int)duration.TotalHours}h {duration.Minutes}m"
            : $"{duration.Minutes}m {duration.Seconds}s";
    }

    /// <summary>
    /// Nullable-seconds overload of <see cref="FormatDurationHoursMinutes(TimeSpan)"/>;
    /// returns <c>null</c> when <paramref name="seconds"/> is missing or not positive.
    /// Originally private in Components/Settings/IngestionBatchHistory.razor.
    /// </summary>
    public static string? FormatDurationHoursMinutesOrNull(double? seconds)
        => seconds is > 0 ? FormatDurationHoursMinutes(TimeSpan.FromSeconds(seconds.Value)) : null;

    /// <summary>
    /// Three-tier "Xs" / "Xm Ys" / "Xh Ym" format that rounds a sub-minute
    /// duration to the nearest whole second. Originally private in
    /// Components/Settings/IngestionLiveDashboard.razor.cs. Distinct from
    /// <see cref="FormatDurationCompactFloor"/> (Services/Integration/IngestionLiveDashboardState.Projection.cs),
    /// which floors instead of rounds for sub-minute durations — the two diverge
    /// on fractional-second input and must not be merged.
    /// </summary>
    public static string FormatDurationCompact(TimeSpan value)
    {
        if (value.TotalSeconds < 60)
        {
            return $"{Math.Max(1, (int)Math.Round(value.TotalSeconds))}s";
        }

        if (value.TotalMinutes < 60)
        {
            return $"{(int)value.TotalMinutes}m {value.Seconds}s";
        }

        return $"{(int)value.TotalHours}h {value.Minutes}m";
    }

    /// <summary>
    /// Three-tier "Xs" / "Xm Ys" / "Xh Ym" format that floors (rather than rounds)
    /// the sub-minute remainder and normalizes sign via <see cref="TimeSpan.Duration()"/>.
    /// Originally private in Services/Integration/IngestionLiveDashboardState.Projection.cs.
    /// See <see cref="FormatDurationCompact"/> for the rounding sibling.
    /// </summary>
    public static string FormatDurationCompactFloor(TimeSpan duration)
    {
        duration = duration.Duration();
        if (duration.TotalHours >= 1)
        {
            return $"{(int)duration.TotalHours}h {duration.Minutes}m";
        }

        if (duration.TotalMinutes >= 1)
        {
            return $"{duration.Minutes}m {duration.Seconds}s";
        }

        return $"{Math.Max(1, duration.Seconds)}s";
    }

    /// <summary>
    /// Hours+minutes only, truncating (never rounding) the minutes component and
    /// never showing seconds. Originally private in Components/Pages/EpubReader.razor
    /// (reading session / total reading time stats). Distinct from
    /// <see cref="FormatDurationHoursMinutesRounded"/>, which rounds instead of
    /// truncating.
    /// </summary>
    public static string FormatDurationHoursMinutesTruncated(long totalSeconds)
    {
        var hours = totalSeconds / 3600;
        var minutes = (totalSeconds % 3600) / 60;
        return hours > 0 ? $"{hours}h {minutes}m" : $"{minutes}m";
    }

    /// <summary>
    /// Hours+minutes only, rounding the total to the nearest minute and never
    /// showing seconds. Returns "0m" for non-positive input. Originally private
    /// in Components/Settings/UserOverviewTab.razor ("Time Tracked" stat).
    /// Distinct from <see cref="FormatDurationHoursMinutesTruncated"/>, which
    /// truncates instead of rounding.
    /// </summary>
    public static string FormatDurationHoursMinutesRounded(double seconds)
    {
        if (seconds <= 0)
        {
            return "0m";
        }

        var totalMinutes = (int)Math.Round(seconds / 60);
        var hours = totalMinutes / 60;
        var minutes = totalMinutes % 60;
        return hours > 0 ? $"{hours}h {minutes}m" : $"{minutes}m";
    }

    // ---- Count ------------------------------------------------------------

    /// <summary>
    /// Thousands-separated count using the current culture. Originally
    /// duplicated (identical) in Components/Settings/IngestionLiveDashboard.razor.cs
    /// and Components/Settings/OverviewTab.razor.
    /// </summary>
    public static string FormatCount(int value) => value.ToString("N0", CultureInfo.CurrentCulture);

    /// <summary>
    /// <see cref="long"/> overload of <see cref="FormatCount(int)"/>. Originally
    /// private in Components/Settings/IngestionLiveDashboard.razor.cs.
    /// </summary>
    public static string FormatCount(long value) => value.ToString("N0", CultureInfo.CurrentCulture);

    /// <summary>
    /// Thousands-separated count that clamps negative input to zero before
    /// formatting. Originally private in
    /// Services/Integration/IngestionLiveDashboardState.Projection.cs — kept
    /// distinct from <see cref="FormatCount(int)"/> because of the clamp.
    /// </summary>
    public static string FormatCountClamped(int value) => Math.Max(0, value).ToString("N0", CultureInfo.CurrentCulture);

    // ---- Playback speed -----------------------------------------------------

    /// <summary>
    /// "{rate}x" clamped to 0.5-32x with up to two decimal places (invariant
    /// culture). Originally duplicated (byte-identical) in
    /// Components/Listen/ListenNowPlayingBar.razor and
    /// Components/Listen/ListenTransportControls.razor.
    /// </summary>
    public static string FormatSpeedListen(double rate)
        => $"{Math.Clamp(rate, 0.5d, 32d).ToString("0.##", CultureInfo.InvariantCulture)}x";

    /// <summary>
    /// "{rate}x" with up to two decimal places, current culture, no clamping.
    /// Takes a <see cref="decimal"/> settings value directly. Originally private
    /// in Components/Settings/PlaybackTab.razor.
    /// </summary>
    public static string FormatSpeedSetting(decimal value) => $"{value:0.##}x";

    /// <summary>
    /// "{rate}x" clamped to 0.5-3x with one required and one optional decimal,
    /// using invariant culture for stable playback-control labels.
    /// Originally private in Components/Shared/PlaybackControlCatalog.cs.
    /// </summary>
    public static string FormatSpeedControl(double rate)
        => $"{Math.Clamp(rate, 0.5d, 3d).ToString("0.0#", CultureInfo.InvariantCulture)}x";

    /// <summary>
    /// "{rate}x" clamped to 0.1-32x with one required and one optional decimal (invariant
    /// culture). Originally private in Components/Shared/PlaybackSpeedControl.razor.
    /// </summary>
    public static string FormatSpeedSlider(double rate)
        => $"{Math.Clamp(rate, 0.1d, 32d).ToString("0.0#", CultureInfo.InvariantCulture)}x";

    // ---- Text shaping -------------------------------------------------------

    /// <summary>
    /// Splits a snake_case/kebab-case/camelCase identifier into title-cased words
    /// (e.g. "tmdb_id" -&gt; "TMDB ID", "MediaTypeAudit" -&gt; "Media Type Audit"),
    /// with a few domain-specific acronym fixups (API/QID/TMDB).
    /// </summary>
    public static string SplitWords(string value)
    {
        var normalized = value.Replace('_', ' ').Replace('-', ' ');
        var builder = new StringBuilder(normalized.Length + 8);
        for (var i = 0; i < normalized.Length; i++)
        {
            if (i > 0 && char.IsUpper(normalized[i]) && !char.IsWhiteSpace(normalized[i - 1]))
            {
                builder.Append(' ');
            }
            else if (i > 0 && char.IsDigit(normalized[i]) && char.IsLetter(normalized[i - 1]))
            {
                builder.Append(' ');
            }

            builder.Append(normalized[i]);
        }

        return CultureInfo.CurrentCulture.TextInfo.ToTitleCase(builder.ToString().Trim())
            .Replace(" Api", " API", StringComparison.Ordinal)
            .Replace(" Qid", " QID", StringComparison.Ordinal)
            .Replace(" Tmdb", " TMDB", StringComparison.Ordinal);
    }
}
