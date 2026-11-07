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
/// Maps the simple choices of Subtitle appearance (presets, Text size, Background) onto the profile's stored subtitle look
/// (<see cref="SubtitleLanguageSettingsDto"/>, read by <see cref="SubtitleStyleCss"/>). A preset sets the underlying values; a look that
/// matches no preset (any custom value) ticks none.
/// </summary>
public static class SubtitleAppearance
{
    /// <summary>Writes the preset's values over the look.</summary>
    public static void Apply(SubtitleLanguageSettingsDto look, SubtitlePreset preset)
    {
        Reset(look);
        switch (preset)
        {
            case SubtitlePreset.LargeText:
                look.SubtitleSize = PlaybackPreferenceValues.Large;
                break;
            case SubtitlePreset.HighContrast:
                // White text on a solid black box: the strongest contrast the colors allow.
                look.SubtitleBackgroundOpacity = 100;
                break;
        }
    }

    /// <summary>The Default preset: medium white text on the 60 percent black box, which is also what Reset to default restores.</summary>
    public static void Reset(SubtitleLanguageSettingsDto look)
    {
        look.SubtitleSize = PlaybackPreferenceValues.Medium;
        look.SubtitleFont = PlaybackPreferenceValues.FontDefault;
        look.SubtitleShadow = false;
        look.SubtitleColor = PlaybackPreferenceValues.ColorWhite;
        look.SubtitleTextOpacity = PlaybackPreferenceValues.DefaultTextOpacity;
        look.SubtitleBackground = true;
        look.SubtitleBackgroundColor = PlaybackPreferenceValues.ColorBlack;
        look.SubtitleBackgroundOpacity = PlaybackPreferenceValues.DefaultBackgroundOpacity;
    }

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

    /// <summary>A box when the backing is on; otherwise a soft shadow when the shadow is on, or nothing.</summary>
    public static SubtitleBackdrop BackdropChoice(SubtitleLanguageSettingsDto? look) =>
        SubtitleStyleCss.BackgroundOpacity(look) > 0
            ? SubtitleBackdrop.Box
            : look?.SubtitleShadow == true ? SubtitleBackdrop.Shadow : SubtitleBackdrop.None;

    /// <summary>Sets the background choice. Choosing Box restores a visible backing if the stored opacity had been slid down to none.</summary>
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

    private static bool SameLook(SubtitleLanguageSettingsDto left, SubtitleLanguageSettingsDto right) =>
        // The raw size, not the three-way choice: Extra large shows as Large in the segmented control but is not the Large text preset (D13).
        string.Equals(RawSize(left), RawSize(right), StringComparison.OrdinalIgnoreCase)
        && string.Equals(left.SubtitleFont, right.SubtitleFont, StringComparison.OrdinalIgnoreCase)
        && left.SubtitleShadow == right.SubtitleShadow
        && SubtitleStyleCss.ColorName(left.SubtitleColor, string.Empty) == SubtitleStyleCss.ColorName(right.SubtitleColor, string.Empty)
        && Math.Abs(SubtitleStyleCss.TextOpacity(left) - SubtitleStyleCss.TextOpacity(right)) < 0.005
        && SubtitleStyleCss.BackgroundColor(left) == SubtitleStyleCss.BackgroundColor(right);
}
