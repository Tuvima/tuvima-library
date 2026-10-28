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
    public void YouTubeAndNamedPlainVideoLibrariesShareOneVideosTabWithAChipEachThatReusesTheVideosPreset()
    {
        var preset = BrowseLibraryTabs.Build(Fixed, "watch",
        [
            new(Youtube, "YouTube", "youtube", "watch", "Videos", true),
            new(Lectures, "Lectures", "other_videos", "watch", "Videos", true),
        ], FixedTabs);

        Assert.Equal(["videos", "playlists"], preset.Tabs.Select(tab => tab.Id));
        var videos = preset.Tabs[0];
        Assert.Equal("Videos", videos.Label);
        Assert.Null(videos.LibraryId);
        Assert.Equal([Youtube, Lectures], videos.LibraryChips.Select(chip => chip.Id));
        Assert.Equal(["YouTube", "Lectures"], videos.LibraryChips.Select(chip => chip.Label));
        Assert.Equal("videos", videos.DefaultGrouping);
        Assert.Equal("Series and videos", Assert.Single(videos.GroupingOptions).Label);
        Assert.Equal("videos", videos.EffectiveBehaviourId);
        Assert.Null(preset.Tabs[1].LibraryId);
    }

    [Fact]
    public void ThreeOtherVideosLibrariesGiveOneVideosTabWithThreeChips()
    {
        var home = Guid.Parse("d0000000-0000-0000-0000-000000000011");
        var tiktok = Guid.Parse("d0000000-0000-0000-0000-000000000012");
        var preset = BrowseLibraryTabs.Build(Fixed, "watch",
        [
            new(home, "Home videos", "other_videos", "watch", "Videos", true),
            new(tiktok, "TikTok edits", "other_videos", "watch", "Videos", true),
            new(Lectures, "Other videos", "other_videos", "watch", "Videos", true),
        ], FixedTabs);

        var videos = Assert.Single(preset.Tabs, tab => tab.Id == "videos");
        Assert.Equal(["Home videos", "TikTok edits", "Other videos"], videos.LibraryChips.Select(chip => chip.Label));
    }

    [Fact]
    public void ALibraryTheProfileCannotSeeIsNotInTheListSoItHasNoChip()
    {
        // Lectures was left out of the Engine's list for this profile.
        var preset = BrowseLibraryTabs.Build(Fixed, "watch",
        [
            new(Youtube, "YouTube", "youtube", "watch", "Videos", true),
            new(Recordings, "Recordings", "other_videos", "watch", "Videos", true),
        ], FixedTabs);

        var chips = preset.Tabs.Single(tab => tab.Id == "videos").LibraryChips;
        Assert.DoesNotContain(chips, chip => chip.Id == Lectures);
        Assert.Equal(2, chips.Count);
    }

    [Fact]
    public void ALoneVideosLibraryIsTheTabsOwnLibraryAndNeedsNoChips()
    {
        var preset = BrowseLibraryTabs.Build(Fixed, "watch", [new(Lectures, "Lectures", "other_videos", "watch", "Videos", true)], FixedTabs);

        var videos = preset.Tabs[0];
        Assert.Equal(Lectures, videos.LibraryId);
        Assert.Empty(videos.LibraryChips);
    }

    [Fact]
    public void ALibraryChipInTheAddressNarrowsTheTabOnlyWhenTheTabListsThatLibrary()
    {
        var preset = BrowseLibraryTabs.Build(Fixed, "watch",
        [
            new(Youtube, "YouTube", "youtube", "watch", "Videos", true),
            new(Lectures, "Lectures", "other_videos", "watch", "Videos", true),
        ], FixedTabs);

        Assert.Equal(Lectures, BrowseQueryBuilder.Read(preset, "videos", $"http://localhost/watch/videos?library={Lectures:D}").LibraryChip);
        Assert.Null(BrowseQueryBuilder.Read(preset, "videos", $"http://localhost/watch/videos?library={Recordings:D}").LibraryChip);
        Assert.Null(BrowseQueryBuilder.Read(preset, "videos", "http://localhost/watch/videos").LibraryChip);
        Assert.True(BrowseQueryBuilder.Read(preset, "videos", $"http://localhost/watch/videos?library={Lectures:D}&unmatched=true").Unmatched);
        Assert.False(BrowseQueryBuilder.Read(preset, "videos", "http://localhost/watch/videos?unmatched=true").Unmatched);
    }

    [Fact]
    public void AListThatCouldNotBeReadKeepsTheFixedTabs()
    {
        Assert.Same(Fixed, BrowseLibraryTabs.Build(Fixed, "watch", null, FixedTabs));
    }

    [Fact]
    public void AListThatWasReadButHoldsNothingUsableForTheAreaGivesNoTabsAtAll()
    {
        Assert.Empty(BrowseLibraryTabs.Build(Fixed, "watch", [], FixedTabs).Tabs);
        Assert.Empty(BrowseLibraryTabs.Build(Fixed, "watch", [new(Music, "Music", "music", "listen", "Music", true)], FixedTabs).Tabs);
        Assert.Empty(BrowseLibraryTabs.Build(Fixed, "watch", [new(Lectures, "Lectures", "other_videos", "watch", "Videos", false)], FixedTabs).Tabs);
    }

    [Fact]
    public void AnOldTabLinkOpensTheFirstTabThatFollowsThatFixedTab()
    {
        var preset = BrowseLibraryTabs.Build(Fixed, "watch",
        [
            new(Youtube, "YouTube", "youtube", "watch", "Videos", true),
            new(Lectures, "Lectures", "other_videos", "watch", "Videos", true),
        ], FixedTabs);

        Assert.Equal("videos", BrowseQueryBuilder.FindTab(preset, "videos")?.Id);
        Assert.Equal("videos", BrowseQueryBuilder.FindTab(preset, "YouTube")?.Id);
        Assert.Equal("videos", BrowseQueryBuilder.FindTab(preset, $"lib-{Lectures:N}")?.Id);
        Assert.Null(BrowseQueryBuilder.FindTab(preset, "movies"));
        Assert.Equal("videos", BrowseQueryBuilder.Read(preset, "movies", "http://localhost/watch/movies").ActiveTabId);
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
