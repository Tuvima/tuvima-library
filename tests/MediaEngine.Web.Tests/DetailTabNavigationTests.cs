using MediaEngine.Contracts.Details;
using MediaEngine.Web.Components.Details;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Navigation;

namespace MediaEngine.Web.Tests;

public sealed class DetailTabNavigationTests
{
    [Fact]
    public void Resolve_NoRequestedTab_UsesFirstVisibleTab()
    {
        var model = CreateModel("overview", "cast", "details");

        var resolution = DetailTabNavigation.Resolve(model, null);

        Assert.Equal("overview", resolution.ActiveTab);
        Assert.False(resolution.HasRequestedTab);
        Assert.False(resolution.ShouldRedirect);
    }

    [Fact]
    public void Resolve_RequestedTab_IsCaseInsensitiveAndRedirectsToCanonicalSlug()
    {
        var model = CreateModel(["overview", "cast", "details"], withCast: true);

        var resolution = DetailTabNavigation.Resolve(model, "CAST");

        Assert.Equal("cast", resolution.ActiveTab);
        Assert.True(resolution.IsRequestedTabValid);
        Assert.True(resolution.ShouldRedirect);
    }

    [Fact]
    public void Resolve_InvalidTab_RedirectsToFirstVisibleTab()
    {
        var model = CreateModel("overview", "details");

        var resolution = DetailTabNavigation.Resolve(model, "missing");

        Assert.Equal("overview", resolution.ActiveTab);
        Assert.False(resolution.IsRequestedTabValid);
        Assert.True(resolution.ShouldRedirect);
    }

    [Fact]
    public void Resolve_CastTabWithoutRenderableCredits_FallsBack()
    {
        var model = CreateModel("overview", "cast", "details");

        var resolution = DetailTabNavigation.Resolve(model, "cast");

        Assert.Equal("overview", resolution.ActiveTab);
        Assert.False(resolution.IsRequestedTabValid);
        Assert.True(resolution.ShouldRedirect);
    }

    [Fact]
    public void Resolve_SeriesTabUsesSequencePlacementAndRemainsFirst()
    {
        var model = new DetailPageViewModel
        {
            Id = Guid.NewGuid().ToString("D"),
            EntityType = DetailEntityType.Book,
            Title = "Leviathan Wakes",
            Tabs = [
                new DetailTab { Key = "series", Label = "Series" },
                new DetailTab { Key = "overview", Label = "Overview" },
                new DetailTab { Key = "details", Label = "Details" },
            ],
            SequencePlacement = new SequencePlacementViewModel
            {
                ContainerId = Guid.NewGuid().ToString("D"),
                ContainerTitle = "The Expanse",
                PositionSummary = "Book 1 in The Expanse",
            },
        };

        var resolution = DetailTabNavigation.Resolve(model, null);

        Assert.Equal("series", resolution.ActiveTab);
        Assert.False(resolution.ShouldRedirect);
    }

    [Fact]
    public void Resolve_AudiobookTracksTabRequiresOwnedTrackRows()
    {
        var model = new DetailPageViewModel
        {
            Id = Guid.NewGuid().ToString("D"),
            EntityType = DetailEntityType.Audiobook,
            Title = "Test audiobook",
            Tabs =
            [
                new DetailTab { Key = "overview", Label = "Overview" },
                new DetailTab { Key = "tracks", Label = "Tracks" },
                new DetailTab { Key = "details", Label = "Details" },
            ],
            MediaGroups =
            [
                new MediaGroupingViewModel
                {
                    Key = "tracks",
                    Title = "Tracks",
                    Items = [new MediaGroupingItemViewModel { Id = "track-1", Title = "Track 1", IsOwned = true }],
                },
            ],
        };

        var resolution = DetailTabNavigation.Resolve(model, "tracks");

        Assert.Equal("tracks", resolution.ActiveTab);
        Assert.True(resolution.IsRequestedTabValid);
    }

