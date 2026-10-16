using MediaEngine.Web.Services.Navigation;

namespace MediaEngine.Web.Tests;

/// <summary>
/// The dock catalog: the fixed three-tab phone dock, the five-tab TV dock (defaults, Kids, the Downloads switch), old
/// saved settings and the last library.
/// </summary>
public sealed class ShellDockTests
{
    private static readonly string[] FiveTabs = ["Home", "Libraries", "Search", "Downloads", "Settings"];
    private static readonly string[] LegacySixTabs = ["Home", "Read", "Watch", "Listen", "View", "Collections"];

    private static string[] Keys(IEnumerable<ShellDockEntry> entries) => entries.Select(entry => entry.Key).ToArray();

    [Fact]
    public void DefaultItemsResolveToTheFiveTabs()
    {
        var entries = ShellDock.Resolve(FiveTabs, isKids: false, DownloadsTabMode.ExplainNativeApp);
        Assert.Equal(FiveTabs, Keys(entries));
        Assert.Equal([ShellDockKind.Link, ShellDockKind.Libraries, ShellDockKind.Search, ShellDockKind.Link, ShellDockKind.Link],
            entries.Select(entry => entry.Kind).ToArray());
        Assert.Equal("/downloads", entries[3].Path);
        Assert.Equal("/settings", entries[4].Path);
    }

    [Fact]
    public void TheShippedDefaultKeepsTheDownloadsTab()
    {
        Assert.Equal(DownloadsTabMode.ExplainNativeApp, ShellDock.DownloadsMode);
        Assert.Equal(FiveTabs, Keys(ShellDock.Resolve(FiveTabs, isKids: false)));
    }

    [Fact]
    public void HiddenDownloadsDropsTheTabToFour()
    {
        var entries = ShellDock.Resolve(FiveTabs, isKids: false, DownloadsTabMode.Hidden);
        Assert.Equal(["Home", "Libraries", "Search", "Settings"], Keys(entries));
    }

    [Fact]
    public void KidsGetFourTabsWithoutSettings()
    {
        Assert.Equal(["Home", "Libraries", "Search", "Downloads"], Keys(ShellDock.Resolve(FiveTabs, isKids: true, DownloadsTabMode.ExplainNativeApp)));
        Assert.Equal(["Home", "Libraries", "Search"], Keys(ShellDock.Resolve(FiveTabs, isKids: true, DownloadsTabMode.Hidden)));
    }

    [Fact]
    public void SettingsSavedBeforeTheFiveTabDockStillResolve()
    {
        Assert.Equal(LegacySixTabs, Keys(ShellDock.Resolve(LegacySixTabs, isKids: false)));
        Assert.Equal(["Home", "Read", "Watch", "Listen"], Keys(ShellDock.Resolve(LegacySixTabs, isKids: true)));
        Assert.Equal(["Home", "Watch"], Keys(ShellDock.Resolve(["Home", "Unknown", "Watch", "watch"], isKids: false)));
    }

    [Fact]
    public void ThePhoneDockIsAlwaysHomeSearchAndMyLibraryForEveryProfile()
    {
        foreach (var isKids in new[] { false, true })
        {
            var entries = ShellDock.ResolvePhone(isKids);
            Assert.Equal(["Home", "Search", "MyLibrary"], Keys(entries));
            Assert.Equal(["/", "/search", "/for-me?view=my-list"], entries.Select(entry => entry.Path).ToArray());
            Assert.Equal(["Nav_Home", "Nav_Search", "Nav_MyLibrary"], entries.Select(entry => entry.LabelKey).ToArray());
            Assert.Equal([ShellDockKind.Link, ShellDockKind.Search, ShellDockKind.Link], entries.Select(entry => entry.Kind).ToArray());
        }
    }

    [Theory]
    [InlineData(DownloadsTabMode.ExplainNativeApp)]
    [InlineData(DownloadsTabMode.Hidden)]
    public void ThePhoneDockHasNoDownloadsOrSettingsTabWhateverTheDownloadsSwitchSays(DownloadsTabMode mode)
    {
        Assert.Equal(["Home", "Search", "MyLibrary"], Keys(ShellDock.Resolve(ShellDock.PhoneDockItems, isKids: false, mode)));
    }

