using MediaEngine.Web.Services.Navigation;

namespace MediaEngine.Web.Tests;

/// <summary>The history-correct rule for which page sits under the details overlay (spec D1).</summary>
public sealed class DetailsOverlayRouteTests
{
    private const string Title = "/details/book/6b0b8b0a-8c8e-4a5e-9c79-0c2d6d4b4f11";
    private const string OtherTitle = "/details/movie/0a1b2c3d-1111-2222-3333-444455556666";

    [Theory]
    [InlineData("/details/book/6b0b8b0a-8c8e-4a5e-9c79-0c2d6d4b4f11", true)]
    [InlineData("/Details/Movie/0a1b2c3d-1111-2222-3333-444455556666?tab=cast", true)]
    [InlineData("/listen", false)]
    [InlineData("/", false)]
    [InlineData("/detailsmore/x", false)]
    [InlineData(null, false)]
    public void OnlyAddressesUnderDetailsAreDetailAddresses(string? uri, bool expected) =>
        Assert.Equal(expected, DetailsOverlayRoute.IsDetailUri(uri));

    [Fact]
    public void OpeningATitleFromAPageKeepsThatPageUnderIt()
    {
        var route = DetailsOverlayRoute.Resolve(DetailsOverlayRoute.None, "/listen?x=1", Title, null, firstRender: false);

        Assert.Equal("/listen?x=1", route.UnderlayUri);
        Assert.Equal(Title, route.OverlayUri);
        Assert.Equal(1, route.Depth);
    }

    [Fact]
    public void ReloadingATitleOpensItOverHomeWhateverStateTheEntryHolds()
    {
        var staleState = DetailsOverlayRoute.EncodeState("/listen", 1);

        var route = DetailsOverlayRoute.Resolve(DetailsOverlayRoute.None, previousUri: null, Title, staleState, firstRender: true);

        Assert.Equal(DetailsOverlayRoute.Home, route.UnderlayUri);
        Assert.Equal(Title, route.OverlayUri);
        Assert.Equal(0, route.Depth);
    }

    [Fact]
    public void ATitleOpenedStraightAfterTheFirstLoadAlsoGoesOverHomeWhenThePreviousPageIsUnknown()
    {
        var route = DetailsOverlayRoute.Resolve(DetailsOverlayRoute.None, previousUri: null, Title, null, firstRender: false);

        Assert.Equal(DetailsOverlayRoute.Home, route.UnderlayUri);
    }

    [Fact]
    public void OpeningAnotherTitleFromInsideTheOverlayKeepsTheSameUnderlayAndAddsAStep()
    {
        var open = DetailsOverlayRoute.Resolve(DetailsOverlayRoute.None, "/listen", Title, null, firstRender: false);

        var next = DetailsOverlayRoute.Resolve(open, Title, OtherTitle, null, firstRender: false);

        Assert.Equal("/listen", next.UnderlayUri);
        Assert.Equal(OtherTitle, next.OverlayUri);
        Assert.Equal(2, next.Depth);
    }

    [Fact]
    public void TitleToTitleStepsAreNotCountedWhenTheStepsBackToTheUnderlayAreUnknown()
    {
        var reloaded = DetailsOverlayRoute.Resolve(DetailsOverlayRoute.None, null, Title, null, firstRender: true);

        var next = DetailsOverlayRoute.Resolve(reloaded, Title, OtherTitle, null, firstRender: false);

        Assert.Equal(0, next.Depth);
    }

    [Fact]
    public void LeavingTheOverlayForAnotherPageClosesIt()
    {
        var open = DetailsOverlayRoute.Resolve(DetailsOverlayRoute.None, "/listen", Title, null, firstRender: false);

        var closed = DetailsOverlayRoute.Resolve(open, Title, "/watch", null, firstRender: false);

        Assert.False(closed.IsOpen);
        Assert.Null(closed.UnderlayUri);
    }

