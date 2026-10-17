using MediaEngine.Web.Services.Navigation;

namespace MediaEngine.Web.Tests;

/// <summary>
/// The one navigation catalogue: the desktop top bar, the phone menu and the phone chips are all generated from it.
/// </summary>
public sealed class ShellNavigationTests
{
    private static string[] Paths(IEnumerable<ShellNavLink> links) => links.Select(link => link.Path).ToArray();

    [Fact]
    public void TheTopBarKeepsItsOrderForAdults()
    {
        Assert.Equal(["/read", "/watch", "/listen", "/view", "/collections"], Paths(ShellNavigation.TopBarLinks(isKids: false)));
    }

    [Fact]
    public void KidsKeepOnlyWatchListenAndRead()
    {
        Assert.Equal(["/watch", "/listen", "/read"], Paths(ShellNavigation.TopBarLinks(isKids: true)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheMenuIsHomeMyLibraryAndRecentlyAddedThenExactlyTheTopBarLinks(bool isKids)
    {
        var menu = ShellNavigation.MenuLinks(isKids);
        var topBar = ShellNavigation.TopBarLinks(isKids);
        var lead = isKids ? 2 : 3;

        Assert.Equal(isKids ? ["Home", "MyLibrary"] : ["Home", "MyLibrary", "RecentlyAdded"], menu.Take(lead).Select(link => link.Key).ToArray());
        // The menu's My Library is the dock's My Library: one destination.
        Assert.Equal(ShellDock.MyLibraryPath, menu[1].Path);
        Assert.Equal(topBar, menu.Skip(lead).ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheChipsAreExactlyTheTopBarLinks(bool isKids)
    {
        Assert.Equal(ShellNavigation.TopBarLinks(isKids), ShellNavigation.SectionChips(isKids));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EveryDestinationHasALabelAndAnIconFromTheSharedLaneSource(bool isKids)
    {
        foreach (var link in ShellNavigation.MenuLinks(isKids))
        {
            Assert.False(string.IsNullOrWhiteSpace(link.LabelKey), link.Key);
            Assert.False(string.IsNullOrWhiteSpace(link.Icon), link.Key);
        }

        foreach (var lane in ShellNavigation.TopBarLinks(isKids))
        {
            var surface = ShellDock.LibrarySurfaces.Single(candidate => candidate.Path == lane.Path);
            Assert.Equal(surface.LabelKey, lane.LabelKey);
            Assert.Equal(surface.Icon, lane.Icon);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ViewIsOneEntryWithNoSeparatePhotosOrDocuments(bool isKids)
    {
        foreach (var link in ShellNavigation.MenuLinks(isKids).Concat(ShellNavigation.SectionChips(isKids)))
        {
            Assert.DoesNotContain("photo", link.Key, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("document", link.Key, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("photo", link.Path, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("document", link.Path, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("/view/", link.Path, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Equal(isKids ? 0 : 1, ShellNavigation.MenuLinks(isKids).Count(link => link.Path == "/view"));
    }

    [Theory]
    [InlineData("/", "/", true)]
    [InlineData("/watch", "/", false)]
    [InlineData("/watch", "/watch", true)]
    [InlineData("/watch", "/Watch/movies", true)]
    [InlineData("/watch", "/watchlist", false)]
    [InlineData("/view", "/view/documents", true)]
    public void ACurrentSectionIncludesItsSubPages(string linkPath, string currentPath, bool expected) =>
        Assert.Equal(expected, ShellNavigation.IsCurrent(linkPath, currentPath));

    [Fact]
    public void SettingsAndSwitchProfileFollowTheAccountMenuRules()
    {
        Assert.True(ShellNavigation.ShowsSettings(isKids: false));
        Assert.False(ShellNavigation.ShowsSettings(isKids: true));
        Assert.False(ShellNavigation.ShowsSwitchProfile(0));
        Assert.False(ShellNavigation.ShowsSwitchProfile(1));
        Assert.True(ShellNavigation.ShowsSwitchProfile(2));
    }
}
