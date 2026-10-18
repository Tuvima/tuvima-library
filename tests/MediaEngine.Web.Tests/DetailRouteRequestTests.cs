using MediaEngine.Contracts.Details;
using MediaEngine.Web.Components.Details;

namespace MediaEngine.Web.Tests;

public sealed class DetailRouteRequestTests
{
    private static readonly Guid EntityId = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid ContainerId = Guid.Parse("20000000-0000-0000-0000-000000000002");

    [Theory]
    [InlineData("read", DetailPresentationContext.Read)]
    [InlineData("LISTEN", DetailPresentationContext.Listen)]
    [InlineData("watch", DetailPresentationContext.Watch)]
    public void WorkRoute_UsesEngineInferredTypeWithLaneContext(
        string context,
        DetailPresentationContext expectedContext)
    {
        var request = DetailRouteRequest.ForUnified("work", EntityId, context);

        Assert.Equal(DetailEntityType.Work, request.EntityType);
        Assert.Equal(EntityId, request.EntityId);
        Assert.Equal(expectedContext, request.PresentationContext);
        Assert.Null(request.ContainerId);
        Assert.Equal("Detail page not found", request.NotFoundTitle);
        Assert.Equal("Details", request.PageTitleFallback);
        Assert.Equal("Tuvima Library", request.ProductTitle);
    }

    [Fact]
    public void TvShowRoute_LoadsRootShowWithoutAContainer()
    {
        var request = DetailRouteRequest.ForUnified("tvshow", ContainerId, "watch");

        Assert.Equal(DetailEntityType.TvShow, request.EntityType);
        Assert.Equal(ContainerId, request.EntityId);
        Assert.Equal(DetailPresentationContext.Watch, request.PresentationContext);
        Assert.Null(request.ContainerId);
        Assert.Equal("Detail page not found", request.NotFoundTitle);
        Assert.Equal("Details", request.PageTitleFallback);
        Assert.Equal("Tuvima Library", request.ProductTitle);
    }

    [Fact]
    public void TvEpisodeRoute_PreservesItsShowContainer()
    {
        var request = DetailRouteRequest.ForUnified("tvshow", ContainerId, "watch", EntityId);

        Assert.Equal(DetailEntityType.TvEpisode, request.EntityType);
        Assert.Equal(EntityId, request.EntityId);
        Assert.Equal(ContainerId.ToString("D"), request.ContainerId);
        Assert.Equal("Episode not found", request.NotFoundTitle);
    }

    [Fact]
    public void TvShowRouteWithASeason_AsksForThatSeasonInsideItsShow()
    {
        var request = DetailRouteRequest.ForUnified("tvshow", ContainerId, "watch", seasonId: EntityId);

        Assert.True(request.CanLoad);
        Assert.Equal(DetailEntityType.TvSeason, request.EntityType);
        Assert.Equal(EntityId, request.EntityId);
        Assert.Equal(ContainerId.ToString("D"), request.ContainerId);
        Assert.Equal(DetailPresentationContext.Watch, request.PresentationContext);
        Assert.Equal("Back to Watch", request.OriginNavigation?.Label);
        Assert.Equal("Season not found", request.NotFoundTitle);
    }

    [Theory]
    [InlineData("tvseason")]
    [InlineData("tv-season")]
    [InlineData("TvSeason")]
    public void TvSeasonRoute_AsksForTheSeasonAndLetsTheEngineFindItsShow(string entityType)
    {
        var request = DetailRouteRequest.ForUnified(entityType, EntityId, null);

        Assert.True(request.CanLoad);
        Assert.Equal(DetailEntityType.TvSeason, request.EntityType);
        Assert.Equal(EntityId, request.EntityId);
        Assert.Null(request.ContainerId);
        Assert.Equal(DetailPresentationContext.Watch, request.PresentationContext);
        Assert.Equal("Season not found", request.NotFoundTitle);
    }

    [Fact]
    public void TvShowRouteWithAnEpisodeAndASeason_OpensTheEpisode()
    {
        var request = DetailRouteRequest.ForUnified("tvshow", ContainerId, "watch", EntityId, Guid.NewGuid());

        Assert.Equal(DetailEntityType.TvEpisode, request.EntityType);
        Assert.Equal(EntityId, request.EntityId);
        Assert.Equal(ContainerId.ToString("D"), request.ContainerId);
    }

    [Fact]
    public void SeasonQueryOnAnotherKindOfPage_IsIgnored()
    {
        var request = DetailRouteRequest.ForUnified("movie", ContainerId, "watch", seasonId: EntityId);

        Assert.Equal(DetailEntityType.Movie, request.EntityType);
        Assert.Equal(ContainerId, request.EntityId);
        Assert.Null(request.ContainerId);
    }

    [Theory]
    [InlineData("music-album", "listen", DetailEntityType.MusicAlbum, DetailPresentationContext.Listen, "Back to Listen", "/listen")]
    [InlineData("comic_issue", "comics", DetailEntityType.ComicIssue, DetailPresentationContext.Comics, "Back to Read", "/read")]
    [InlineData("movie", "watch", DetailEntityType.Movie, DetailPresentationContext.Watch, "Back to Watch", "/watch")]
    [InlineData("person", "not-a-context", DetailEntityType.Person, DetailPresentationContext.Default, "Back", "/")]
    public void UnifiedRoute_ParsesEntityContextAndOrigin(
        string entityType,
        string context,
        DetailEntityType expectedEntityType,
        DetailPresentationContext expectedContext,
        string expectedBackLabel,
        string expectedFallbackRoute)
    {
        var request = DetailRouteRequest.ForUnified(entityType, EntityId, context);

        Assert.True(request.CanLoad);
        Assert.Equal(expectedEntityType, request.EntityType);
        Assert.Equal(expectedContext, request.PresentationContext);
        Assert.Equal(expectedBackLabel, request.OriginNavigation?.Label);
        Assert.Equal(expectedFallbackRoute, request.OriginNavigation?.FallbackRoute);
        Assert.Equal("Details", request.PageTitleFallback);
        Assert.Equal("Tuvima Library", request.ProductTitle);
    }

    [Theory]
    [InlineData("tv-episode")]
    [InlineData("TvEpisode")]
    [InlineData("podcast")]
    [InlineData("PodcastSeries")]
    [InlineData("not-real")]
    public void UnifiedRoute_RejectsUnsupportedEntities(string entityType)
    {
        var request = DetailRouteRequest.ForUnified(entityType, EntityId, "watch");

        Assert.False(request.CanLoad);
        Assert.Null(request.EntityType);
        Assert.Equal("Detail page not found", request.NotFoundTitle);
    }

    [Fact]
    public void TvShowRoute_ReloadsWhenTheContextChangesButNotWhenOnlyASeasonIsPicked()
    {
        var season = Guid.NewGuid();

        var plain = DetailRouteRequest.ForUnified("tvshow", EntityId, null);
        var listen = DetailRouteRequest.ForUnified("tvshow", EntityId, "listen");
        var withSeason = DetailRouteRequest.ForUnified("tvshow", EntityId, null, seasonId: season);
        var seasonInListen = DetailRouteRequest.ForUnified("tvshow", EntityId, "listen", seasonId: season);

        Assert.NotEqual(plain.LoadKey, listen.LoadKey);
        Assert.Equal(plain.LoadKey, withSeason.LoadKey);
        Assert.Equal(listen.LoadKey, seasonInListen.LoadKey);
        Assert.Equal(plain.LoadKey, DetailRouteRequest.ForUnified("tvseason", season, null).ShowLoadKey(EntityId));
    }
}
