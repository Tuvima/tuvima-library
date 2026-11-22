using Bunit;
using MediaEngine.Contracts.Collections;
using MediaEngine.Web.Components.MediaTiles;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.MediaTiles;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>
/// The quadrant tile for people with works (spec details-and-tile-fixes D2): four covers in a 2 by 2 grid, a person's square photo
/// bottom left with the count beside it; a person with no works is a square photo. A group of titles is the landscape cover collage
/// (<see cref="GroupCollageTileTests"/>). The fan, the single-cover group fallback and the round person tile are gone.
/// </summary>
public sealed class GroupQuadrantTileTests : AsyncBunitContext
{
    public GroupQuadrantTileTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddNativeUiServices();
    }

    [Fact]
    public void AGroupWithNoCovers_IsThePlainTileWithTheKindIcon()
    {
        var cut = Render<MediaTile>(p => p.Add(c => c.Item, Group([])));

        Assert.Empty(cut.FindAll(".media-tile-quadrants"));
        Assert.Empty(cut.FindAll(".group-collage"));
        Assert.Single(cut.FindAll(".media-tile-fallback"));
    }

    [Theory]
    [InlineData("/v/folder.jpg", null, "/v/folder.jpg")]
    [InlineData(null, "/v/frame.jpg", "/v/frame.jpg")]
    public void AFolderOfVideosWithNoChosenCover_DrawsItsFolderPictureOrAFrameFromItsVideos(string? tileImage, string? memberImage, string expected)
    {
        var item = Group(memberImage is null ? [] : Covers(memberImage), mediaKind: MediaEngine.Domain.Services.VideoFolderWording.FolderMediaType, tileImage: tileImage);

        var cut = Render<MediaTile>(p => p.Add(c => c.Item, item));

        Assert.Empty(cut.FindAll(".media-tile-fallback"));
        Assert.Contains(cut.FindAll("img"), img => (img.GetAttribute("src") ?? string.Empty).Contains(expected, StringComparison.Ordinal));
    }

    [Fact]
    public void TheKindWordIsTheTopLeftLabel_AndAStatusLabelTakesTheSlotFirst()
    {
        var plain = Render<MediaTile>(p => p.Add(c => c.Item, Group(Covers("/c/1.jpg"), kind: MediaTileGroupKind.Universe)));
        var isNew = Render<MediaTile>(p => p.Add(c => c.Item, Group(Covers("/c/1.jpg"), newLabel: "New")));

        Assert.Equal("Universe", plain.Find(".media-tile-label--status").TextContent.Trim());
        Assert.Equal("New", isNew.Find(".media-tile-label--status").TextContent.Trim());
        Assert.Empty(isNew.FindAll(".media-tile-label--kind"));
    }

    [Fact]
    public void AnArtistWithAlbums_IsTheSquareQuadrantTileWithThePhotoBottomLeftAndTheCountBeside()
    {
        var id = Guid.NewGuid();
        var tile = MediaPersonGroupTileComposer.Compose(
            id, "Evanescence", id, "/persons/evanescence/headshot", ["Artist"], "Music", 90, 2003, 2021, DateTimeOffset.UtcNow,
            Covers("/a/1.jpg", "/a/2.jpg", "/a/3.jpg", "/a/4.jpg"),
            MediaPersonGroupTileComposer.NavigationUrl(id, "Evanescence"), "#000", albumCount: 11, songCount: 90);

        var cut = Render<MediaTile>(p => p.Add(c => c.Item, tile));

        Assert.Contains("is-square", cut.Find("article.media-tile").ClassList);
        Assert.Equal(4, cut.FindAll("img.media-tile-quadrant").Count);
        Assert.Contains("headshot", cut.Find(".media-tile-person-photo img").GetAttribute("src"), StringComparison.Ordinal);
        Assert.Equal("11 albums", cut.Find(".media-tile-label--beside-photo").TextContent.Trim());
        Assert.Equal("Evanescence", cut.Find(".media-tile-name--beside-photo").TextContent.Trim());
        Assert.Empty(cut.FindAll("a.person-tile"));
        Assert.Empty(cut.FindAll(".media-tile-label--kind"));
        Assert.Equal("Evanescence, 11 albums, 90 songs", cut.Find("a.media-tile-link").GetAttribute("aria-label"));
    }

    [Fact]
    public void ADirectorWithFilms_IsThePosterQuadrantTile()
    {
        var id = Guid.NewGuid();
        var tile = MediaPersonGroupTileComposer.Compose(
            id, "Ava DuVernay", id, "/persons/ava/headshot", ["Director"], "Movies", 4, 2012, 2023, DateTimeOffset.UtcNow,
            Covers("/m/1.jpg", "/m/2.jpg", "/m/3.jpg", "/m/4.jpg"),
            MediaPersonGroupTileComposer.NavigationUrl(id, "Ava DuVernay"), "#000");

        var cut = Render<MediaTile>(p => p.Add(c => c.Item, tile));

        Assert.Contains("is-portrait", cut.Find("article.media-tile").ClassList);
        Assert.Equal(4, cut.FindAll("img.media-tile-quadrant").Count);
        Assert.Equal("4 movies", cut.Find(".media-tile-label--beside-photo").TextContent.Trim());
    }

    [Fact]
    public void APersonWithWorksButNoPhoto_ShowsThePersonIconInTheCorner()
    {
        var id = Guid.NewGuid();
        var tile = MediaPersonGroupTileComposer.Compose(
            id, "Ava DuVernay", id, null, ["Director"], "Movies", 2, 2012, 2023, DateTimeOffset.UtcNow,
            Covers("/m/1.jpg", "/m/2.jpg"), MediaPersonGroupTileComposer.NavigationUrl(id, "Ava DuVernay"), "#000");

        var cut = Render<MediaTile>(p => p.Add(c => c.Item, tile));

        Assert.Empty(cut.FindAll(".media-tile-person-photo img"));
        Assert.Single(cut.FindAll(".media-tile-person-photo"));
    }

    [Fact]
    public void ACastMemberOrAPersonWithNoWorks_IsASquarePhotoWithTheNameOnTheArt()
    {
        var id = Guid.NewGuid();
        var cast = new MediaTileViewModel
        {
            Id = id,
            Title = "Jane Actor",
            NavigationUrl = $"/details/person/{id:D}",
            Person = new MediaTilePersonViewModel { Id = id, Name = "Jane Actor", ImageUrl = "/persons/jane/headshot", Roles = ["Actor"] },
        };

        var cut = Render<MediaTile>(p => p.Add(c => c.Item, cast));

        Assert.False(cast.RenderAsPersonQuadrants);
        Assert.True(cast.RenderAsPersonPhoto);
        Assert.Equal("Jane Actor", cut.Find(".person-tile__name").TextContent.Trim());
        Assert.Empty(cut.FindAll(".media-tile-quadrants"));
        Assert.Contains("headshot", cut.Find(".person-tile__photo img").GetAttribute("src"), StringComparison.Ordinal);
    }

    [Fact]
    public void APersonWithNoPhoto_IsAPlainSquareWithTheIcon()
    {
        var id = Guid.NewGuid();
        var cast = new MediaTileViewModel
        {
            Id = id,
            Title = "Jane Actor",
            NavigationUrl = $"/details/person/{id:D}",
            Person = new MediaTilePersonViewModel { Id = id, Name = "Jane Actor" },
        };

        var cut = Render<PersonTile>(p => p.Add(c => c.Item, cast));

        Assert.Empty(cut.FindAll(".person-tile__photo img"));
        Assert.Single(cut.FindAll(".person-tile__icon"));
    }

    [Fact]
    public void GroupTileShape_IsLandscapeForAGroupOfTitles_AndSquareOnlyForAPersonWhoseEveryShownCoverIsSquare()
    {
        Assert.Equal(MediaTileShape.Landscape, GroupTileShape.For(Covers(ArtworkShape.Square, "/a/1.jpg", "/a/2.jpg")));
        Assert.Equal(MediaTileShape.Landscape, GroupTileShape.For(Covers(ArtworkShape.Portrait, "/p/1.jpg", "/p/2.jpg")));
        Assert.Equal(MediaTileShape.Landscape, GroupTileShape.For([]));

        Assert.Equal(MediaTileShape.Square, GroupTileShape.ForPerson(Covers(ArtworkShape.Square, "/a/1.jpg", "/a/2.jpg")));
        Assert.Equal(MediaTileShape.Portrait, GroupTileShape.ForPerson(Covers(ArtworkShape.Portrait, "/p/1.jpg", "/p/2.jpg")));
        Assert.Equal(MediaTileShape.Portrait, GroupTileShape.ForPerson([
            new ArtworkStackItem { ImageUrl = "/a/1.jpg", Shape = ArtworkShape.Square },
            new ArtworkStackItem { ImageUrl = "/p/1.jpg", Shape = ArtworkShape.Portrait },
        ]));
        Assert.Equal(MediaTileShape.Portrait, GroupTileShape.ForPerson([]));
    }

    [Fact]
    public void AnAudiobookAuthor_IsAPosterQuadrantTile_NotASquare()
    {
        var id = Guid.NewGuid();
        var tile = MediaPersonGroupTileComposer.Compose(
            id, "Frank Herbert", id, null, ["Author"], "Audiobooks", 3, 1965, 1981, DateTimeOffset.UtcNow,
            Covers("/b/1.jpg", "/b/2.jpg", "/b/3.jpg"), MediaPersonGroupTileComposer.NavigationUrl(id, "Frank Herbert"), "#000");

        Assert.Equal(MediaTileShape.Portrait, tile.Shape);
        Assert.Equal(MediaTileSurfaceKind.CoverPortrait, tile.SurfaceKind);
    }

    [Fact]
    public void AnArtistShelfFromAContributor_IsASquareOfAlbums_WithTheCountAsWorks()
    {
        var shelf = new ContributorShelfDto
        {
            Key = "artist:evanescence",
            PersonId = Guid.NewGuid(),
            PersonName = "Evanescence",
            HeadshotUrl = "/persons/evanescence/headshot",
            Role = "Artist",
            Lane = "Listen",
            Title = "Evanescence",
            OwnedCount = 3,
            Items =
            [
                new ContributorShelfItemDto { WorkId = Guid.NewGuid(), Title = "Fallen", MediaType = "Music", CoverUrl = "/a/1.jpg" },
                new ContributorShelfItemDto { WorkId = Guid.NewGuid(), Title = "The Open Door", MediaType = "Music", CoverUrl = "/a/2.jpg" },
            ],
        };

        var tile = CollectionSurfaceTileComposer.FromContributorShelf(shelf);

        Assert.Equal(MediaTileShape.Square, tile.Shape);
        Assert.True(tile.RenderAsPersonQuadrants);
        Assert.Equal("3 works", MediaTileLabels.Bottom(tile, "Kept"));
    }

    [Fact]
    public void ACollectionOfAlbumsIsTheLandscapeCollage_AndItsChosenCoverIsCarriedAsTheCustomCover()
    {
        var collection = new CollectionManagementCatalogViewModel
        {
            Id = Guid.NewGuid(),
            Name = "Listening Room",
            ItemCount = 2,
            CoverArtworkUrl = "/stream/artwork/chosen",
            ArtworkItems =
            [
                new CollectionArtworkItemDto { WorkId = Guid.NewGuid(), Title = "One", MediaType = "Music", CoverUrl = "/stream/artwork/00000000-0000-0000-0000-000000000001", ArtworkShape = "square" },
                new CollectionArtworkItemDto { WorkId = Guid.NewGuid(), Title = "Two", MediaType = "Music", CoverUrl = "/stream/artwork/00000000-0000-0000-0000-000000000002", ArtworkShape = "square" },
            ],
        };
        var automatic = new CollectionManagementCatalogViewModel
        {
            Id = Guid.NewGuid(),
            Name = "Listening Room",
            ItemCount = 2,
            ArtworkItems = collection.ArtworkItems,
        };

        var chosen = CollectionSurfaceTileComposer.FromCollection(collection);
        var stacked = CollectionSurfaceTileComposer.FromCollection(automatic);

        Assert.Equal(MediaTileShape.Landscape, chosen.Shape);
        Assert.Equal("/stream/artwork/chosen", chosen.CustomCoverUrl);
        Assert.Null(stacked.CustomCoverUrl);
        Assert.Equal(2, stacked.GroupCovers.Count);
    }

    private static MediaTileViewModel Group(
        IReadOnlyList<ArtworkStackItem> covers,
        MediaTileShape shape = MediaTileShape.Portrait,
        MediaTileGroupKind kind = MediaTileGroupKind.Collection,
        string? customCover = null,
        string? newLabel = null,
        string? mediaKind = null,
        string? tileImage = null) => new()
        {
            Id = Guid.NewGuid(),
            CollectionId = Guid.NewGuid(),
            Title = "Group",
            IsCollection = true,
            UseGroupTile = true,
            GroupKind = kind,
            Shape = shape,
            NewLabel = newLabel,
            CustomCoverUrl = customCover,
            MediaKind = mediaKind ?? string.Empty,
            TileImageUrl = tileImage,
            ArtworkStackItems = covers,
            NavigationUrl = "/details/collection/group",
            PreviewTotalCount = covers.Count,
        };

    private static IReadOnlyList<ArtworkStackItem> Covers(params string[] urls) => Covers(ArtworkShape.Portrait, urls);

    private static IReadOnlyList<ArtworkStackItem> Covers(ArtworkShape shape, params string[] urls) =>
        urls.Select((url, index) => new ArtworkStackItem { Id = index.ToString(), Title = $"Item {index}", ImageUrl = url, MediaType = "Book", Shape = shape }).ToList();
}
