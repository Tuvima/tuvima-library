using Bunit;
using MediaEngine.Contracts.Collections;
using MediaEngine.Web.Components.MediaTiles;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.MediaTiles;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>
/// The landscape cover collage that draws every group of titles (issue 1175; also 1160, 1173, 1174): all the widths' plans drawn in one
/// tile with a container query picking one, each cover in its own shape, two titles in opposite corners, and the name on a solid band.
/// </summary>
public sealed class GroupCollageTileTests : AsyncBunitContext
{
    public GroupCollageTileTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddNativeUiServices();
    }

    [Fact]
    public void AGroupOfTitles_IsTheLandscapeTile_WhateverItHolds()
    {
        var posters = Render<MediaTile>(p => p.Add(c => c.Item, Group(Members(ArtworkShape.Portrait, 4))));
        var albums = Render<MediaTile>(p => p.Add(c => c.Item, Group(Members(ArtworkShape.Square, 4))));
        var plain = Render<MediaTile>(p => p.Add(c => c.Item, Group([])));

        Assert.Contains("is-landscape", posters.Find("article.media-tile").ClassList);
        Assert.Contains("is-landscape", albums.Find("article.media-tile").ClassList);
        Assert.Contains("is-landscape", plain.Find("article.media-tile").ClassList);
        Assert.Empty(posters.FindAll(".media-tile-quadrants"));
    }

    [Fact]
    public void ATenTitleGroup_DrawsSixCovers_AndOnePlusChipForEachWidthTier()
    {
        var group = Group(Members(ArtworkShape.Portrait, 6), total: 10);

        var cut = Render<MediaTile>(p => p.Add(c => c.Item, group));

        Assert.Equal(6, cut.FindAll(".group-collage__cover img").Count);
        var chips = cut.FindAll(".group-collage__more").ToDictionary(chip => chip.GetAttribute("data-tier")!, chip => chip.TextContent.Trim());
        Assert.Equal("+7", chips["3"]);
        Assert.Equal("+6", chips["4"]);
        Assert.Equal("+4", chips["6"]);
        // The covers past the third and fourth are the ones a narrower tile hides.
        Assert.Single(cut.FindAll(".group-collage__cover.is-from-medium"));
        Assert.Equal(2, cut.FindAll(".group-collage__cover.is-from-wide").Count);
    }

    [Fact]
    public void ThePlanForEveryWidthIsHandedToTheStylesheet_AsCountAndAspectSum()
    {
        var shapes = new[] { ArtworkShape.Portrait, ArtworkShape.Square, ArtworkShape.Wide, ArtworkShape.Portrait, ArtworkShape.Square, ArtworkShape.Wide };
        var members = shapes.Select((shape, index) => Member(index, shape)).ToList();

        var cut = Render<MediaTile>(p => p.Add(c => c.Item, Group(members, total: 9)));

        var style = cut.Find(".group-collage").GetAttribute("style")!;
        Assert.Contains("--n3:3;--sa3:3.4444;", style, StringComparison.Ordinal);
        Assert.Contains("--n4:4;--sa4:4.1111;", style, StringComparison.Ordinal);
        Assert.Contains("--n6:6;--sa6:6.8889;", style, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryCoverCarriesItsOwnShape_SoABookAnAlbumAndAFilmAreNotMadeTheSame()
    {
        var members = new List<ArtworkStackItem>
        {
            Member(0, ArtworkShape.Portrait),
            Member(1, ArtworkShape.Square),
            Member(2, ArtworkShape.Wide),
        };

        var cut = Render<MediaTile>(p => p.Add(c => c.Item, Group(members)));

        var covers = cut.FindAll(".group-collage__cover");
        Assert.Equal(["portrait", "square", "wide"], covers.Select(cover => cover.GetAttribute("data-shape")));
        Assert.Contains("--a:0.6667;", covers[0].GetAttribute("style"), StringComparison.Ordinal);
        Assert.Contains("--a:1;", covers[1].GetAttribute("style"), StringComparison.Ordinal);
        Assert.Contains("--a:1.7778;", covers[2].GetAttribute("style"), StringComparison.Ordinal);
    }

    [Fact]
    public void TwoTitles_SitInOppositeCorners_TheFirstTopLeftAndTheSecondBottomRight()
    {
        var cut = Render<MediaTile>(p => p.Add(c => c.Item, Group(Members(ArtworkShape.Portrait, 2))));

        Assert.Equal("corners", cut.Find(".group-collage").GetAttribute("data-arrangement"));
        var covers = cut.FindAll(".group-collage__cover");
        Assert.Contains("is-corner-first", covers[0].ClassList);
        Assert.Contains("is-corner-second", covers[1].ClassList);
        Assert.Contains("--pair:1.2533;", cut.Find(".group-collage").GetAttribute("style"), StringComparison.Ordinal);
        Assert.Empty(cut.FindAll(".group-collage__more"));
    }

    [Fact]
    public void TheNameAndCountSitOnASolidBand_NotOnAScrimOverTheCovers()
    {
        var group = Group(Members(ArtworkShape.Portrait, 4), total: 10);

        var cut = Render<MediaTile>(p => p.Add(c => c.Item, group));

        Assert.Equal("Group", cut.Find(".group-collage__band .group-collage__name").TextContent.Trim());
        Assert.Equal("10 items", cut.Find(".group-collage__band .group-collage__count").TextContent.Trim());
        Assert.Empty(cut.FindAll(".media-tile-scrim"));
        Assert.Empty(cut.FindAll(".media-tile-name"));
        // The count lives in the band, not in a second label over the covers.
        Assert.Empty(cut.FindAll(".media-tile-label--bottom"));
    }

    [Fact]
    public void TheLinkStillNamesTheGroupAndItsKind_AndTheCoversAreDecorative()
    {
        var cut = Render<MediaTile>(p => p.Add(c => c.Item, Group(Members(ArtworkShape.Portrait, 4), kind: MediaTileGroupKind.Series)));

        Assert.Equal("Group, Series", cut.Find("a.media-tile-link").GetAttribute("aria-label"));
        Assert.Equal("true", cut.Find(".group-collage").GetAttribute("aria-hidden"));
        Assert.All(cut.FindAll(".group-collage img"), image => Assert.Equal(string.Empty, image.GetAttribute("alt")));
    }

    [Fact]
    public void AChosenCover_IsShownWholeInPlaceOfTheMemberCovers_AndTheNameBandStays()
    {
        var group = Group(Members(ArtworkShape.Portrait, 4), customCover: "/art/chosen.jpg");

        var cut = Render<MediaTile>(p => p.Add(c => c.Item, group));

        Assert.Contains("chosen.jpg", cut.Find("img.group-collage__custom").GetAttribute("src"), StringComparison.Ordinal);
        Assert.Empty(cut.FindAll(".group-collage__cover"));
        Assert.Empty(cut.FindAll(".group-collage__more"));
        Assert.Equal("Group", cut.Find(".group-collage__name").TextContent.Trim());
    }

    [Fact]
    public void APersonWithWorks_KeepsTheQuadrantTile_NotTheCollage()
    {
        var id = Guid.NewGuid();
        var tile = MediaPersonGroupTileComposer.Compose(
            id, "Ava DuVernay", id, "/persons/ava/headshot", ["Director"], "Movies", 4, 2012, 2023, DateTimeOffset.UtcNow,
            Members(ArtworkShape.Portrait, 4), MediaPersonGroupTileComposer.NavigationUrl(id, "Ava DuVernay"), "#000");

        var cut = Render<MediaTile>(p => p.Add(c => c.Item, tile));

        Assert.Empty(cut.FindAll(".group-collage"));
        Assert.Equal(4, cut.FindAll("img.media-tile-quadrant").Count);
        Assert.Contains("is-portrait", cut.Find("article.media-tile").ClassList);
    }

    [Fact]
    public void ASeriesTileTakesEachMembersRealShapeFromTheEngine_AndKeepsUpToSixCovers()
    {
        var shapes = new[] { "portrait", "square", "wide", "portrait", "square", "wide", "portrait", "portrait" };
        var tile = CollectionSurfaceTileComposer.FromContentGroup(new ContentGroupViewModel
        {
            CollectionId = Guid.NewGuid(),
            DisplayName = "Dune (everything)",
            PrimaryMediaType = "Movies",
            WorkCount = 8,
            DistinctTitleCount = 8,
            PreviewItems = shapes
                .Select((shape, index) => new ContentGroupPreviewItemDto(Guid.NewGuid(), $"Title {index}", $"/art/{index}.jpg", shape, (index + 1).ToString()))
                .ToList(),
        });

        Assert.Equal(MediaTileShape.Landscape, tile.Shape);
        Assert.Equal(6, tile.ArtworkStackItems.Count);
        Assert.Equal(
            [ArtworkShape.Portrait, ArtworkShape.Square, ArtworkShape.Wide, ArtworkShape.Portrait, ArtworkShape.Square, ArtworkShape.Wide],
            tile.ArtworkStackItems.Select(item => item.Shape));
        Assert.Equal(8, MediaTileLabels.GroupTotal(tile));
    }

    private static MediaTileViewModel Group(
        IReadOnlyList<ArtworkStackItem> members,
        int? total = null,
        MediaTileGroupKind kind = MediaTileGroupKind.Collection,
        string? customCover = null) => new()
        {
            Id = Guid.NewGuid(),
            CollectionId = Guid.NewGuid(),
            Title = "Group",
            IsCollection = true,
            UseGroupTile = true,
            GroupKind = kind,
            Shape = MediaTileShape.Landscape,
            CustomCoverUrl = customCover,
            ArtworkStackItems = members,
            NavigationUrl = "/details/collection/group",
            PreviewTotalCount = total ?? members.Count,
        };

    private static IReadOnlyList<ArtworkStackItem> Members(ArtworkShape shape, int count) =>
        Enumerable.Range(0, count).Select(index => Member(index, shape)).ToList();

    private static ArtworkStackItem Member(int index, ArtworkShape shape) => new()
    {
        Id = index.ToString(),
        Title = $"Item {index}",
        ImageUrl = $"/c/{index + 1}.jpg",
        MediaType = "Book",
        Shape = shape,
    };
}
