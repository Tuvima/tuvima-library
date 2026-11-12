using System.Text.Json;
using Bunit;
using MediaEngine.Contracts.Playback;
using MediaEngine.Web.Components.Player;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Playback;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>Unified player v2 D13 (mockup screen 4): Subtitle appearance, its presets and Background (independent of each other, follow-ups D7 and D8), Custom appearance with Text size, Reset and the saved look.</summary>
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
    public async Task ACustomTextValue_TicksNoPreset()
    {
        var cut = await OpenAppearanceAsync();
        await cut.Find("[data-sav-custom]").ClickAsync();

        await cut.Find("[aria-label='Text size: Small']").ClickAsync();
        await cut.Find("button[aria-label='Back']").ClickAsync();

        Assert.Equal(PlaybackPreferenceValues.Small, _look.SubtitleSize);
        Assert.Null(PresetOn(cut));
        Assert.Empty(cut.FindAll(".sav__tick"));
    }

    [Fact]
    public async Task Background_IsTheOnlySegmentedControlAndReflectsTheLook()
    {
        var cut = await OpenAppearanceAsync();

        var rows = cut.FindAll(".sav__row");
        Assert.Single(rows);
        Assert.Equal(["None", "Shadow", "Box"], rows[0].QuerySelectorAll(".app-segmented-control__item").Select(item => item.TextContent.Trim()));
        Assert.Equal("true", rows[0].QuerySelectorAll(".app-segmented-control__item")[2].GetAttribute("aria-pressed"));

        await rows[0].QuerySelectorAll(".app-segmented-control__item")[1].ClickAsync();

        Assert.False(_look.SubtitleBackground);
        Assert.True(_look.SubtitleShadow);
        Assert.Equal("true", cut.Find(".sav__row").QuerySelectorAll(".app-segmented-control__item")[1].GetAttribute("aria-pressed"));
    }

    [Fact]
    public async Task TheSubtitleAppearanceView_HasNoTextSizeRowAndCustomAppearanceHasOne()
    {
        var cut = await OpenAppearanceAsync();

        Assert.DoesNotContain("Text size", cut.Find("[data-subtitle-appearance]").TextContent);
        await cut.Find("[data-sav-custom]").ClickAsync();
        var first = cut.FindAll(".player-settings-menu__group")[0];
        Assert.Equal("Text size", first.QuerySelector("h4")!.TextContent);
        Assert.Equal(["Small", "Medium", "Large"], first.QuerySelectorAll("button").Select(button => button.TextContent.Trim()));
        Assert.Equal("true", first.QuerySelector("[aria-label='Text size: Medium']")!.GetAttribute("aria-pressed"));
        await cut.Find("[aria-label='Text size: Large']").ClickAsync();
        Assert.Equal(PlaybackPreferenceValues.Large, _look.SubtitleSize);
    }

    [Fact]
    public async Task ChoosingBackgroundNone_KeepsTheTickedPresetTicked()
    {
        var cut = await OpenAppearanceAsync();
        await cut.Find("[data-sav-preset='large']").ClickAsync();

        await cut.Find(".sav__row .app-segmented-control__item:first-child").ClickAsync();

        Assert.False(_look.SubtitleBackground);
        Assert.Equal("large", PresetOn(cut));
        await cut.Find("[data-sav-preset='contrast']").ClickAsync();
        await cut.Find(".sav__row .app-segmented-control__item:nth-child(2)").ClickAsync();
        Assert.Equal("contrast", PresetOn(cut));
    }

    [Theory]
    [InlineData(1, false, false)]
    [InlineData(2, false, true)]
    [InlineData(3, true, false)]
    public async Task ChoosingAPreset_DoesNotChangeTheBackgroundChoice(int backgroundIndex, bool box, bool shadow)
    {
        var cut = await OpenAppearanceAsync();
        await cut.Find($".sav__row .app-segmented-control__item:nth-child({backgroundIndex})").ClickAsync();

        foreach (var preset in new[] { "large", "contrast", "default" })
        {
            await cut.Find($"[data-sav-preset='{preset}']").ClickAsync();

            Assert.Equal(box, _look.SubtitleBackground);
            Assert.Equal(shadow, _look.SubtitleShadow);
            Assert.Equal(preset, PresetOn(cut));
        }
    }

    [Fact]
    public async Task EveryTile_ShowsTheSampleAndTheNameAsSeparateBlockLinesAndNoTileIsDisabled()
    {
        var cut = await OpenAppearanceAsync();

        var tiles = cut.FindAll("[data-sav-preset]");
        Assert.Equal(3, tiles.Count);
        Assert.All(tiles, tile =>
        {
            Assert.False(tile.HasAttribute("disabled"));
            var lines = tile.Children.Where(child => child.ClassList.Contains("sav__aa") || child.ClassList.Contains("sav__tile-label")).ToList();
            Assert.Equal(2, lines.Count);
            Assert.Contains("sav__aa", lines[0].ClassList);
            Assert.Contains("sav__tile-label", lines[1].ClassList);
        });
        Assert.Equal(["Aa", "Aa", "Aa"], cut.FindAll(".sav__aa").Select(sample => sample.TextContent));
        var css = File.ReadAllText(Path.Combine(FindRepoRoot(), "src/MediaEngine.Web/Components/Player/SubtitleAppearanceView.razor.css"));
        Assert.Contains("flex-direction: column;", css, StringComparison.Ordinal);
        Assert.Contains("height: 84px;", css, StringComparison.Ordinal);
        Assert.Contains("flex: 1 1 0;", css, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ThePreviewFollowsTheLookAtOnce()
    {
        var cut = await OpenAppearanceAsync();
        Assert.Contains("font-size:16px", cut.Find(".sav__preview-line").GetAttribute("style"));

        await cut.Find("[data-sav-preset='large']").ClickAsync();

        Assert.Contains("font-size:21px", cut.Find(".sav__preview-line").GetAttribute("style"));
        await cut.Find(".sav__row .app-segmented-control__item:first-child").ClickAsync();
        Assert.Contains("background:transparent", cut.Find(".sav__preview-line").GetAttribute("style"));
    }

    [Fact]
    public async Task CustomAppearance_OpensTheFullChoicesOneLevelDeeperAndTheBackArrowReturnsOneLevel()
    {
        var cut = await OpenAppearanceAsync();
        Assert.Equal("Font, size, colors and opacity", cut.Find(".sav__custom-sub").TextContent);

        await cut.Find("[data-sav-custom]").ClickAsync();

        Assert.Equal("Custom appearance", cut.Find("header h2").TextContent);
        var titles = cut.FindAll(".player-settings-menu__group > h4").Select(title => title.TextContent).ToArray();
        Assert.Equal(["Text size", "Font", "Shadow", "Text color", "Text opacity", "Background color", "Background opacity"], titles);
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
        await cut.Find(".sav__row .app-segmented-control__item:first-child").ClickAsync();

        await cut.Find("[data-asp-action='reset']").ClickAsync();

        Assert.Equal(new SubtitleLanguageSettingsDto().SubtitleSize, _look.SubtitleSize);
        Assert.Equal(PlaybackPreferenceValues.DefaultBackgroundOpacity, _look.SubtitleBackgroundOpacity);
        Assert.True(_look.SubtitleBackground);
        Assert.False(_look.SubtitleShadow);
        Assert.Equal(SubtitleBackdrop.Box, SubtitleAppearance.BackdropChoice(_look));
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
    public void HighContrastWithABox_IsASolidBlackBox()
    {
        var look = new SubtitleLanguageSettingsDto();
        SubtitleAppearance.Apply(look, SubtitlePreset.HighContrast);
        SubtitleAppearance.ApplyBackdrop(look, SubtitleBackdrop.Box);

        Assert.Equal("rgb(0 0 0 / 100%)", SubtitleStyleCss.BackgroundColor(look));
        Assert.Equal("rgb(255 255 255 / 100%)", SubtitleStyleCss.TextColor(look));
        Assert.False(SubtitleStyleCss.UsesHighContrastOutline(look));
        Assert.Equal(SubtitlePreset.HighContrast, SubtitleAppearance.Matching(look));
    }

    [Theory]
    [InlineData(SubtitleBackdrop.None)]
    [InlineData(SubtitleBackdrop.Shadow)]
    public void HighContrastWithNoBox_HasNoBoxAndAHeavyBlackOutline(SubtitleBackdrop backdrop)
    {
        var look = new SubtitleLanguageSettingsDto();
        SubtitleAppearance.Apply(look, SubtitlePreset.HighContrast);
        SubtitleAppearance.ApplyBackdrop(look, backdrop);

        Assert.Equal("transparent", SubtitleStyleCss.BackgroundColor(look));
        Assert.True(SubtitleStyleCss.IsHighContrast(look));
        Assert.True(SubtitleStyleCss.UsesHighContrastOutline(look));
        Assert.Equal(SubtitlePreset.HighContrast, SubtitleAppearance.Matching(look));
        Assert.Equal(backdrop, SubtitleAppearance.BackdropChoice(look));
    }

    [Theory]
    [InlineData(SubtitlePreset.Default)]
    [InlineData(SubtitlePreset.LargeText)]
    public void DefaultAndLargeText_WithABox_GiveTheNormal60PercentBlackBoxAndNoOutline(SubtitlePreset preset)
    {
        var look = new SubtitleLanguageSettingsDto { SubtitleBackgroundOpacity = 100 };
        SubtitleAppearance.Apply(look, preset);

        Assert.Equal("rgb(0 0 0 / 60%)", SubtitleStyleCss.BackgroundColor(look));
        Assert.False(SubtitleStyleCss.IsHighContrast(look));
        SubtitleAppearance.ApplyBackdrop(look, SubtitleBackdrop.None);
        Assert.False(SubtitleStyleCss.UsesHighContrastOutline(look));
        Assert.Equal(preset, SubtitleAppearance.Matching(look));
    }

    [Fact]
    public void TheOutlineTheCuesDrawIsTheSameValueThePreviewUses()
    {
        var css = File.ReadAllText(Path.Combine(FindRepoRoot(), "src/MediaEngine.Web/wwwroot/app.css"));

        Assert.Contains($"--tl-subtitle-shadow: {SubtitleStyleCss.HighContrastOutline};", css, StringComparison.Ordinal);
        Assert.Contains("[data-subtitle-contrast=\"true\"]", css, StringComparison.Ordinal);
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
        await cut.Find("[data-sav-custom]").ClickAsync();
        await cut.Find("[aria-label='Text size: Small']").ClickAsync();
        await cut.Find("button[aria-label='Back']").ClickAsync();
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
