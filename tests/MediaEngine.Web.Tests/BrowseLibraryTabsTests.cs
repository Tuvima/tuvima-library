using MediaEngine.Contracts.Display;
using MediaEngine.Web.Components.Browse;
using MediaEngine.Web.Components.Pages;
using MediaEngine.Web.Models.ViewDTOs;

namespace MediaEngine.Web.Tests;

/// <summary>How a lane's tabs are built from the Engine's browse library list (Library setup spec D54), and how old tab links still resolve.</summary>
public sealed class BrowseLibraryTabsTests
{
    private static readonly Guid Youtube = Guid.Parse("d0000000-0000-0000-0000-000000000001");
    private static readonly Guid Lectures = Guid.Parse("d0000000-0000-0000-0000-000000000002");
    private static readonly Guid Recordings = Guid.Parse("d0000000-0000-0000-0000-000000000003");
    private static readonly Guid Music = Guid.Parse("d0000000-0000-0000-0000-000000000004");

    private static readonly LibraryBrowsePreset Fixed = new()
    {
        RouteBase = "/watch",
        Title = "Watch",
        HeroVariant = BrowseHeroVariant.Watch,
        Tabs =
        [
            new BrowseTabPreset { Id = "movies", Label = "Movies", MediaType = "Movies" },
            new BrowseTabPreset
            {
                Id = "videos",
                Label = "Videos",
                MediaType = "Videos",
                DefaultGrouping = "videos",
                GroupingOptions = [new("videos", "Series and videos", "icon")],
            },
            new BrowseTabPreset { Id = "playlists", Label = "Playlists", MediaType = "Music" },
        ],
    };

    private static readonly HashSet<string> FixedTabs = new(StringComparer.OrdinalIgnoreCase) { "playlists" };

    [Fact]
    public void ANamedPlainLibraryGetsItsOwnTabNamedForTheLibraryThatReusesTheVideosPreset()
    {
        var preset = BrowseLibraryTabs.Build(Fixed, "watch",
        [
            new(Youtube, "YouTube", "youtube", "watch", "Videos", true),
            new(Lectures, "Lectures", "other_videos", "watch", "Videos", true),
        ], FixedTabs);

        Assert.Equal(["youtube", $"lib-{Lectures:N}", "playlists"], preset.Tabs.Select(tab => tab.Id));
        var lectures = preset.Tabs[1];
        Assert.Equal("Lectures", lectures.Label);
        Assert.Equal(Lectures, lectures.LibraryId);
        Assert.Equal("videos", lectures.DefaultGrouping);
        Assert.Equal("Series and videos", Assert.Single(lectures.GroupingOptions).Label);
        Assert.Equal("videos", lectures.EffectiveBehaviourId);
        Assert.True(lectures.NarrowToLibrary);
        Assert.Null(preset.Tabs[2].LibraryId);
    }

    [Fact]
    public void AnEmptyListOrNoLibraryForTheAreaKeepsTheFixedTabs()
    {
        Assert.Same(Fixed, BrowseLibraryTabs.Build(Fixed, "watch", [], FixedTabs));
        Assert.Same(Fixed, BrowseLibraryTabs.Build(Fixed, "watch", [new(Music, "Music", "music", "listen", "Music", true)], FixedTabs));
        Assert.Same(Fixed, BrowseLibraryTabs.Build(Fixed, "watch", null, FixedTabs));
    }

    [Fact]
    public void AnOldTabLinkOpensTheFirstTabThatFollowsThatFixedTab()
    {
        var preset = BrowseLibraryTabs.Build(Fixed, "watch",
        [
            new(Youtube, "YouTube", "youtube", "watch", "Videos", true),
            new(Lectures, "Lectures", "other_videos", "watch", "Videos", true),
        ], FixedTabs);

        Assert.Equal("youtube", BrowseQueryBuilder.FindTab(preset, "videos")?.Id);
        Assert.Equal("youtube", BrowseQueryBuilder.FindTab(preset, "YouTube")?.Id);
        Assert.Null(BrowseQueryBuilder.FindTab(preset, "movies"));
        Assert.Equal("youtube", BrowseQueryBuilder.Read(preset, "movies", "http://localhost/watch/movies").ActiveTabId);
    }

    [Fact]
    public void TheUnmatchedValueIsReadOnlyOnALibraryTab()
    {
        var preset = BrowseLibraryTabs.Build(Fixed, "watch", [new(Lectures, "Lectures", "other_videos", "watch", "Videos", true)], FixedTabs);

        Assert.True(BrowseQueryBuilder.Read(preset, $"lib-{Lectures:N}", "http://localhost/watch/x?unmatched=true").Unmatched);
        Assert.False(BrowseQueryBuilder.Read(preset, $"lib-{Lectures:N}", "http://localhost/watch/x").Unmatched);
        Assert.False(BrowseQueryBuilder.Read(Fixed, "movies", "http://localhost/watch/movies?unmatched=true").Unmatched);
    }
}
