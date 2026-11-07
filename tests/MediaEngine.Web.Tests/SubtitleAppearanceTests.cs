using System.Text.Json;
using Bunit;
using MediaEngine.Contracts.Playback;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Components.Watch;
using MediaEngine.Web.Services.Playback;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>Unified player v2 D13 (mockup screen 4): Subtitle appearance, its presets, Text size and Background, Custom appearance, Reset and the saved look.</summary>
public sealed class SubtitleAppearanceTests : AsyncBunitContext
{
    private readonly PlaybackTransientToolCoordinator _tools = new();
    private readonly SubtitleLanguageSettingsDto _look = new();
    private int _changes;

    public SubtitleAppearanceTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        Services.AddNativeUiServices();
        Services.AddSingleton(_tools);
        Render<AppPopoverHost>();
    }

    private async Task<IRenderedComponent<AudioSubtitlesPanel>> OpenAppearanceAsync()
    {
        var cut = Render<AudioSubtitlesPanel>(parameters => parameters
            .Add(p => p.Audio, [])
            .Add(p => p.Subtitles, [new SubtitleChoice(null, "Off", true)])
            .Add(p => p.Look, _look)
            .Add(p => p.StyleChanged, change => { change(_look); _changes++; return Task.CompletedTask; }));
        await cut.InvokeAsync(() => cut.FindComponent<PlaybackPanel>().Instance.OpenAsync(true));
        cut.WaitForElement("[data-audio-subtitles]");
        await cut.Find("[data-asa-action='appearance']").ClickAsync();
        cut.WaitForElement("[data-subtitle-appearance]");
        return cut;
    }

    private static string? PresetOn(IRenderedComponent<AudioSubtitlesPanel> cut) =>
        cut.FindAll("[data-sav-preset]").SingleOrDefault(tile => tile.GetAttribute("aria-checked") == "true")?.GetAttribute("data-sav-preset");

    [Fact]
    public async Task SubtitleAppearance_OpensAsANestedViewWithABackArrowAndTheSavedNoteInTheFooter()
    {
        var cut = await OpenAppearanceAsync();

        Assert.Equal("Subtitle appearance", cut.Find("header h2").TextContent);
        Assert.NotNull(cut.Find("button[aria-label='Back']"));
        Assert.Contains("This is how your subtitles will look", cut.Find(".sav__preview").TextContent);
        Assert.Equal("Style", cut.Find(".sav__heading").TextContent);
        var footer = cut.Find("footer.playback-panel__footer");
        Assert.Equal("Reset to default", footer.QuerySelector("[data-asp-action='reset']")!.TextContent.Trim());
        Assert.Equal("Changes saved automatically", footer.QuerySelector(".asp-footer__note")!.TextContent);
        await cut.Find("button[aria-label='Back']").ClickAsync();
        Assert.Equal("Audio & subtitles", cut.Find("header h2").TextContent);
    }

    [Fact]
    public async Task ThreePresetsAreOfferedAndDefaultIsTickedForTheDefaultLook()
    {
        var cut = await OpenAppearanceAsync();

        Assert.Equal(["default", "large", "contrast"], cut.FindAll("[data-sav-preset]").Select(tile => tile.GetAttribute("data-sav-preset")));
        Assert.Equal(["Default", "Large text", "High contrast"], cut.FindAll(".sav__tile-label").Select(label => label.TextContent));
        Assert.Equal("default", PresetOn(cut));
        Assert.Single(cut.FindAll(".sav__tick"));
    }

    [Fact]
    public async Task ChoosingAPreset_SetsTheUnderlyingValuesAndTicksOnlyThatTile()
    {
        var cut = await OpenAppearanceAsync();

        await cut.Find("[data-sav-preset='large']").ClickAsync();

        Assert.Equal(PlaybackPreferenceValues.Large, _look.SubtitleSize);
        Assert.Equal("large", PresetOn(cut));
        await cut.Find("[data-sav-preset='contrast']").ClickAsync();
        Assert.Equal(PlaybackPreferenceValues.Medium, _look.SubtitleSize);
        Assert.Equal(PlaybackPreferenceValues.ColorWhite, _look.SubtitleColor);
        Assert.Equal(PlaybackPreferenceValues.ColorBlack, _look.SubtitleBackgroundColor);
        Assert.Equal(100, _look.SubtitleBackgroundOpacity);
        Assert.True(_look.SubtitleBackground);
        Assert.Equal("contrast", PresetOn(cut));
        Assert.Equal(2, _changes);
    }

    [Fact]
    public async Task ACustomValue_TicksNoPreset()
    {
        var cut = await OpenAppearanceAsync();

        await cut.Find(".sav__row--first .app-segmented-control__item:first-child").ClickAsync();

        Assert.Equal(PlaybackPreferenceValues.Small, _look.SubtitleSize);
        Assert.Null(PresetOn(cut));
        Assert.Empty(cut.FindAll(".sav__tick"));
    }

    [Fact]
    public async Task TextSizeAndBackground_AreSegmentedControlsThatReflectTheLook()
    {
        var cut = await OpenAppearanceAsync();

        var rows = cut.FindAll(".sav__row");
        Assert.Equal(["Small", "Medium", "Large"], rows[0].QuerySelectorAll(".app-segmented-control__item").Select(item => item.TextContent.Trim()));
        Assert.Equal(["None", "Shadow", "Box"], rows[1].QuerySelectorAll(".app-segmented-control__item").Select(item => item.TextContent.Trim()));
        Assert.Equal("true", rows[0].QuerySelectorAll(".app-segmented-control__item")[1].GetAttribute("aria-pressed"));
        Assert.Equal("true", rows[1].QuerySelectorAll(".app-segmented-control__item")[2].GetAttribute("aria-pressed"));

        await rows[1].QuerySelectorAll(".app-segmented-control__item")[1].ClickAsync();

        Assert.False(_look.SubtitleBackground);
        Assert.True(_look.SubtitleShadow);
        Assert.Equal("true", cut.FindAll(".sav__row")[1].QuerySelectorAll(".app-segmented-control__item")[1].GetAttribute("aria-pressed"));
        Assert.Null(PresetOn(cut));
    }

    [Fact]
    public async Task ThePreviewFollowsTheLookAtOnce()
    {
        var cut = await OpenAppearanceAsync();
        Assert.Contains("font-size:16px", cut.Find(".sav__preview-line").GetAttribute("style"));

        await cut.Find("[data-sav-preset='large']").ClickAsync();

        Assert.Contains("font-size:21px", cut.Find(".sav__preview-line").GetAttribute("style"));
        await cut.Find(".sav__row:not(.sav__row--first) .app-segmented-control__item:first-child").ClickAsync();
        Assert.Contains("background:transparent", cut.Find(".sav__preview-line").GetAttribute("style"));
    }

    [Fact]
    public async Task CustomAppearance_OpensTheFullChoicesOneLevelDeeperAndTheBackArrowReturnsOneLevel()
    {
        var cut = await OpenAppearanceAsync();
        Assert.Equal("Font, colors and opacity", cut.Find(".sav__custom-sub").TextContent);

        await cut.Find("[data-sav-custom]").ClickAsync();

        Assert.Equal("Custom appearance", cut.Find("header h2").TextContent);
        var titles = cut.FindAll(".player-settings-menu__group > h4").Select(title => title.TextContent).ToArray();
        Assert.Equal(["Font", "Shadow", "Text color", "Text opacity", "Background color", "Background opacity"], titles);
        Assert.Equal("Reset to default", cut.Find("footer [data-asp-action='reset']").TextContent.Trim());

        await cut.Find("[aria-label='Font: Serif']").ClickAsync();
        Assert.Equal(PlaybackPreferenceValues.FontSerif, _look.SubtitleFont);

        await cut.Find("button[aria-label='Back']").ClickAsync();
        Assert.Equal("Subtitle appearance", cut.Find("header h2").TextContent);
        Assert.Null(PresetOn(cut));
    }

    [Fact]
    public async Task ResetToDefault_PutsEveryValueBackAndTicksDefault()
    {
        var cut = await OpenAppearanceAsync();
        await cut.Find("[data-sav-preset='contrast']").ClickAsync();
        await cut.Find(".sav__row--first .app-segmented-control__item:last-child").ClickAsync();

        await cut.Find("[data-asp-action='reset']").ClickAsync();

        Assert.Equal(new SubtitleLanguageSettingsDto().SubtitleSize, _look.SubtitleSize);
        Assert.Equal(PlaybackPreferenceValues.DefaultBackgroundOpacity, _look.SubtitleBackgroundOpacity);
        Assert.Equal("default", PresetOn(cut));
    }

    [Theory]
    [InlineData(SubtitlePreset.Default)]
    [InlineData(SubtitlePreset.LargeText)]
    [InlineData(SubtitlePreset.HighContrast)]
    public void APreset_RoundTripsThroughTheStoredPreferenceModel(SubtitlePreset preset)
    {
        var look = new SubtitleLanguageSettingsDto { SubtitleSize = PlaybackPreferenceValues.Small, SubtitleFont = PlaybackPreferenceValues.FontMono };
        SubtitleAppearance.Apply(look, preset);

        var saved = JsonSerializer.Deserialize<SubtitleLanguageSettingsDto>(JsonSerializer.Serialize(look))!;

        Assert.Equal(preset, SubtitleAppearance.Matching(saved));
        Assert.Equal(SubtitleStyleCss.TextColor(look), SubtitleStyleCss.TextColor(saved));
        Assert.Equal(SubtitleStyleCss.BackgroundColor(look), SubtitleStyleCss.BackgroundColor(saved));
    }

    [Fact]
    public void ACustomValueSavedOnAPreset_RoundTripsAsNoPreset()
    {
        var look = new SubtitleLanguageSettingsDto();
        SubtitleAppearance.Apply(look, SubtitlePreset.LargeText);
        look.SubtitleColor = PlaybackPreferenceValues.ColorYellow;

        var saved = JsonSerializer.Deserialize<SubtitleLanguageSettingsDto>(JsonSerializer.Serialize(look))!;

        Assert.Null(SubtitleAppearance.Matching(saved));
        Assert.Equal(PlaybackPreferenceValues.ColorYellow, saved.SubtitleColor);
    }

    [Theory]
    [InlineData(SubtitleBackdrop.None, false, false)]
    [InlineData(SubtitleBackdrop.Shadow, false, true)]
    [InlineData(SubtitleBackdrop.Box, true, false)]
    public void TheBackgroundChoices_MapOntoTheBackingAndTheShadow(SubtitleBackdrop backdrop, bool backing, bool shadow)
    {
        var look = new SubtitleLanguageSettingsDto();

        SubtitleAppearance.ApplyBackdrop(look, backdrop);

        Assert.Equal(backing, look.SubtitleBackground);
        Assert.Equal(shadow, look.SubtitleShadow);
        Assert.Equal(backdrop, SubtitleAppearance.BackdropChoice(look));
    }

    [Fact]
    public void ChoosingBox_RestoresAVisibleBackingWhenTheStoredOpacityWasSlidToNone()
    {
        var look = new SubtitleLanguageSettingsDto { SubtitleBackground = false, SubtitleBackgroundOpacity = 0 };

        SubtitleAppearance.ApplyBackdrop(look, SubtitleBackdrop.Box);

        Assert.True(SubtitleStyleCss.BackgroundOpacity(look) > 0);
    }

    [Fact]
    public void ANullLook_ReadsAsTheDefaultPresetWithMediumTextAndABox()
    {
        Assert.Equal(SubtitlePreset.Default, SubtitleAppearance.Matching(null));
        Assert.Equal(PlaybackPreferenceValues.Medium, SubtitleAppearance.SizeChoice(null));
        Assert.Equal(SubtitleBackdrop.Box, SubtitleAppearance.BackdropChoice(null));
    }

    [Fact]
    public void AnExtraLargeLook_TicksNoPresetEvenThoughTheSizeControlShowsLarge()
    {
        var look = new SubtitleLanguageSettingsDto();
        SubtitleAppearance.Apply(look, SubtitlePreset.LargeText);
        look.SubtitleSize = PlaybackPreferenceValues.ExtraLarge;

        Assert.Equal(PlaybackPreferenceValues.Large, SubtitleAppearance.SizeChoice(look));
        Assert.Null(SubtitleAppearance.Matching(look));
    }

    [Fact]
    public async Task TheStylePresets_AreOneTabStopWithTheTickedTileHoldingIt()
    {
        var cut = await OpenAppearanceAsync();

        var tiles = cut.FindAll("[data-sav-preset]");
        Assert.Equal(["default"], tiles.Where(tile => tile.GetAttribute("tabindex") == "0").Select(tile => tile.GetAttribute("data-sav-preset")));
        await cut.Find("[data-sav-preset='large']").ClickAsync();
        Assert.Equal(["large"], cut.FindAll("[data-sav-preset]").Where(tile => tile.GetAttribute("tabindex") == "0").Select(tile => tile.GetAttribute("data-sav-preset")));
        // A custom look ticks no tile; the first tile then holds the one Tab stop.
        await cut.Find(".sav__row:not(.sav__row--first) .app-segmented-control__item:first-child").ClickAsync();
        Assert.Equal(["default"], cut.FindAll("[data-sav-preset]").Where(tile => tile.GetAttribute("tabindex") == "0").Select(tile => tile.GetAttribute("data-sav-preset")));
    }

    [Fact]
    public async Task TheCustomAppearanceOpacitySliders_UseThePlayersOwnSliderStyleWithPercentMarks()
    {
        var cut = await OpenAppearanceAsync();

        await cut.Find("[data-sav-custom]").ClickAsync();

        var sliders = cut.FindAll(".player-settings-menu__group--slider .playback-range-slider");
        Assert.Equal(2, sliders.Count);
        Assert.All(sliders, slider => Assert.Contains("playback-range-slider--player", slider.ClassName, StringComparison.Ordinal));
        Assert.Empty(cut.FindAll(".player-settings-menu__group--slider .app-slider"));
        Assert.Equal(["20%", "40%", "60%", "80%", "100%"],
            cut.FindAll(".player-settings-menu__group--slider")[0].QuerySelectorAll(".playback-range-slider__label").Select(label => label.TextContent.Trim()));
    }

    [Fact]
    public void TheSettingsPreview_FollowsTheShadowSwitchLikeThePlayerDoes()
    {
        var css = File.ReadAllText(Path.Combine(FindRepoRoot(), "src/MediaEngine.Web/wwwroot/app.css"));
        var page = File.ReadAllText(Path.Combine(FindRepoRoot(), "src/MediaEngine.Web/Components/Settings/PlaybackSettingsPage.razor"));

        Assert.Contains(".subtitle-preview[data-subtitle-shadow=\"true\"] { --subtitle-preview-shadow: 0 1px 3px black, 0 0 6px black; }", css, StringComparison.Ordinal);
        Assert.Contains("data-subtitle-shadow=\"@(Subtitles.SubtitleShadow", page, StringComparison.Ordinal);
        Assert.DoesNotContain("subtitle-preview[data-subtitle-background=\"false\"] { --subtitle-preview-bg: transparent; --subtitle-preview-shadow", css, StringComparison.Ordinal);
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MediaEngine.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }
}
