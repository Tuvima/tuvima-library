using MediaEngine.Web.Models.ViewDTOs;

namespace MediaEngine.Web.Tests;

/// <summary>Home's "Collections and series" row (Group pages and tiles spec WP0b) is its own kind of row, placed right after Recently added.</summary>
public sealed class HomeCollectionsAndSeriesShelfTests
{
    [Fact]
    public void TheCollectionsAndSeriesRow_IsClassifiedAsACollectionsRow_NotAPickUpOrStandardRow()
    {
        Assert.Equal(MediaTileShelfKind.Collections, MediaTileShelfKeys.Classify(MediaTileShelfKeys.CollectionsAndSeries));
        Assert.Equal("collections-and-series", MediaTileShelfKeys.CollectionsAndSeries);
    }

    [Fact]
    public void HomeDrawsTheCollectionsAndSeriesRow_RightAfterRecentlyAdded_AndNotAgainAmongTheOtherRows()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "MediaEngine.Web", "Components", "Pages"));
        var page = File.ReadAllText(Path.Combine(root, "LibraryBrowsePage.razor"));

        var recent = page.IndexOf("<RecentMediaFeed", StringComparison.Ordinal);
        var groups = page.IndexOf("IsCollectionsAndSeriesShelf(s)))", StringComparison.Ordinal);
        var others = page.IndexOf("!IsMyListShelf(s) && !IsCollectionsAndSeriesShelf(s)", StringComparison.Ordinal);

        Assert.True(recent >= 0 && groups > recent && others > groups);
    }
}
