using MediaEngine.Contracts.Playback;
using MediaEngine.Web.Models.ViewDTOs;

namespace MediaEngine.Web.Services.Playback;

/// <summary>
/// Maps a profile's <see cref="ReadingSettingsDto"/> to and from the book reader's own appearance options
/// (<see cref="BookReaderOpenOptions"/>). The reader's layout is "paginated" or "scrolled"; the profile's
/// <see cref="ReadingSettingsDto.ReadingMode"/> is "Paginated" or "Scroll".
/// </summary>
public static class ReaderProfileSettings
{
    // The reader's own names for its six settings; these are also the keys the script merges per setting.
    public const string KeyTheme = "theme";
    public const string KeyFontSize = "fontSize";
    public const string KeyLineHeight = "lineHeight";
    public const string KeyFont = "font";
    public const string KeyPageWidth = "pageWidth";
    public const string KeyFlow = "flow";

    public const string FlowPaginated = "paginated";
    public const string FlowScrolled = "scrolled";

    /// <summary>The reader's appearance for these profile settings: the base that device-stored choices are merged onto.</summary>
    public static BookReaderOpenOptions ToOpenOptions(ReadingSettingsDto? reading)
    {
        if (reading is null)
        {
            return new BookReaderOpenOptions();
        }

        return new BookReaderOpenOptions
        {
            Theme = reading.Theme,
            Font = reading.Font,
            FontSize = Math.Clamp(reading.FontSizePx, ReadingPreferenceValues.MinFontSizePx, ReadingPreferenceValues.MaxFontSizePx),
            LineHeight = (double)Math.Clamp(reading.LineHeight, ReadingPreferenceValues.MinLineHeight, ReadingPreferenceValues.MaxLineHeight),
            PageWidth = reading.PageWidth,
            Flow = FlowFromReadingMode(reading.ReadingMode),
        };
    }

    /// <summary>The settings that differ between <paramref name="before"/> and <paramref name="after"/>, by the reader's key names.</summary>
    public static IReadOnlyList<string> ChangedKeys(BookReaderOpenOptions before, BookReaderOpenOptions after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);

        var changed = new List<string>();
        if (!string.Equals(before.Theme, after.Theme, StringComparison.Ordinal))
        {
            changed.Add(KeyTheme);
        }
        if (before.FontSize != after.FontSize)
        {
            changed.Add(KeyFontSize);
        }
        if (Math.Abs(before.LineHeight - after.LineHeight) > 0.0001)
        {
            changed.Add(KeyLineHeight);
        }
        if (!string.Equals(before.Font, after.Font, StringComparison.Ordinal))
        {
            changed.Add(KeyFont);
        }
        if (!string.Equals(before.PageWidth, after.PageWidth, StringComparison.Ordinal))
        {
            changed.Add(KeyPageWidth);
        }
        if (!string.Equals(before.Flow, after.Flow, StringComparison.Ordinal))
        {
            changed.Add(KeyFlow);
        }
        return changed;
    }

    /// <summary>Only the named settings of <paramref name="options"/>, keyed by the reader's names: what is written to this device.</summary>
    public static Dictionary<string, object> SelectKeys(BookReaderOpenOptions options, IEnumerable<string> keys)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(keys);

        var selected = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var key in keys)
        {
            switch (key)
            {
                case KeyTheme: selected[key] = options.Theme; break;
                case KeyFontSize: selected[key] = options.FontSize; break;
                case KeyLineHeight: selected[key] = options.LineHeight; break;
                case KeyFont: selected[key] = options.Font; break;
                case KeyPageWidth: selected[key] = options.PageWidth; break;
                case KeyFlow: selected[key] = options.Flow; break;
            }
        }

        return selected;
    }

    /// <summary>
    /// Writes the reader's appearance onto <paramref name="reading"/>, leaving every other reading field as it was.
    /// With <paramref name="keys"/> only those settings are written; without it all six are.
    /// </summary>
    public static void ApplyTo(ReadingSettingsDto reading, BookReaderOpenOptions options, IReadOnlyCollection<string>? keys = null)
    {
        ArgumentNullException.ThrowIfNull(reading);
        ArgumentNullException.ThrowIfNull(options);

        bool Wants(string key) => keys is null || keys.Contains(key);

        if (Wants(KeyTheme))
        {
            reading.Theme = options.Theme;
        }
        if (Wants(KeyFont))
        {
            reading.Font = options.Font;
        }
        if (Wants(KeyFontSize))
        {
            reading.FontSizePx = Math.Clamp(options.FontSize, ReadingPreferenceValues.MinFontSizePx, ReadingPreferenceValues.MaxFontSizePx);
        }

        if (Wants(KeyLineHeight))
        {
            reading.LineHeight = Math.Clamp(
                Math.Round((decimal)options.LineHeight, 2),
                ReadingPreferenceValues.MinLineHeight,
                ReadingPreferenceValues.MaxLineHeight);
        }

        if (Wants(KeyPageWidth))
        {
            reading.PageWidth = options.PageWidth;
        }
        if (Wants(KeyFlow))
        {
            reading.ReadingMode = ReadingModeFromFlow(options.Flow);
        }
    }

    public static string FlowFromReadingMode(string? readingMode) =>
        string.Equals(readingMode, PlaybackPreferenceValues.Scroll, StringComparison.OrdinalIgnoreCase) ? FlowScrolled : FlowPaginated;

    public static string ReadingModeFromFlow(string? flow) =>
        string.Equals(flow, FlowScrolled, StringComparison.OrdinalIgnoreCase) ? PlaybackPreferenceValues.Scroll : PlaybackPreferenceValues.Paginated;
}