    [Fact]
    public void MediaNavigation_DoesNotCreateDetailTabUrls()
    {
        var workId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var collectionId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

        var route = MediaNavigation.ForMedia("Movies", workId, collectionId, "cast");

        Assert.Equal(
            "/details/work/aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa?context=watch",
            route);
        Assert.DoesNotContain("tab=", route, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MediaNavigation_OpensTracksAndAlbumsOnTheirOwnCanonicalDetails()
    {
        var trackId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var albumId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

        Assert.Equal(
            $"/listen/music?browse=songs&track={trackId:D}",
            MediaNavigation.ForMedia("Music", trackId, albumId));
        Assert.Equal(
            $"/details/musicalbum/{albumId:D}?context=listen",
            MediaNavigation.ForCollectionMedia("Music", albumId));
    }

    [Fact]
    public void MediaNavigation_UsesRootWorkIdForTvContentGroups()
    {
        var collectionId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        var rootWorkId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
        var group = new ContentGroupViewModel
        {
            CollectionId = collectionId,
            RootWorkId = rootWorkId,
            PrimaryMediaType = "TV",
        };

        Assert.Equal($"/details/tvshow/{rootWorkId:D}?context=watch", MediaNavigation.ForContentGroup(group));
    }

    [Fact]
    public void MediaNavigation_UsesRootWorkIdForMusicAlbumContentGroups()
    {
        var collectionId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        var rootWorkId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
        var group = new ContentGroupViewModel
        {
            CollectionId = collectionId,
            RootWorkId = rootWorkId,
            PrimaryMediaType = "Music",
        };

        Assert.Equal(
            $"/details/musicalbum/{rootWorkId:D}?context=listen",
            MediaNavigation.ForContentGroup(group));
    }

    [Fact]
    public void MediaNavigation_KeepsCollectionFallbackForTvContentGroupsWithoutRoot()
    {
        var collectionId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        var group = new ContentGroupViewModel
        {
            CollectionId = collectionId,
            PrimaryMediaType = "TV",
        };

        Assert.Equal($"/details/tvshow/{collectionId:D}?context=watch", MediaNavigation.ForContentGroup(group));
    }

    [Fact]
    public void MediaNavigation_UsesStandardCollectionDetailForAudiobookShelves()
    {
        var collectionId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        var group = new ContentGroupViewModel
        {
            CollectionId = collectionId,
            PrimaryMediaType = "Audiobooks",
        };

        Assert.Equal(
            $"/details/collection/{collectionId:D}?context=listen",
            MediaNavigation.ForContentGroup(group));
    }

    [Fact]
    public void UnifiedTvShowRoute_LoadsEpisodeThroughTheSharedDetailHost()
    {
        var showId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        var episodeId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");

        var request = DetailRouteRequest.ForUnified("tvshow", showId, "watch", episodeId);

        Assert.True(request.CanLoad);
        Assert.Equal(DetailEntityType.TvEpisode, request.EntityType);
        Assert.Equal(episodeId, request.EntityId);
        Assert.Equal(showId.ToString("D"), request.ContainerId);
        Assert.Equal(DetailPresentationContext.Watch, request.PresentationContext);
    }

    [Fact]
    public void GetVisibleTabs_HidesUniverseAndRelatedWhileTheSharedSwitchIsOff()
    {
        var model = ModelWithRelatedContent("overview", "cast", "universe", "related", "details");

        Assert.False(DetailTabVisibility.ShowUniverseAndRelatedTabs);
        Assert.Equal(["overview", "cast", "details"], DetailTabNavigation.GetVisibleTabs(model).Select(tab => tab.Key));
        Assert.Equal(["overview", "cast", "details"], DetailTabNavigation.GetVisibleTabs(model, showUniverseAndRelated: false).Select(tab => tab.Key));
    }

    [Fact]
    public void GetVisibleTabs_RestoresUniverseAndRelatedWhenTheSwitchIsOn()
    {
        var model = ModelWithRelatedContent("overview", "cast", "universe", "related", "details");

        Assert.Equal(
            ["overview", "cast", "universe", "related", "details"],
            DetailTabNavigation.GetVisibleTabs(model, showUniverseAndRelated: true).Select(tab => tab.Key));
    }

    [Fact]
    public void Resolve_RequestedUniverseOrRelatedTabRedirectsToTheFirstTabWhileOffAndOpensItWhenOn()
    {
        var model = ModelWithRelatedContent("overview", "cast", "universe", "related", "details");

        foreach (var requested in new[] { "universe", "related" })
        {
            var hidden = DetailTabNavigation.Resolve(model, requested, showUniverseAndRelated: false);
            Assert.Equal("overview", hidden.ActiveTab);
            Assert.False(hidden.IsRequestedTabValid);
            Assert.True(hidden.ShouldRedirect);

            var restored = DetailTabNavigation.Resolve(model, requested, showUniverseAndRelated: true);
            Assert.Equal(requested, restored.ActiveTab);
            Assert.True(restored.IsRequestedTabValid);
        }
    }

    [Fact]
    public void GetVisibleTabs_HoldsADashboardBuiltTabListToTheSameSwitch()
    {
        // A tab list the Dashboard assembled itself (not one the Engine built) follows the same rule.
        var model = ModelWithRelatedContent("tracks", "overview", "credits", "related", "details");

        Assert.DoesNotContain(DetailTabNavigation.GetVisibleTabs(model), tab => tab.Key == "related");
    }

    private static DetailPageViewModel ModelWithRelatedContent(params string[] tabs) => new()
    {
        Id = Guid.NewGuid().ToString("D"),
        EntityType = DetailEntityType.Movie,
        Title = "Test",
        Tabs = tabs.Select(tab => new DetailTab { Key = tab, Label = tab }).ToList(),
        ContributorGroups = [new CreditGroupViewModel { Title = "Cast", GroupType = CreditGroupType.Cast }],
        MediaGroups = [new MediaGroupingViewModel { Key = "related", Title = "More like this" }],
    };

    private static DetailPageViewModel CreateModel(params string[] tabs) =>
        CreateModel(tabs, withCast: false);

    private static DetailPageViewModel CreateModel(string[] tabs, bool withCast) =>
        new()
        {
            Id = Guid.NewGuid().ToString("D"),
            EntityType = DetailEntityType.Movie,
            Title = "Test",
            Tabs = tabs.Select(tab => new DetailTab { Key = tab, Label = tab }).ToList(),
            ContributorGroups = withCast
                ? [new CreditGroupViewModel { Title = "Cast", GroupType = CreditGroupType.Cast }]
                : [],
        };
}
