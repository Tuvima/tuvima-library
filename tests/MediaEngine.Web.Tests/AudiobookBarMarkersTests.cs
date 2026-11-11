using MediaEngine.Contracts.Playback;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Playback;

namespace MediaEngine.Web.Tests;

// Unified player D10: the bookmark markers the audiobook bar draws (only when the profile turned them on; in the chapter view only that chapter's).
public sealed class AudiobookBarMarkersTests
{

    [Fact]
    public void BarMarkers_AreHiddenUnlessTheProfileTurnedThemOn()
    {
        var saved = new[] { Bookmark(100, inEveryone: true), Bookmark(500, isGlobal: true, inEveryone: true) };

        Assert.Empty(AudiobookBarMarkers.ForBar(saved, show: false, choice: null, duration: 1000, scope: null));
        var shown = AudiobookBarMarkers.ForBar(saved, show: true, choice: null, duration: 1000, scope: null);
        Assert.Equal([100d, 500d], shown.Select(marker => marker.Seconds));
        Assert.True(shown[1].IsGlobal);
    }

    [Fact]
    public void BarMarkers_FollowTheListThePanelShows_EveryoneByDefaultOrMine()
    {
        var saved = new[]
        {
            Bookmark(100, inEveryone: true),
            Bookmark(200, isYours: true),
            Bookmark(300, isYours: true, inEveryone: true),
        };

        Assert.Equal([100d, 300d], AudiobookBarMarkers.ForBar(saved, true, PlaybackPreferenceValues.All, 1000, null).Select(marker => marker.Seconds));
        Assert.Equal([100d, 300d], AudiobookBarMarkers.ForBar(saved, true, null, 1000, null).Select(marker => marker.Seconds));
        Assert.Equal([200d, 300d], AudiobookBarMarkers.ForBar(saved, true, PlaybackPreferenceValues.Mine, 1000, null).Select(marker => marker.Seconds));
    }

    [Fact]
    public void BarMarkers_InTheChapterViewKeepOnlyThatChaptersMarkersAndMapThemToTimeIntoTheChapter()
    {
        var saved = new[] { Bookmark(100, inEveryone: true), Bookmark(450, inEveryone: true), Bookmark(900, inEveryone: true) };
        var chapter = new AudiobookChapterWindow(2, 3, "Two", Start: 400, End: 800);

        var shown = AudiobookBarMarkers.ForBar(saved, show: true, choice: null, duration: 1000, scope: chapter);

        var marker = Assert.Single(shown);
        Assert.Equal(50d, marker.Seconds);
        Assert.Equal(450d, AudiobookBarMarkers.ToAbsolute(marker, chapter));
    }

    private static AudiobookBookmarkDto Bookmark(double seconds, bool isGlobal = false, bool isYours = false, bool inEveryone = false) => new()
    {
        Id = Guid.NewGuid(),
        PositionSeconds = seconds,
        IsGlobal = isGlobal,
        IsYours = isYours,
        InEveryone = inEveryone,
    };
}
