using MediaEngine.Contracts.Playback;
using MediaEngine.Web.Services.Playback;

namespace MediaEngine.Web.Tests;

public sealed class PageReaderProfileSettingsTests
{
    private static readonly Guid Asset = Guid.Parse("11111111-2222-3333-4444-555555555555");

    [Fact]
    public void ChoicesFor_WithoutProfile_GivesTheReadersOwnDefaults()
    {
        var choices = PageReaderProfileSettings.ChoicesFor(null, Asset, "Saga");

        Assert.Equal(("auto", "page", "auto", "black", false), (choices.Layout, choices.Fit, choices.Direction, choices.Background, choices.ShowBookmarksOnBar));
    }

    [Fact]
    public void DirectionChosenOnOneIssue_AppliesToTheNextIssueOfTheSeries()
    {
        var reading = new ReadingSettingsDto();
        PageReaderProfileSettings.SetDirection(reading, "Saga", "rtl");

        var otherIssue = PageReaderProfileSettings.ChoicesFor(reading, Guid.NewGuid(), "  SAGA ");
        var otherSeries = PageReaderProfileSettings.ChoicesFor(reading, Guid.NewGuid(), "Other");

        Assert.Equal("rtl", otherIssue.Direction);
        Assert.Equal("auto", otherSeries.Direction);
    }

    [Fact]
    public void LayoutIsRememberedPerTitle_AndAutoClearsIt()
    {
        var reading = new ReadingSettingsDto();
        PageReaderProfileSettings.SetLayout(reading, Asset, "scroll");

        Assert.Equal("scroll", PageReaderProfileSettings.ChoicesFor(reading, Asset, null).Layout);
        Assert.Equal("auto", PageReaderProfileSettings.ChoicesFor(reading, Guid.NewGuid(), null).Layout);

        PageReaderProfileSettings.SetLayout(reading, Asset, "auto");
        Assert.Empty(reading.PageLayouts);
    }

    [Fact]
    public void SetDirection_WithoutASeries_RemembersNothing()
    {
        var reading = new ReadingSettingsDto();
        PageReaderProfileSettings.SetDirection(reading, null, "rtl");

        Assert.Empty(reading.ComicDirections);
    }
}
