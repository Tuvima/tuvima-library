using MediaEngine.Contracts.Playback;
using MediaEngine.Web.Services.Playback;

namespace MediaEngine.Web.Tests;

public sealed class SubtitleStyleCssTests
{
    [Fact]
    public void ADefaultProfile_LooksLikeBefore_WhiteTextOnA60PercentBlackBacking()
    {
        var look = new SubtitleLanguageSettingsDto();

        Assert.Equal("rgb(255 255 255 / 100%)", SubtitleStyleCss.TextColor(look));
        Assert.Equal("rgb(0 0 0 / 60%)", SubtitleStyleCss.BackgroundColor(look));
    }

    [Fact]
    public void EveryOfferedColour_HasItsOwnSwatch()
    {
        var swatches = PlaybackPreferenceValues.SubtitleColors.Select(name => SubtitleStyleCss.Rgb(name)).Distinct().ToList();

        Assert.Equal(8, PlaybackPreferenceValues.SubtitleColors.Count);
        Assert.Equal(8, swatches.Count);
        Assert.Equal(PlaybackPreferenceValues.ColorRed, SubtitleStyleCss.ColorName("RED", PlaybackPreferenceValues.ColorWhite));
        Assert.Equal(PlaybackPreferenceValues.ColorWhite, SubtitleStyleCss.ColorName("Plaid", PlaybackPreferenceValues.ColorWhite));
    }

    [Fact]
    public void TheTextAndBackgroundOpacity_ChangeTheCssColours()
    {
        var look = new SubtitleLanguageSettingsDto
        {
            SubtitleColor = PlaybackPreferenceValues.ColorYellow,
            SubtitleTextOpacity = 40,
            SubtitleBackgroundColor = PlaybackPreferenceValues.ColorBlue,
            SubtitleBackgroundOpacity = 80,
        };

        Assert.Equal("rgb(255 224 102 / 40%)", SubtitleStyleCss.TextColor(look));
        Assert.Equal("rgb(79 139 255 / 80%)", SubtitleStyleCss.BackgroundColor(look));
    }

    [Fact]
    public void WithTheBackgroundSwitchedOff_ThereIsNoBacking_WhateverTheSliderLastSaid()
    {
        var look = new SubtitleLanguageSettingsDto { SubtitleBackground = false, SubtitleBackgroundOpacity = 80 };

        Assert.Equal(0d, SubtitleStyleCss.BackgroundOpacity(look));
        Assert.Equal("transparent", SubtitleStyleCss.BackgroundColor(look));
    }

    [Fact]
    public void AnOpacityBetweenStops_ShowsTheNearestStop()
    {
        Assert.Equal(60, SubtitleStyleCss.NearestStop(PlaybackPreferenceValues.BackgroundOpacityStops, 0.55));
        Assert.Equal(20, SubtitleStyleCss.NearestStop(PlaybackPreferenceValues.TextOpacityStops, 0.0));
    }
}
