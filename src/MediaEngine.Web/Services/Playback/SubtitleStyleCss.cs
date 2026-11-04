using System.Globalization;
using MediaEngine.Contracts.Playback;

namespace MediaEngine.Web.Services.Playback;

/// <summary>
/// Turns a profile's subtitle look (colours and opacities, unified player D12) into the CSS values the player cues and the
/// style menu's live preview read, so the real picture and the preview can never disagree.
/// </summary>
public static class SubtitleStyleCss
{
    /// <summary>The swatch colour for a stored colour name, as red, green and blue; an unknown name gives the fallback colour.</summary>
    public static (int R, int G, int B) Rgb(string? colour, string fallback = PlaybackPreferenceValues.ColorWhite) =>
        Lookup(colour) ?? Lookup(fallback) ?? (255, 255, 255);

    private static (int R, int G, int B)? Lookup(string? colour) => colour?.Trim().ToLowerInvariant() switch
    {
        "white" => (255, 255, 255),
        "black" => (0, 0, 0),
        "blue" => (79, 139, 255),
        "teal" => (45, 212, 191),
        "green" => (74, 222, 128),
        "yellow" => (255, 224, 102),
        "red" => (255, 92, 92),
        "magenta" => (232, 121, 249),
        _ => null,
    };

    /// <summary>How solid the text is, from 0.2 to 1.</summary>
    public static double TextOpacity(SubtitleLanguageSettingsDto? look) =>
        Math.Clamp(look?.SubtitleTextOpacity ?? PlaybackPreferenceValues.DefaultTextOpacity, PlaybackPreferenceValues.MinTextOpacity, 100) / 100d;

    /// <summary>How solid the backing is, from 0 to 1. The Background switch off means none, whatever the slider last said.</summary>
    public static double BackgroundOpacity(SubtitleLanguageSettingsDto? look) =>
        look?.SubtitleBackground == false
            ? 0d
            : Math.Clamp(look?.SubtitleBackgroundOpacity ?? PlaybackPreferenceValues.DefaultBackgroundOpacity, 0, 100) / 100d;

    /// <summary>The text colour with its opacity, as a CSS <c>rgb()</c> value.</summary>
    public static string TextColor(SubtitleLanguageSettingsDto? look) =>
        Rgba(Rgb(look?.SubtitleColor), TextOpacity(look));

    /// <summary>The backing colour with its opacity, as a CSS <c>rgb()</c> value (<c>transparent</c> when there is none).</summary>
    public static string BackgroundColor(SubtitleLanguageSettingsDto? look)
    {
        var opacity = BackgroundOpacity(look);
        return opacity <= 0 ? "transparent" : Rgba(Rgb(look?.SubtitleBackgroundColor, PlaybackPreferenceValues.ColorBlack), opacity);
    }

    /// <summary>A stored colour name as one of the eight known names (any case in, canonical case out); anything else is the fallback.</summary>
    public static string ColorName(string? colour, string fallback) =>
        PlaybackPreferenceValues.SubtitleColors.FirstOrDefault(name => string.Equals(name, colour?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? fallback;

    /// <summary>The slider stop (a percent) closest to an opacity between 0 and 1.</summary>
    public static int NearestStop(IReadOnlyList<int> stops, double opacity) =>
        stops.OrderBy(stop => Math.Abs(stop - opacity * 100)).First();

    private static string Rgba((int R, int G, int B) rgb, double opacity) =>
        string.Create(CultureInfo.InvariantCulture, $"rgb({rgb.R} {rgb.G} {rgb.B} / {Math.Round(opacity * 100)}%)");
}
