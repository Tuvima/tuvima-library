using MediaEngine.Contracts.Playback;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Playback;

namespace MediaEngine.Web.Tests;

public sealed class ReaderProfileSettingsTests
{
    [Fact]
    public void ToOpenOptions_MapsEveryReaderFieldAndTheReadingModeToTheFlow()
    {
        var reading = new ReadingSettingsDto
        {
            Theme = "dark",
            Font = "sans",
            FontSizePx = 26,
            LineHeight = 2.1m,
            PageWidth = "narrow",
            ReadingMode = PlaybackPreferenceValues.Scroll,
        };

        var options = ReaderProfileSettings.ToOpenOptions(reading);

        Assert.Equal("dark", options.Theme);
        Assert.Equal("sans", options.Font);
        Assert.Equal(26, options.FontSize);
        Assert.Equal(2.1, options.LineHeight, 3);
        Assert.Equal("narrow", options.PageWidth);
        Assert.Equal("scrolled", options.Flow);
    }

    [Fact]
    public void ToOpenOptions_OfTheDefaultsMatchesTheContractDefaults()
    {
        var options = ReaderProfileSettings.ToOpenOptions(new ReadingSettingsDto());

        Assert.Equal("system", options.Theme);
        Assert.Equal("book", options.Font);
        Assert.Equal(18, options.FontSize);
        Assert.Equal(1.5, options.LineHeight, 3);
        Assert.Equal("standard", options.PageWidth);
        Assert.Equal("paginated", options.Flow);
    }

    [Fact]
    public void ToOpenOptions_WithoutProfileSettingsGivesTheReadersOwnDefaults()
    {
        Assert.Equal(new BookReaderOpenOptions(), ReaderProfileSettings.ToOpenOptions(null));
    }

    [Fact]
    public void ToOpenOptions_PullsOutOfRangeValuesIntoRange()
    {
        var options = ReaderProfileSettings.ToOpenOptions(new ReadingSettingsDto { FontSizePx = 99, LineHeight = 0.2m });

        Assert.Equal(32, options.FontSize);
        Assert.Equal(1.2, options.LineHeight, 3);
    }

    [Fact]
    public void ApplyTo_ChangesOnlyTheReaderFields()
    {
        var reading = new ReadingSettingsDto { KeepScreenAwake = false, Margins = PlaybackPreferenceValues.Wide, FontSizePercent = 120 };

        ReaderProfileSettings.ApplyTo(reading, new BookReaderOpenOptions
        {
            Theme = "sepia",
            Font = "serif",
            FontSize = 21,
            LineHeight = 1.7,
            PageWidth = "wide",
            Flow = "scrolled",
        });

        Assert.Equal("sepia", reading.Theme);
        Assert.Equal("serif", reading.Font);
        Assert.Equal(21, reading.FontSizePx);
        Assert.Equal(1.7m, reading.LineHeight);
        Assert.Equal("wide", reading.PageWidth);
        Assert.Equal(PlaybackPreferenceValues.Scroll, reading.ReadingMode);
        Assert.False(reading.KeepScreenAwake);
        Assert.Equal(PlaybackPreferenceValues.Wide, reading.Margins);
        Assert.Equal(120, reading.FontSizePercent);
    }

    [Fact]
    public void ChangedKeys_NamesOnlyTheSettingsThatDiffer()
    {
        var before = new BookReaderOpenOptions { Theme = "dark", FontSize = 18, LineHeight = 1.5, Font = "book", PageWidth = "standard", Flow = "paginated" };

        Assert.Empty(ReaderProfileSettings.ChangedKeys(before, before with { }));
        Assert.Equal(["fontSize"], ReaderProfileSettings.ChangedKeys(before, before with { FontSize = 20 }));
        Assert.Equal(["lineHeight"], ReaderProfileSettings.ChangedKeys(before, before with { LineHeight = 1.6 }));
        Assert.Equal(["theme", "flow"], ReaderProfileSettings.ChangedKeys(before, before with { Theme = "sepia", Flow = "scrolled" }));
    }

    [Fact]
    public void SelectKeys_KeepsOnlyTheNamedSettingsUnderTheReadersNames()
    {
        var options = new BookReaderOpenOptions { Theme = "sepia", FontSize = 21, LineHeight = 1.7, Font = "sans", PageWidth = "wide", Flow = "scrolled" };

        var selected = ReaderProfileSettings.SelectKeys(options, ["fontSize", "flow"]);

        Assert.Equal(["fontSize", "flow"], selected.Keys);
        Assert.Equal(21, selected["fontSize"]);
        Assert.Equal("scrolled", selected["flow"]);
    }

    [Fact]
    public void ApplyTo_WithKeys_ChangesOnlyThoseSettings()
    {
        var reading = new ReadingSettingsDto { Theme = "light", Font = "serif", FontSizePx = 20, LineHeight = 1.4m, PageWidth = "narrow" };

        ReaderProfileSettings.ApplyTo(
            reading,
            new BookReaderOpenOptions { Theme = "dark", Font = "sans", FontSize = 30, LineHeight = 2.2, PageWidth = "wide", Flow = "scrolled" },
            ["fontSize", "theme"]);

        Assert.Equal(30, reading.FontSizePx);
        Assert.Equal("dark", reading.Theme);
        Assert.Equal("serif", reading.Font);
        Assert.Equal(1.4m, reading.LineHeight);
        Assert.Equal("narrow", reading.PageWidth);
        Assert.Equal(PlaybackPreferenceValues.Paginated, reading.ReadingMode);
    }
}