    [Fact]
    public void BackFromThePlayerShowsTheTitleOverListenNotOverThePlayer()
    {
        // Listen, then a title (tagged with Listen), then the player.
        var overListen = DetailsOverlayRoute.Resolve(DetailsOverlayRoute.None, "/listen/music", Title, null, firstRender: false);
        var tag = DetailsOverlayRoute.StateOf(overListen);
        var onPlayer = DetailsOverlayRoute.Resolve(overListen, Title, "/watch/player/abc", null, firstRender: false);
        Assert.False(onPlayer.IsOpen);

        // Browser Back lands on the title's entry, which carries its own underlay.
        var back = DetailsOverlayRoute.Resolve(onPlayer, "/watch/player/abc", Title, tag, firstRender: false);

        Assert.Equal("/listen/music", back.UnderlayUri);
        Assert.Equal(Title, back.OverlayUri);
        Assert.Equal(1, back.Depth);
    }

    [Fact]
    public void BackOntoAnEarlierTitleRestoresThatEntrysOwnDepth()
    {
        var first = DetailsOverlayRoute.Resolve(DetailsOverlayRoute.None, "/listen", Title, null, firstRender: false);
        var firstTag = DetailsOverlayRoute.StateOf(first);
        var second = DetailsOverlayRoute.Resolve(first, Title, OtherTitle, null, firstRender: false);

        var back = DetailsOverlayRoute.Resolve(second, OtherTitle, Title, firstTag, firstRender: false);

        Assert.Equal("/listen", back.UnderlayUri);
        Assert.Equal(1, back.Depth);
    }

    [Fact]
    public void AStoredUnderlayThatCannotBeShownFallsBackToHome()
    {
        var tag = DetailsOverlayRoute.EncodeState("/never-seen", 2);

        var route = DetailsOverlayRoute.Resolve(DetailsOverlayRoute.None, "/watch", Title, tag, firstRender: false, canRestoreUnderlay: uri => uri != "/never-seen");

        Assert.Equal(DetailsOverlayRoute.Home, route.UnderlayUri);
        Assert.Equal(0, route.Depth);
    }

    [Fact]
    public void APageThatCannotBeShownUnderAnOverlayIsReplacedByHome()
    {
        var route = DetailsOverlayRoute.Resolve(DetailsOverlayRoute.None, "/read/abc", Title, null, firstRender: false, canRestoreUnderlay: _ => false);

        Assert.Equal(DetailsOverlayRoute.Home, route.UnderlayUri);
        Assert.Equal(0, route.Depth);
    }

    [Theory]
    [InlineData("https://elsewhere.example/x")]
    [InlineData("//elsewhere.example/x")]
    [InlineData("listen")]
    [InlineData("/details/book/6b0b8b0a-8c8e-4a5e-9c79-0c2d6d4b4f11")]
    public void StoredStateNamingAnythingButAnAppPageIsIgnored(string underlay)
    {
        var tag = DetailsOverlayRoute.EncodeState(underlay, 1);

        Assert.False(DetailsOverlayRoute.TryDecodeState(tag, out _, out _));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("tl-details-overlay:v1;x;/listen")]
    [InlineData("something else")]
    public void UnreadableStateIsIgnored(string? state) =>
        Assert.False(DetailsOverlayRoute.TryDecodeState(state, out _, out _));

    [Fact]
    public void StateRoundTripsTheUnderlayAndDepthEvenWhenTheAddressHasSemicolons()
    {
        var tag = DetailsOverlayRoute.EncodeState("/search?q=a;b", 3);

        Assert.True(DetailsOverlayRoute.TryDecodeState(tag, out var underlay, out var depth));
        Assert.Equal("/search?q=a;b", underlay);
        Assert.Equal(3, depth);
    }

    [Fact]
    public void AnAddressThatIsNotADetailAddressGetsNoState() =>
        Assert.Null(DetailsOverlayRoute.StateForNewEntry(DetailsOverlayRoute.None, "/listen", "/watch"));
}