    [Fact]
    public void TheDownloadsIconInThePhoneTopBarShowsOnlyWhereDownloadsWork()
    {
        Assert.False(ShellDock.ShowsDownloadsInTopBar(ShellDock.DownloadsMode));
        Assert.False(ShellDock.ShowsDownloadsInTopBar(DownloadsTabMode.ExplainNativeApp));
        Assert.False(ShellDock.ShowsDownloadsInTopBar(DownloadsTabMode.Hidden));
        Assert.True(ShellDock.ShowsDownloadsInTopBar(DownloadsTabMode.Available));
    }

    [Theory]
    [InlineData("/for-me?view=my-list", "/for-me")]
    [InlineData("/search", "/search")]
    [InlineData("/watch#top", "/watch")]
    public void PathOnlyDropsTheQueryAndFragment(string path, string expected) =>
        Assert.Equal(expected, ShellDock.PathOnly(path));

    [Fact]
    public void LibrariesAndWatchAreDifferentTabsEvenThoughLibrariesStartsAtWatch()
    {
        Assert.Equal(["Home", "Watch", "Libraries", "Search"], Keys(ShellDock.Resolve(["Home", "Watch", "Libraries", "Search"], isKids: false)));
        Assert.Equal(["Home", "Libraries"], Keys(ShellDock.Resolve(["Home", "Libraries", "Libraries"], isKids: false)));
    }

    [Fact]
    public void LibrarySurfacesIncludeLanesSubPagesAndRecentlyAdded()
    {
        foreach (var path in new[] { "/watch", "/watch/movies", "/read", "/listen/music", "/view", "/collections", "/recently-added" })
        {
            Assert.True(ShellDock.IsLibrarySurface(path), path);
        }

        foreach (var path in new[] { "/", "/search", "/settings", "/downloads", "/details/work/1", "/watchlist" })
        {
            Assert.False(ShellDock.IsLibrarySurface(path), path);
        }
    }

    [Fact]
    public void OnlyLanesAreRemembered()
    {
        Assert.Equal("/listen", ShellDock.LaneForPath("/listen/music"));
        Assert.Equal("/collections", ShellDock.LaneForPath("/Collections"));
        Assert.Null(ShellDock.LaneForPath("/recently-added"));
        Assert.Null(ShellDock.LaneForPath("/"));
    }

    [Theory]
    [InlineData(null, false, "/watch")]
    [InlineData("/read", false, "/read")]
    [InlineData("/VIEW", false, "/view")]
    [InlineData("/view", true, "/watch")]
    [InlineData("/collections", true, "/watch")]
    [InlineData("/listen", true, "/listen")]
    [InlineData("/settings", false, "/watch")]
    [InlineData("garbage", false, "/watch")]
    public void StoredLastLibraryFallsBackToWatch(string? stored, bool isKids, string expected) =>
        Assert.Equal(expected, ShellDock.NormalizeLastLibrary(stored, isKids));

    [Fact]
    public void SwitcherListsSixRowsForAdultsAndThreeLanesForKids()
    {
        Assert.Equal(["Watch", "Read", "Listen", "View", "Collections", "RecentlyAdded"], ShellDock.SwitcherRows(isKids: false).Select(row => row.Key).ToArray());
        Assert.Equal(["Watch", "Listen", "Read"], ShellDock.SwitcherRows(isKids: true).Select(row => row.Key).ToArray());
    }

    [Fact]
    public void LastLibraryIsStoredPerProfile()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        Assert.NotEqual(ShellDock.LastLibraryStorageKey(first), ShellDock.LastLibraryStorageKey(second));
        Assert.StartsWith(ShellDock.LastLibraryStorageKeyPrefix, ShellDock.LastLibraryStorageKey(null), StringComparison.Ordinal);
    }
}
