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
    public void Resolve_WithoutARequestedTabOpensOverviewAndNeverASeriesTab()
    {
        var model = new DetailPageViewModel
        {
            Id = Guid.NewGuid().ToString("D"),
            EntityType = DetailEntityType.Book,
            Title = "Leviathan Wakes",
            Tabs = [
                new DetailTab { Key = "overview", Label = "Overview" },
                new DetailTab { Key = "credits", Label = "Credits" },
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
        var requestedSeries = DetailTabNavigation.Resolve(model, "series");

        Assert.Equal("overview", resolution.ActiveTab);
        Assert.False(resolution.ShouldRedirect);
        Assert.Equal("overview", requestedSeries.ActiveTab);
        Assert.True(requestedSeries.ShouldRedirect);
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
    public void Resolve_RequestedUniverseOrRelatedTabRedirectsToTheFirstTab()
    {
        var model = CreateModel(["overview", "cast", "details"], withCast: true);

        foreach (var requested in new[] { "universe", "related" })
        {
            var resolution = DetailTabNavigation.Resolve(model, requested);

            Assert.Equal("overview", resolution.ActiveTab);
            Assert.False(resolution.IsRequestedTabValid);
            Assert.True(resolution.ShouldRedirect);
        }
    }

    [Theory]
    [InlineData("characters")]
    [InlineData("portrayals")]
    [InlineData("relationships")]
    public void Resolve_RequestedCharacterTabRedirectsToTheFirstTab(string requested)
    {
        var model = CreateModel(["overview", "cast", "details"], withCast: true);

        var resolution = DetailTabNavigation.Resolve(model, requested);

        Assert.Equal("overview", resolution.ActiveTab);
        Assert.False(resolution.IsRequestedTabValid);
        Assert.True(resolution.ShouldRedirect);
    }

    [Fact]
    public void Resolve_RequestedRegistryTabRedirectsToTheFirstTab()
    {
        var model = CreateModel("overview", "details");

        var resolution = DetailTabNavigation.Resolve(model, "registry");

        Assert.Equal("overview", resolution.ActiveTab);
        Assert.False(resolution.IsRequestedTabValid);
        Assert.True(resolution.ShouldRedirect);
    }

    [Fact]
    public void UnifiedRoute_ForACharacterAddressCannotLoadSoThePageReadsNotFound()
    {
        var request = DetailRouteRequest.ForUnified("character", Guid.NewGuid(), "universe");

        Assert.False(request.CanLoad);
        Assert.Equal("Detail page not found", request.NotFoundTitle);
    }

    [Theory]
    [InlineData("admin")]
    [InlineData("ADMIN")]
    public void UnifiedRoute_TreatsTheAdminAddressFlagAsTheDefaultContext(string contextValue)
    {
        var request = DetailRouteRequest.ForUnified("work", Guid.NewGuid(), contextValue);

        Assert.Equal(DetailPresentationContext.Default, request.PresentationContext);
    }

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
