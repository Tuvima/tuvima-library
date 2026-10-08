using MediaEngine.Web.Components.Browse;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Models.ViewDTOs;

namespace MediaEngine.Web.Tests;

public sealed class BrowseQueryBuilderTests
{
    [Fact]
    public void TimelinePeriod_ParsesOnlySupportedGroupingWithoutChangingFilters()
    {
        var state = BrowseQueryBuilder.Read(CreatePreset(), "books", "http://localhost/read/books?period=decade&genre=History&q=travel");
        Assert.Equal(MediaEngine.Web.Components.Shared.TimelineGrouping.Decade, state.Period);
        Assert.Equal("travel", state.SearchText);
        Assert.Contains("History", state.Genres);
        Assert.Equal(MediaEngine.Web.Components.Shared.TimelineGrouping.Year,
            BrowseQueryBuilder.Read(CreatePreset(), "books", "http://localhost/read/books?period=month").Period);
        Assert.Equal("newest", BrowseQueryBuilder.ResolveSort(null, "books", "timeline"));
        Assert.DoesNotContain(BrowseQueryBuilder.GetSortOptions("books", "timeline"), option => option.Value == "title");
    }
    [Fact]
    public void Read_NormalizesTabGroupingLayoutSortAndIgnoresLegacyGroupState()
    {
        var preset = CreatePreset();
        var state = BrowseQueryBuilder.Read(
            preset,
            "music",
            "http://localhost/listen/music?browse=albums&view=list&sort=title&q=dune");

        Assert.Equal("music", state.ActiveTabId);
        Assert.Equal("albums", state.Grouping);
        Assert.Equal(LibraryLayoutMode.List, state.Layout);
        Assert.Equal("title", state.SortBy);
        Assert.Equal("dune", state.SearchText);
        Assert.DoesNotContain("GroupId", state.GetType().GetProperties().Select(property => property.Name));
    }

    [Fact]
    public void Read_InvalidQueryFallsBackToTabDefaults()
    {
        var preset = CreatePreset();
        var state = BrowseQueryBuilder.Read(
            preset,
            "missing",
            "http://localhost/listen?browse=bad&view=bad&sort=bad");

        Assert.Equal("books", state.ActiveTabId);
        Assert.Equal("all", state.Grouping);
        Assert.Equal(LibraryLayoutMode.Card, state.Layout);
        Assert.Equal("newest", state.SortBy);
    }

    [Theory]
    [InlineData("books", "series", "series")]
    [InlineData("audiobooks", "series", "series")]
    [InlineData("comics", "series", null)]
    [InlineData("books", "authors", "author")]
    [InlineData("movies", "directors", "director")]
    [InlineData("tv", "networks", "network")]
    public void GetSystemViewGroupField_UsesProviderBackedContentGroupsForComicRuns(
        string activeTabId,
        string grouping,
        string? expected)
    {
        var groupField = BrowseQueryBuilder.GetSystemViewGroupField(activeTabId, grouping);

        Assert.Equal(expected, groupField);
    }

    private static LibraryBrowsePreset CreatePreset() => new()
    {
        RouteBase = "/listen",
        Title = "Listen",
        HeroVariant = BrowseHeroVariant.Listen,
        Tabs =
        [
            new()
            {
                Id = "books",
                Label = "Books",
                MediaType = "Books",
                DefaultGrouping = "all",
                DefaultLayout = LibraryLayoutMode.Card,
                GroupingOptions = [new("all", "All", AppMaterialIcons.Outlined.LibraryBooks)],
            },
            new()
            {
                Id = "music",
                Label = "Music",
                MediaType = "Music",
                DefaultGrouping = "artists",
                DefaultLayout = LibraryLayoutMode.Card,
                GroupingOptions =
                [
                    new("artists", "Artists", AppMaterialIcons.Outlined.Person),
                    new("albums", "Albums", AppMaterialIcons.Outlined.Album),
                ],
            },
        ],
    };
}
