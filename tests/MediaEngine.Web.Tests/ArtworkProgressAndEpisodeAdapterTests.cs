using Bunit;
using MediaEngine.Contracts.Details;
using MediaEngine.Contracts.Display;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.MediaTiles;

namespace MediaEngine.Web.Tests;

public sealed class ArtworkProgressAndEpisodeAdapterTests : AsyncBunitContext
{
    [Theory]
    [InlineData(600, 900, "s 213w, m 600w")]
    [InlineData(1200, 1200, "s 320w, m 960w, l 1200w")]
    [InlineData(3840, 2160, "s 320w, m 960w, l 2160w")]
    [InlineData(200, 300, "s 200w")]
    public void ResponsiveSourcesUseLongEdgeBoundsWithoutUpscaling(int width, int height, string expected)
        => Assert.Equal(expected, MediaTileArtworkUrl.SrcSet("s", "m", "l", width, height));
    [Fact]
    public void UnmeasuredSourcesOmitUnverifiableWidthDescriptors()
    {
        Assert.Null(MediaTileArtworkUrl.SrcSet("s", "m", "l", null, 900));
        Assert.Null(MediaTileArtworkUrl.SrcSet("s", "m"));
        var surface = MediaTileArtworkResolver.Resolve(MediaTileBucket.Book, MediaTilePresentation.Default, [new(ArtworkRole.Cover, "s", "m", WidthPx: 600, HeightPx: 900)]);
        Assert.Equal("s 213w, m 600w", surface.TileImageSrcSet);
        Assert.Equal("s", surface.TileImageUrl);
    }
    [Fact]
    public void StructuralGroupKeepsFixedLandscapeFrameWithPortraitArtwork()
    {
        var surface = MediaTileArtworkResolver.Resolve(MediaTileBucket.Book, MediaTilePresentation.BookSeries, [new(ArtworkRole.Cover, "s", "m", WidthPx: 600, HeightPx: 900)], preferLandscapeTile: true);
        Assert.Equal(MediaTileShape.Landscape, surface.Shape); Assert.Equal("s", surface.TileImageUrl);
    }
    [Fact]
    public void PartialStripHasAccessibleRealPercentAndRemainingWhileCompletedHasNoResumeStrip()
    {
        var cut = Render<AppArtworkProgress>(p => p.Add(c => c.State, DisplayContinuationState.InProgress).Add(c => c.Percent, 42d).Add(c => c.MediaKind, "TV").Add(c => c.RemainingSeconds, 600d));
        Assert.Equal("42", cut.Find("[role=progressbar]").GetAttribute("aria-valuenow"));
        Assert.Contains("10 min remaining", cut.Find("[role=progressbar]").GetAttribute("aria-valuetext"));
        cut.Render(p => p.Add(c => c.State, DisplayContinuationState.Completed).Add(c => c.Percent, 100d));
        Assert.Empty(cut.FindAll("[role=progressbar]")); Assert.Contains("Watched", cut.Markup);
        cut.Render(p => p.Add(c => c.State, DisplayContinuationState.Unstarted).Add(c => c.Percent, 0d));
        Assert.Empty(cut.FindAll("[role=progressbar]")); Assert.DoesNotContain("Watched", cut.Markup);
        cut.Render(p => p.Add(c => c.Enabled, false).Add(c => c.State, DisplayContinuationState.Completed)); Assert.Equal(string.Empty, cut.Markup.Trim());
    }
    [Fact]
    public void EpisodeSequenceAndDisplayShareIdentityNavigationArtAndSavedState()
    {
        var show = Guid.NewGuid(); var episode = Guid.NewGuid(); var asset = Guid.NewGuid();
        var context = new DisplayEpisodeContextDto(show, episode, asset, "Show", "Episode", 2, 5, DisplayContinuationState.InProgress, 300, 900);
        var item = new SequenceItemViewModel { Id = episode.ToString("D"), Title = "Episode", IsOwned = true, EntityType = DetailEntityType.TvEpisode, ArtworkUrl = $"/stream/artwork/{asset:D}", EpisodeContext = context, ProgressPercent = 42, RemainingSeconds = 600, Description = "Episode synopsis" };
        var tile = SequenceEpisodeTileAdapter.FromItem(item);
        Assert.Equal(DisplaySubjectKind.TvEpisode, tile.Subject); Assert.Equal(context, tile.EpisodeContext);
        Assert.Equal(TvEpisodeDetailRoute.Build(show, episode, "watch"), tile.DetailsNavigationUrl);
        Assert.Equal("S2 E5", tile.Subtitle); Assert.Equal("Episode synopsis", tile.Description);
        Assert.Equal(MediaTileShape.Landscape, tile.Shape); Assert.Contains("size=s", tile.TileImageUrl); Assert.Equal(600, tile.RemainingSeconds);
    }
    private static SequenceItemViewModel StandInItem(bool standIn, string? still = null)
    {
        var show = Guid.NewGuid(); var episode = Guid.NewGuid(); var asset = Guid.NewGuid();
        var context = new DisplayEpisodeContextDto(show, episode, asset, "Show", "Episode", 4, 7, DisplayContinuationState.Unstarted, null, null);
        return new SequenceItemViewModel { Id = episode.ToString("D"), Title = "Episode", IsOwned = true, EntityType = DetailEntityType.TvEpisode, EpisodeContext = context, PictureIsStandIn = standIn, StandInRetryAfter = "2027-02-01", EpisodeStillUrl = still };
    }
    [Fact]
    public void EpisodeWithoutAPicture_IsDrawnAsAMarkedStandInFromTheShowBackdrop()
    {
        var tile = SequenceEpisodeTileAdapter.FromItem(StandInItem(true), "/stream/artwork/show-backdrop");
        Assert.True(tile.IsStandInPicture); Assert.Equal("E7", tile.StandInLabel); Assert.Equal("2027-02-01", tile.StandInRetryAfter);
        Assert.Contains("/stream/artwork/show-backdrop", tile.TileImageUrl);
    }
    [Fact]
    public void ARealStill_AlwaysWinsOverTheStandIn()
    {
        var tile = SequenceEpisodeTileAdapter.FromItem(StandInItem(true, "/stream/artwork/real"), "/stream/artwork/show-backdrop");
        Assert.False(tile.IsStandInPicture); Assert.Null(tile.StandInLabel);
        Assert.False(SequenceEpisodeTileAdapter.FromItem(StandInItem(false), "/stream/artwork/show-backdrop").IsStandInPicture);
    }
    [Fact]
    public void StandInTile_ShowsTheEpisodeNumberAndTheShowImageTag_WithABackdrop()
    {
        var tile = SequenceEpisodeTileAdapter.FromItem(StandInItem(true), "/stream/artwork/show-backdrop");
        var cut = Render<MediaEngine.Web.Components.MediaTiles.MediaTile>(p => p.Add(c => c.Item, tile));
        Assert.Equal("E7", cut.Find(".media-tile-standin__number").TextContent.Trim());
        Assert.Equal("Show image", cut.Find(".media-tile-standin__tag").TextContent.Trim());
        Assert.Contains("Library checks again every 90 days", cut.Find(".media-tile-standin__tag").GetAttribute("title"));
        Assert.NotEmpty(cut.FindAll(".media-tile-standin__image"));
    }
    [Fact]
    public void StandInTile_FallsBackToALibraryTile_WhenTheShowHasNoBackdrop()
    {
        var tile = SequenceEpisodeTileAdapter.FromItem(StandInItem(true), null);
        var cut = Render<MediaEngine.Web.Components.MediaTiles.MediaTile>(p => p.Add(c => c.Item, tile));
        Assert.Empty(cut.FindAll(".media-tile-standin__image"));
        Assert.NotEmpty(cut.FindAll(".media-tile-standin__library-tile"));
        Assert.Equal("E7", cut.Find(".media-tile-standin__number").TextContent.Trim());
        Assert.Equal("Show image", cut.Find(".media-tile-standin__tag").TextContent.Trim());
    }
    [Theory]
    [InlineData(600, 900, MediaTileShape.Square)]
    [InlineData(900, 900, MediaTileShape.Square)]
    [InlineData(1600, 900, MediaTileShape.Square)]
    public void AudiobookUsesMeasuredCoverShape(int width, int height, MediaTileShape expected)
    {
        var surface = MediaTileArtworkResolver.Resolve(MediaTileBucket.Audiobook, MediaTilePresentation.Default, [new(ArtworkRole.Cover, "/small.jpg", "/medium.jpg", WidthPx: width, HeightPx: height)]);
        Assert.Equal(expected, surface.Shape); Assert.Equal("/small.jpg", surface.TileImageUrl);
    }
    [Fact]
    public void MissingLandscapeKeepsActualCoverInsteadOfStretchingIt()
    {
        var surface = MediaTileArtworkResolver.Resolve(MediaTileBucket.Movie, MediaTilePresentation.Default, [new(ArtworkRole.Cover, "/small.jpg", "/medium.jpg", WidthPx: 600, HeightPx: 900)], preferLandscapeTile: true);
        Assert.Equal(MediaTileShape.Landscape, surface.Shape); Assert.Equal(MediaTileSurfaceKind.BannerLandscape, surface.SurfaceKind); Assert.Equal("/small.jpg", surface.TileImageUrl);
    }
}
