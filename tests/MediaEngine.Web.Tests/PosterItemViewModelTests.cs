using MediaEngine.Contracts.Display;
using MediaEngine.Web.Models.ViewDTOs;

namespace MediaEngine.Web.Tests;

public sealed class PosterItemViewModelTests
{
    [Fact]
    public void FromDisplayCard_MapsDisplayContractForUniverseCards()
    {
        var workId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var action = new DisplayActionDto("openWork", "Open", WorkId: workId, WebUrl: $"/details/work/{workId:D}");
        var card = new DisplayCardDto(
            Id: workId,
            WorkId: workId,
            AssetId: Guid.Parse("22222222-2222-2222-2222-222222222222"),
            CollectionId: null,
            MediaType: "Book",
            GroupingType: "work",
            Title: "Dune",
            Subtitle: "Frank Herbert",
            Facts: ["1965", "Science Fiction"],
            Artwork: new DisplayArtworkDto(
                CoverUrl: "/cover.jpg",
                CoverSmallUrl: "/cover-s.jpg",
                CoverMediumUrl: "/cover-m.jpg",
                CoverLargeUrl: "/cover-l.jpg",
                SquareUrl: null,
                SquareSmallUrl: null,
                SquareMediumUrl: null,
                SquareLargeUrl: null,
                BannerUrl: null,
                BannerSmallUrl: null,
                BannerMediumUrl: null,
                BannerLargeUrl: null,
                BackgroundUrl: null,
                BackgroundSmallUrl: null,
                BackgroundMediumUrl: null,
                BackgroundLargeUrl: null,
                LogoUrl: null,
                CoverWidthPx: 600,
                CoverHeightPx: 900,
                SquareWidthPx: null,
                SquareHeightPx: null,
                BannerWidthPx: null,
                BannerHeightPx: null,
                BackgroundWidthPx: null,
                BackgroundHeightPx: null,
                AccentColor: "#123456"),
            PreferredShape: "portrait",
            Presentation: "book",
            TileTextMode: "caption",
            PreviewPlacement: "bottom",
            Progress: new DisplayProgressDto(37, "37%", DateTimeOffset.Parse("2026-04-24T12:00:00Z"), action),
            Actions: [action],
            Flags: new DisplayCardFlagsDto(false, true, false, false),
            SortTimestamp: DateTimeOffset.UtcNow);

        var item = PosterItemViewModel.FromDisplayCard(card);

        Assert.Equal("Dune", item.Title);
        Assert.Equal("Frank Herbert", item.Subtitle);
        Assert.Equal("1965", item.Year);
        Assert.Equal("/cover.jpg", item.CoverUrl);
        Assert.Equal(37, item.Progress);
        Assert.Equal($"/details/work/{workId:D}", item.NavigationUrl);
        Assert.Equal("#123456", item.DominantHexColor);
        Assert.Equal(PosterSourceType.Work, item.SourceType);
    }
}
