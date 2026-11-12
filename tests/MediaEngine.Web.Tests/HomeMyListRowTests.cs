namespace MediaEngine.Web.Tests;

// Home's My List row (My List spec D9) sits directly under Continue and is not repeated among the lanes below.
public sealed class HomeMyListRowTests
{
    [Fact]
    public void HomePage_DrawsTheMyListRowAfterContinueAndBeforeRecentlyAddedAsAStandardShelf()
    {
        var home = Read(@"src/MediaEngine.Web/Components/Pages/LibraryBrowsePage.razor");

        var continueRow = home.IndexOf("UseContinueWatchingOrListeningTiles=\"true\"", StringComparison.Ordinal);
        var myListRow = home.IndexOf("IsMyListShelf(s)))", StringComparison.Ordinal);
        var recentRow = home.IndexOf("<RecentMediaFeed", StringComparison.Ordinal);

        Assert.True(continueRow >= 0 && myListRow > continueRow && recentRow > myListRow);
        Assert.Contains("<MediaTileShelf Shelf=\"@shelf\" @key=\"shelf.Key\" />", home, StringComparison.Ordinal);
    }

    [Fact]
    public void HomePage_LeavesTheMyListShelfOutOfTheLaneRowsBelowSoItIsNeverDrawnTwice()
    {
        var home = Read(@"src/MediaEngine.Web/Components/Pages/LibraryBrowsePage.razor");

        Assert.Contains("s.Kind != MediaTileShelfKind.Continue && !IsMyListShelf(s)", home, StringComparison.Ordinal);
        Assert.Contains("string.Equals(shelf.Key, \"my-list\", StringComparison.OrdinalIgnoreCase)", home, StringComparison.Ordinal);
    }

    private static string Read(string relativePath) =>
        LocalizedSource.ReadAllText(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", relativePath)));
}
