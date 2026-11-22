using Bunit;
using MediaEngine.Web.Components.MediaTiles;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.MediaTiles;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>
/// A playlist is the shared square album quadrant (spec group-pages-and-tiles D1, D1a, WP1): the artist tile's 2 by 2 with no photo, filled with the
/// first four albums of its songs, a missing quarter a faint dashed square, the name on the scrim, the type line as the bottom label. With no covers
/// it is the shared no-covers tile with the playlist icon; a cover the person chose fills the tile whole.
/// </summary>
public sealed class PlaylistTileTests : AsyncBunitContext
{
    private static readonly PlaylistTileWords Words = new("Playlist", "Smart playlist", "Built in", "1 song", "{0} songs");

    public PlaylistTileTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddNativeUiServices();
    }

    private static ManagedCollectionViewModel Playlist(string name = "Road trip", string type = "Playlist", int items = 48, double? seconds = null, string? cover = null) => new()
    {
        Id = Guid.Parse("b1000000-0000-0000-0000-000000000001"),
        Name = name,
        CollectionType = type,
        MembershipMode = type == "Smart" ? "Smart" : "Manual",
        Resolution = type == "Smart" ? "query" : "materialized",
        CanEdit = true,
        IsEnabled = true,
        ItemCount = items,
        TotalDurationSeconds = seconds,
        CoverArtworkUrl = cover,
    };

    private IRenderedComponent<MediaTile> Tile(ManagedCollectionViewModel playlist, params string[] covers) =>
        Render<MediaTile>(p => p.Add(c => c.Item, PlaylistTileComposer.Compose(playlist, covers, PlaylistTileComposer.TypeLine(playlist, Words, includeLength: true))));

    // -- The type line ---------------------------------------------------------------------------------------

    [Fact]
    public void TheTypeLineSaysWhatItIsTheCountAndTheLengthWhenKnown()
    {
        Assert.Equal("Playlist · 48 songs · 3h 12m", PlaylistTileComposer.TypeLine(Playlist(seconds: 3 * 3600 + 12 * 60), Words, includeLength: true));
        Assert.Equal("Playlist · 48 songs", PlaylistTileComposer.TypeLine(Playlist(), Words, includeLength: true));
        Assert.Equal("Smart playlist · 32 songs", PlaylistTileComposer.TypeLine(Playlist(type: "Smart", items: 32), Words, includeLength: true));
        Assert.Equal("Playlist · 1 song", PlaylistTileComposer.TypeLine(Playlist(items: 1), Words, includeLength: true));
        Assert.Equal("Built in · 126 songs", PlaylistTileComposer.BuiltInTypeLine(126, Words));
        Assert.Equal("Built in", PlaylistTileComposer.BuiltInTypeLine(null, Words));
    }

    [Fact]
    public void OnAPhoneTheLengthIsLeftOutSoTwoTilesFitAcross()
    {
        Assert.Equal("Playlist · 48 songs", PlaylistTileComposer.TypeLine(Playlist(seconds: 11520), Words, includeLength: false));
    }

    // -- The covers ------------------------------------------------------------------------------------------

    [Fact]
    public void ThePlaylistTakesTheFirstFourDifferentAlbumCoversInSongOrder()
    {
        var tile = PlaylistTileComposer.Compose(Playlist(), ["/c/1.jpg", "/c/1.jpg", "", "/c/2.jpg", "/c/3.jpg", "/c/4.jpg", "/c/5.jpg"]);

        Assert.Equal(["/c/1.jpg", "/c/2.jpg", "/c/3.jpg", "/c/4.jpg"], tile.ArtworkStackItems.Select(item => item.ImageUrl).ToList());
        Assert.All(tile.ArtworkStackItems, item => Assert.Equal(ArtworkShape.Square, item.Shape));
        Assert.True(tile.IsPlaylist);
        Assert.True(tile.AcceptsSongDrops);
    }

    [Fact]
    public void ACoverThePersonChoseReplacesTheAlbums()
    {
        var tile = PlaylistTileComposer.Compose(Playlist(cover: "/collections/x/artwork/poster"), ["/c/1.jpg", "/c/2.jpg"]);

        Assert.Equal(["/collections/x/artwork/poster"], tile.ArtworkStackItems.Select(item => item.ImageUrl).ToList());
    }

    // -- What is drawn ---------------------------------------------------------------------------------------

    [Fact]
    public void FourAlbumsAreTheSquareQuadrantWithNoPhotoTheNameOnTheScrimAndTheTypeLineBelow()
    {
        var cut = Tile(Playlist(seconds: 11520), "/c/1.jpg", "/c/2.jpg", "/c/3.jpg", "/c/4.jpg");

        Assert.Contains("is-square", cut.Find("article.media-tile").ClassList);
        Assert.Equal(4, cut.FindAll("img.media-tile-quadrant").Count);
        Assert.Empty(cut.FindAll(".media-tile-quadrant.is-empty"));
        Assert.Empty(cut.FindAll(".media-tile-person-photo"));
        Assert.Equal("Road trip", cut.Find(".media-tile-name").TextContent.Trim());
        Assert.DoesNotContain("media-tile-name--beside-photo", cut.Find(".media-tile-name").ClassList);
        Assert.Equal("Playlist · 48 songs · 3h 12m", cut.Find(".media-tile-label--bottom").TextContent.Trim());
        Assert.DoesNotContain("media-tile-label--beside-photo", cut.Find(".media-tile-label--bottom").ClassList);
        Assert.Equal("Road trip, Playlist", cut.Find("a.media-tile-link").GetAttribute("aria-label"));
        Assert.Empty(cut.FindAll(".media-tile-label--kind"));
    }

    [Fact]
    public void AMissingQuarterIsAFaintDashedSquare()
    {
        var cut = Tile(Playlist(), "/c/1.jpg", "/c/2.jpg", "/c/3.jpg");

        Assert.Equal(3, cut.FindAll("img.media-tile-quadrant").Count);
        Assert.Single(cut.FindAll(".media-tile-quadrant.is-empty"));
    }

    [Fact]
    public void APlaylistWithNoCoversIsTheSharedNoCoversTileWithThePlaylistIcon()
    {
        var cut = Tile(Playlist());

        Assert.Empty(cut.FindAll(".media-tile-quadrants"));
        Assert.Single(cut.FindAll(".media-tile-fallback"));
        Assert.Equal("Road trip", cut.Find(".media-tile-fallback__title").TextContent.Trim());
        Assert.Empty(cut.FindAll(".media-tile-fallback__detail"));
        Assert.Equal("Playlist · 48 songs", cut.Find(".media-tile-label--bottom").TextContent.Trim());
        Assert.Contains(PlaylistIconShape(), cut.Find(".media-tile-fallback").InnerHtml, StringComparison.Ordinal);
    }

    [Fact]
    public void ACoverThePersonChoseFillsTheTileWhole()
    {
        var cut = Tile(Playlist(cover: "/collections/x/artwork/poster"), "/c/1.jpg");

        Assert.Single(cut.FindAll("img.media-tile-quadrant"));
        Assert.Contains("is-single", cut.Find("img.media-tile-quadrant").ClassList);
        Assert.Contains("/collections/x/artwork/poster", cut.Find("img.media-tile-quadrant").GetAttribute("src"), StringComparison.Ordinal);
    }

    [Fact]
    public void OnlyAHandMadePlaylistsTileIsADropTarget()
    {
        Assert.Equal("playlist", Tile(Playlist(), "/c/1.jpg").Find("article.media-tile").GetAttribute("data-song-drop"));
        Assert.Null(Tile(Playlist(type: "Smart"), "/c/1.jpg").Find("article.media-tile").GetAttribute("data-song-drop"));
        Assert.Null(Render<MediaTile>(p => p.Add(c => c.Item, PlaylistTileComposer.ComposeBuiltIn("Favorites", "/listen/music/playlists/system/favorite-songs", 126, Words)))
            .Find("article.media-tile").GetAttribute("data-song-drop"));
    }

    [Fact]
    public void ATileWithAPageMenuDrawsItInTheSharedSlotInsteadOfItsOwn()
    {
        var cut = Render<MediaTile>(p => p
            .Add(c => c.Item, PlaylistTileComposer.Compose(Playlist(), ["/c/1.jpg"]))
            .Add(c => c.Menu, builder => builder.AddMarkupContent(0, "<button data-page-menu>menu</button>")));

        Assert.Single(cut.FindAll(".media-tile-actions [data-page-menu]"));
    }

    // The drawing of the Queue music icon, so the test says "the playlist icon" without knowing its path data.
    private string PlaylistIconShape() =>
        Render<MediaEngine.Web.Components.Shared.AppMaterialIcon>(p => p.Add(c => c.Icon, MediaEngine.Web.Components.Shared.AppMaterialIcons.Outlined.QueueMusic))
            .Find("path").GetAttribute("d")!;
}
