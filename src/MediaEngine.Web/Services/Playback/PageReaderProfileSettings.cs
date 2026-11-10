using MediaEngine.Contracts.Playback;

namespace MediaEngine.Web.Services.Playback;

/// <summary>What the page reader starts with for one title: the profile's remembered choices (PDF and comic reader spec section 5).</summary>
public sealed record PageReaderChoices(string Layout, string Fit, string Direction, string Background, bool ShowBookmarksOnBar);

/// <summary>
/// Reads and writes the page reader's choices on a profile's <see cref="ReadingSettingsDto"/>: layout per title, direction per Series
/// (the Series name in lower case), and fit, background and the bookmark marks per profile.
/// </summary>
public static class PageReaderProfileSettings
{
    public const string Auto = ReadingPreferenceValues.LayoutAuto;

    public static PageReaderChoices ChoicesFor(ReadingSettingsDto? reading, Guid assetId, string? series)
    {
        if (reading is null)
        {
            return new PageReaderChoices(Auto, ReadingPreferenceValues.PageFitPage, Auto, ReadingPreferenceValues.PageBackgroundBlack, false);
        }

        var layout = reading.PageLayouts.TryGetValue(LayoutKey(assetId), out var l) ? l : Auto;
        var direction = !string.IsNullOrWhiteSpace(series) && reading.ComicDirections.TryGetValue(SeriesKey(series), out var d) ? d : Auto;
        return new PageReaderChoices(layout, reading.PageFit, direction, reading.PageBackground, reading.ShowBookmarksOnBar);
    }

    /// <summary>Remembers a layout for this title; "auto" (the default) removes the entry so the profile does not fill up.</summary>
    public static void SetLayout(ReadingSettingsDto reading, Guid assetId, string layout)
    {
        ArgumentNullException.ThrowIfNull(reading);
        var key = LayoutKey(assetId);
        if (layout == Auto)
        {
            reading.PageLayouts.Remove(key);
        }
        else
        {
            reading.PageLayouts[key] = layout;
        }
    }

    /// <summary>Remembers a direction for the whole Series; "auto" removes it so the comic's own information decides again.</summary>
    public static void SetDirection(ReadingSettingsDto reading, string? series, string direction)
    {
        ArgumentNullException.ThrowIfNull(reading);
        if (string.IsNullOrWhiteSpace(series))
        {
            return;
        }

        var key = SeriesKey(series);
        if (direction == Auto)
        {
            reading.ComicDirections.Remove(key);
        }
        else
        {
            reading.ComicDirections[key] = direction;
        }
    }

    public static string LayoutKey(Guid assetId) => assetId.ToString("D");

    public static string SeriesKey(string series) => series.Trim().ToLowerInvariant();
}
