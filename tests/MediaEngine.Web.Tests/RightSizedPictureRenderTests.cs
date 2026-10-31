using Bunit;
using MediaEngine.Contracts.Details;
using MediaEngine.Contracts.Persons;
using MediaEngine.Web.Components.Browse;
using MediaEngine.Web.Components.Collections;
using MediaEngine.Web.Components.Details;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.MediaTiles;

namespace MediaEngine.Web.Tests;

/// <summary>
/// Right-sized pictures (spec D1/D2): a picture drawn at a fixed size asks the Engine for the smallest
/// copy at least that size, and offers the next copy up to sharper screens.
/// </summary>
public sealed class RightSizedPictureRenderTests : LocalizedBunitContext
{
    private const string Face = "/engine-image/persons/6f6b4d6e-0000-4000-8000-000000000001/headshot?v=abc123";

    [Fact]
    public void PeopleList_AsksForTheTinyCopyOfEachFaceAndLoadsThemLazily()
    {
        var person = new PersonListItemResponse(Guid.NewGuid(), "Frank Herbert", ["Author"], null, Face, true, false, false, null, null);

        var cut = Render<PeopleCatalogList>(parameters => parameters.Add(component => component.Items, [person]));

        var image = cut.Find(".people-catalog-row__portrait img");
        Assert.Equal($"{Face.Split('?')[0]}?v=abc123&size=t", image.GetAttribute("src"));
        Assert.Equal("lazy", image.GetAttribute("loading"));
        Assert.Equal("async", image.GetAttribute("decoding"));
        var srcset = image.GetAttribute("srcset");
        Assert.NotNull(srcset);
        Assert.Contains("size=t", srcset);
        Assert.Contains("size=s", srcset);
        Assert.EndsWith("3x", srcset);
    }

    [Fact]
    public void PeopleList_LeavesAFaceFromAProviderLinkUnchanged()
    {
        const string External = "https://upload.wikimedia.example/face.jpg";
        var person = new PersonListItemResponse(Guid.NewGuid(), "Someone", [], null, External, false, false, false, null, null);

        var cut = Render<PeopleCatalogList>(parameters => parameters.Add(component => component.Items, [person]));

        var image = cut.Find(".people-catalog-row__portrait img");
        Assert.Equal(External, image.GetAttribute("src"));
        Assert.Null(image.GetAttribute("srcset"));
    }

    [Theory]
    [InlineData("md", "size=s")]
    [InlineData("sm", "size=t")]
    public void PersonAvatar_PicksTheCopyForItsSize(string size, string expected)
    {
        var cut = Render<PersonAvatar>(parameters => parameters
            .Add(component => component.Name, "Frank Herbert")
            .Add(component => component.ImageUrl, Face)
            .Add(component => component.Size, size));

        Assert.Contains(expected, cut.Find("img").GetAttribute("src"));
        Assert.Contains("2x", cut.Find("img").GetAttribute("srcset"));
    }

    [Fact]
    public void PersonAvatar_ACallerThatDrawsItBiggerAsksForABiggerCopy()
    {
        var cut = Render<PersonAvatar>(parameters => parameters
            .Add(component => component.Name, "Frank Herbert")
            .Add(component => component.ImageUrl, Face)
            .Add(component => component.DrawnPx, 400));

        Assert.Contains("size=m", cut.Find("img").GetAttribute("src"));
    }

    [Theory]
    [InlineData(AppControlSize.Compact)]
    [InlineData(AppControlSize.Normal)]
    [InlineData(AppControlSize.Large)]
    public void AppAvatar_EverySizeAsksForTheTinyCopyAndOffersASmallOneToSharperScreens(AppControlSize size)
    {
        var cut = Render<AppAvatar>(parameters => parameters
            .Add(component => component.ImageUrl, Face)
            .Add(component => component.Size, size));

        var image = cut.Find("img");
        Assert.Contains("size=t", image.GetAttribute("src"));
        Assert.Contains("size=s", image.GetAttribute("srcset"));
    }

