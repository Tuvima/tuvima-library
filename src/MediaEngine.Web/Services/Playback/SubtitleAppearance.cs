using MediaEngine.Contracts.Playback;

namespace MediaEngine.Web.Services.Playback;

/// <summary>The three style presets of the Subtitle appearance view (unified player v2 D13, mockup screen 4).</summary>
public enum SubtitlePreset
{
    Default,
    LargeText,
    HighContrast,
}

/// <summary>The background choices of the Subtitle appearance view: no backing, a soft shadow, or a box behind the text.</summary>
public enum SubtitleBackdrop
{
    None,
    Shadow,
    Box,
}

/// <summary>
/// Maps the simple choices of Subtitle appearance (presets, Background) onto the profile's stored subtitle look
/// (<see cref="SubtitleLanguageSettingsDto"/>, read by <see cref="SubtitleStyleCss"/>). A preset sets the text treatment only and Background is a
/// separate choice (unified player follow-ups D8); a look whose text matches no preset (any custom value) ticks none.
/// </summary>
public static class SubtitleAppearance
{
    /// <summary>
    /// Writes the preset's text treatment over the look: size, font, colour and text opacity, plus the high-contrast marker. A preset never touches
    /// the Background choice (<c>SubtitleBackground</c> and the shadow), so None, Shadow and Box stay as they were (D8).
    /// </summary>
    public static void Apply(SubtitleLanguageSettingsDto look, SubtitlePreset preset)
    {
        look.SubtitleSize = preset == SubtitlePreset.LargeText ? PlaybackPreferenceValues.Large : PlaybackPreferenceValues.Medium;
        look.SubtitleFont = PlaybackPreferenceValues.FontDefault;
        look.SubtitleColor = PlaybackPreferenceValues.ColorWhite;
        look.SubtitleTextOpacity = PlaybackPreferenceValues.DefaultTextOpacity;
        // The box, when there is one, is black: solid for High contrast (which is also the marker that survives Background None and Shadow), the normal 60 percent otherwise.
        look.SubtitleBackgroundColor = PlaybackPreferenceValues.ColorBlack;
        look.SubtitleBackgroundOpacity = preset == SubtitlePreset.HighContrast ? HighContrastOpacity : PlaybackPreferenceValues.DefaultBackgroundOpacity;
    }

    /// <summary>The Default preset on the 60 percent black box, which is also what Reset to default restores.</summary>
    public static void Reset(SubtitleLanguageSettingsDto look)
    {
        Apply(look, SubtitlePreset.Default);
        look.SubtitleShadow = false;
        look.SubtitleBackground = true;
    }

    /// <summary>The stored background opacity that marks the High contrast preset; it survives Background None and Shadow because those only switch the box off.</summary>
    public const int HighContrastOpacity = 100;

    /// <summary>The preset whose values the look has exactly, or null for a custom look.</summary>
    public static SubtitlePreset? Matching(SubtitleLanguageSettingsDto? look)
    {
        foreach (var preset in Enum.GetValues<SubtitlePreset>())
        {
            var expected = new SubtitleLanguageSettingsDto();
            Apply(expected, preset);
            if (SameLook(look ?? new SubtitleLanguageSettingsDto(), expected))
            {
                return preset;
            }
        }

        return null;
    }

    /// <summary>The Text size choice (Small, Medium or Large); Extra large counts as Large.</summary>
    public static string SizeChoice(SubtitleLanguageSettingsDto? look) => look?.SubtitleSize switch
    {
        var size when string.Equals(size, PlaybackPreferenceValues.Small, StringComparison.OrdinalIgnoreCase) => PlaybackPreferenceValues.Small,
        var size when string.Equals(size, PlaybackPreferenceValues.Large, StringComparison.OrdinalIgnoreCase)
            || string.Equals(size, PlaybackPreferenceValues.ExtraLarge, StringComparison.OrdinalIgnoreCase) => PlaybackPreferenceValues.Large,
        _ => PlaybackPreferenceValues.Medium,
    };

    /// <summary>A box when the backing is on; otherwise a soft shadow when the shadow is on, or nothing. Reads the choice itself, not the opacity.</summary>
    public static SubtitleBackdrop BackdropChoice(SubtitleLanguageSettingsDto? look) =>
        look?.SubtitleBackground != false
            ? SubtitleBackdrop.Box
            : look?.SubtitleShadow == true ? SubtitleBackdrop.Shadow : SubtitleBackdrop.None;

    /// <summary>
    /// Sets the background choice. It only switches the box and the shadow, so the stored opacity (and with it the High contrast marker) is kept.
    /// Choosing Box restores a visible backing if the stored opacity had been slid down to none.
    /// </summary>
    public static void ApplyBackdrop(SubtitleLanguageSettingsDto look, SubtitleBackdrop backdrop)
    {
        look.SubtitleBackground = backdrop == SubtitleBackdrop.Box;
        look.SubtitleShadow = backdrop == SubtitleBackdrop.Shadow;
        if (backdrop == SubtitleBackdrop.Box && look.SubtitleBackgroundOpacity <= 0)
        {
            look.SubtitleBackgroundOpacity = PlaybackPreferenceValues.DefaultBackgroundOpacity;
        }
    }

    private static string RawSize(SubtitleLanguageSettingsDto look) =>
        string.IsNullOrWhiteSpace(look.SubtitleSize) ? PlaybackPreferenceValues.Medium : look.SubtitleSize.Trim();

    // Only the text treatment is compared: the Background choice (box, shadow, none) and the box colour belong to Background, not to the style (D8).
    private static bool SameLook(SubtitleLanguageSettingsDto left, SubtitleLanguageSettingsDto right) =>
        // The raw size, not the three-way choice: Extra large shows as Large in the Custom appearance control but is not the Large text preset (D13).
        string.Equals(RawSize(left), RawSize(right), StringComparison.OrdinalIgnoreCase)
        && string.Equals(left.SubtitleFont, right.SubtitleFont, StringComparison.OrdinalIgnoreCase)
        && SubtitleStyleCss.ColorName(left.SubtitleColor, string.Empty) == SubtitleStyleCss.ColorName(right.SubtitleColor, string.Empty)
        && Math.Abs(SubtitleStyleCss.TextOpacity(left) - SubtitleStyleCss.TextOpacity(right)) < 0.005
        && SubtitleStyleCss.IsHighContrast(left) == SubtitleStyleCss.IsHighContrast(right);
}
