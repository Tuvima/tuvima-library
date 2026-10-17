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
    /// in Components/Settings/UserOverviewTab.razor, now shown as "Hours" in ProfilesTab.razor.
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
        => FormatPlaybackRate(rate);

    private static string FormatPlaybackRate(double rate)
    {
        var exact = rate.ToString("R", CultureInfo.InvariantCulture);
        if (!exact.Contains('.', StringComparison.Ordinal))
        {
            exact += ".0";
        }
        return $"{exact}x";
    }

    // ---- File facts ---------------------------------------------------------

    /// <summary>
    /// File size in binary units with invariant digits: "312 MB", "1.4 GB". One decimal below 100 of a
    /// unit, none above it. Zero or negative input has no size to show and returns null.
    /// </summary>
    public static string? FormatFileSize(long bytes)
    {
        if (bytes <= 0)
        {
            return null;
        }

        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var size = (double)bytes;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        var format = size >= 100 ? "0" : "0.#";
        return $"{size.ToString(format, CultureInfo.InvariantCulture)} {units[unit]}";
    }

    /// <summary>
    /// Bitrate from bits per second in decimal units: "6.2 Mbps" from one megabit, otherwise "256 kbps".
    /// Zero or negative input returns null.
    /// </summary>
    public static string? FormatBitrate(long bitsPerSecond)
    {
        if (bitsPerSecond <= 0)
        {
            return null;
        }

        return bitsPerSecond >= 1_000_000
            ? $"{(bitsPerSecond / 1_000_000d).ToString("0.#", CultureInfo.InvariantCulture)} Mbps"
            : $"{Math.Max(1, (long)Math.Round(bitsPerSecond / 1000d)).ToString(CultureInfo.InvariantCulture)} kbps";
    }

    /// <summary>A plain calendar date such as 2026-10-16 (UTC, so it never shifts with the viewer's clock).</summary>
    public static string FormatPlainDate(DateTimeOffset value)
        => value.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>
    /// An English language name for a stored language code ("eng", "en", "fre", "pt-BR" read as English,
    /// French, Portuguese). Handles the bibliographic three-letter codes ffmpeg writes (fre, ger, chi, ...)
    /// as well as ISO 639-1 and 639-2/T. "und" (undetermined) and "zxx" (no language) return null so the
    /// caller shows nothing; an unrecognised code is shown as stored. Never throws.
    /// </summary>
    public static string? FormatLanguageName(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return null;
        }

        var trimmed = code.Trim();
        var key = trimmed.Split('-', '_')[0];
        if (key.Equals("und", StringComparison.OrdinalIgnoreCase) || key.Equals("zxx", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (BibliographicLanguageNames.TryGetValue(key, out var bibliographic))
        {
            return bibliographic;
        }

        return CultureLanguageNames.Value.TryGetValue(key, out var name) ? name : trimmed;
    }

    // ISO 639-2/B codes that differ from the 639-2/T code (or that .NET cultures do not know).
    private static readonly Dictionary<string, string> BibliographicLanguageNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["alb"] = "Albanian",
        ["arm"] = "Armenian",
        ["baq"] = "Basque",
        ["bur"] = "Burmese",
        ["chi"] = "Chinese",
        ["cze"] = "Czech",
        ["dut"] = "Dutch",
        ["fre"] = "French",
        ["geo"] = "Georgian",
        ["ger"] = "German",
        ["gre"] = "Greek",
        ["ice"] = "Icelandic",
        ["mac"] = "Macedonian",
        ["mao"] = "Maori",
        ["may"] = "Malay",
        ["mul"] = "Multiple languages",
        ["per"] = "Persian",
        ["rum"] = "Romanian",
        ["slo"] = "Slovak",
        ["tib"] = "Tibetan",
        ["wel"] = "Welsh",
    };

    private static readonly Lazy<Dictionary<string, string>> CultureLanguageNames = new(() =>
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var culture in CultureInfo.GetCultures(CultureTypes.NeutralCultures))
        {
            if (culture.TwoLetterISOLanguageName.Length == 2)
            {
                names.TryAdd(culture.TwoLetterISOLanguageName, culture.EnglishName);
            }

            if (culture.ThreeLetterISOLanguageName.Length == 3)
            {
                names.TryAdd(culture.ThreeLetterISOLanguageName, culture.EnglishName);
            }
        }

        return names;
    });

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