    [Fact]
    public void AppMediaArtwork_ReadsItsDrawnSizeFromSizesUnlessTheCallerSetsIt()
    {
        const string Cover = "/engine-image/stream/artwork/6f6b4d6e-0000-4000-8000-000000000002?v=k1";

        var row = Render<AppMediaArtwork>(parameters => parameters.Add(component => component.ImageUrl, Cover));
        var rail = Render<AppMediaArtwork>(parameters => parameters
            .Add(component => component.ImageUrl, Cover)
            .Add(component => component.Sizes, "148px"));
        var drawer = Render<AppMediaArtwork>(parameters => parameters
            .Add(component => component.ImageUrl, Cover)
            .Add(component => component.DrawnPx, 180));

        Assert.Contains("size=t", row.Find("img").GetAttribute("src"));
        Assert.Contains("size=s", rail.Find("img").GetAttribute("src"));
        Assert.Contains("size=s", drawer.Find("img").GetAttribute("src"));
        Assert.Contains("v=k1", drawer.Find("img").GetAttribute("src"));
    }

    [Fact]
    public void ArtworkGroupPreview_DrawsEachCoverFromTheSmallCopyAndTheHeroClusterFromTheMediumOne()
    {
        const string Cover = "/engine-image/stream/artwork/6f6b4d6e-0000-4000-8000-000000000003";
        IReadOnlyList<ArtworkStackItem> items = [new ArtworkStackItem { Id = "one", ImageUrl = Cover, Title = "One" }];

        var tile = Render<MediaArtworkGroupPreview>(parameters => parameters.Add(component => component.Items, items));
        var hero = Render<MediaArtworkGroupPreview>(parameters => parameters
            .Add(component => component.Items, items)
            .Add(component => component.DrawnPx, 640));

        Assert.EndsWith("size=s", tile.Find("img").GetAttribute("src"));
        Assert.EndsWith("size=m", hero.Find("img").GetAttribute("src"));
    }

    [Fact]
    public void ScreenWidePictures_OfferTheSmallMediumAndLargeCopiesByLongSide()
    {
        var srcset = MediaTileArtworkUrl.SrcSetByLongSide("/engine-image/stream/artwork/6f6b4d6e-0000-4000-8000-000000000004?v=k2");

        Assert.Equal(
            "/engine-image/stream/artwork/6f6b4d6e-0000-4000-8000-000000000004?v=k2&size=s 320w, "
            + "/engine-image/stream/artwork/6f6b4d6e-0000-4000-8000-000000000004?v=k2&size=m 960w, "
            + "/engine-image/stream/artwork/6f6b4d6e-0000-4000-8000-000000000004?v=k2&size=l 2160w",
            srcset);
        Assert.Null(MediaTileArtworkUrl.SrcSetByLongSide("https://provider.example/backdrop.jpg"));
        Assert.Null(MediaTileArtworkUrl.SrcSetByLongSide(null));
    }
    [Fact]
    public void ContinueStrip_SizesEnginePicturesAndNeverShowsAProviderLink()
    {
        var engine = new MediaTileViewModel { Id = Guid.NewGuid(), Title = "Dune", TileImageUrl = "/engine-image/stream/artwork/6f6b4d6e-0000-4000-8000-000000000002?size=s&v=abc" };
        var provider = new MediaTileViewModel { Id = Guid.NewGuid(), Title = "Arrival", TileImageUrl = "https://image.tmdb.org/t/p/original/poster.jpg" };

        var cut = Render<CompactContinueStrip>(parameters => parameters
            .Add(component => component.Title, "Continue")
            .Add(component => component.Items, [engine, provider]));

        var image = Assert.Single(cut.FindAll("img[data-artwork-purpose='continue-thumbnail']"));
        Assert.Equal("/engine-image/stream/artwork/6f6b4d6e-0000-4000-8000-000000000002?v=abc&size=t", image.GetAttribute("src"));
        Assert.DoesNotContain("tmdb.org", cut.Markup, StringComparison.OrdinalIgnoreCase);
    }
}
