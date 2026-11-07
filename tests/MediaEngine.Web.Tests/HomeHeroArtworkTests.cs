using Bunit;
using MediaEngine.Contracts.Details;
using MediaEngine.Contracts.Display;
using MediaEngine.Web.Components.Cinematic;
using MediaEngine.Web.Components.Details;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Playback;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>Home hero artwork per kind (issue 709): big covers for books, comics and audiobooks, the extracted frame as the backdrop for Other videos, films unchanged.</summary>
public sealed class HomeHeroArtworkTests : AsyncBunitContext
{
    private readonly DeviceContextService _device;

    public HomeHeroArtworkTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        Services.AddNativeUiServices();
        var api = EngineApiClientStub.CreateDefault();
        Services.AddSingleton(api);
        Services.AddSingleton(new PlaybackSessionController(null!, api));
        _device = new DeviceContextService(api);
        Services.AddSingleton(_device);
    }

    private Task UsePhoneAsync() => _device.InitialiseAsync(new DeviceContextSnapshot("mobile", "phone", true));

    private static DiscoveryHeroViewModel Book(string kind = "Book", int? coverWidth = 1691) => new()
    {
        WorkId = Guid.NewGuid(),
        Title = "Leviathan Wakes",
        MediaKind = kind,
        PreviewImageUrl = "/engine-image/stream/artwork/book-cover",
        CoverWidthPx = coverWidth,
        CoverHeightPx = 2500,
        SurfaceKind = MediaTileSurfaceKind.CoverPortrait,
        PrimaryActionLabel = "Read",
        PrimaryNavigationUrl = "/read/a",
    };

    private static DiscoveryHeroViewModel VideoWithFrame(bool composed) => new()
    {
        WorkId = Guid.NewGuid(),
        Title = "Sample Talk",
        MediaKind = "Videos",
        // The composer puts the frame in the background slot; a hero built another way only has it as the picture.
        BackgroundImageUrl = composed ? "/engine-image/stream/artwork/frame" : null,
        HeroBackgroundImageUrl = composed ? "/engine-image/stream/artwork/frame" : null,
        PreviewImageUrl = "/engine-image/stream/artwork/frame",
        CoverWidthPx = 1280,
        CoverHeightPx = 720,
        SurfaceKind = composed ? MediaTileSurfaceKind.BannerLandscape : MediaTileSurfaceKind.CoverPortrait,
        PrimaryActionLabel = "Play",
        PrimaryNavigationUrl = "/watch/player/x",
    };

    private static DiscoveryHeroViewModel Movie(bool withBackdrop) => new()
    {
        WorkId = Guid.NewGuid(),
        Title = "Dune: Part Two",
        MediaKind = "Movie",
        BackgroundImageUrl = withBackdrop ? "/engine-image/stream/artwork/backdrop" : null,
        HeroBackgroundImageUrl = withBackdrop ? "/engine-image/stream/artwork/backdrop" : null,
        BackgroundWidthPx = withBackdrop ? 1920 : null,
        BackgroundHeightPx = withBackdrop ? 1080 : null,
        PreviewImageUrl = "/engine-image/stream/artwork/poster",
        CoverWidthPx = 1000,
        CoverHeightPx = 1500,
        SurfaceKind = withBackdrop ? MediaTileSurfaceKind.BannerLandscape : MediaTileSurfaceKind.CoverPortrait,
        PrimaryActionLabel = "Play",
        PrimaryNavigationUrl = "/watch/player/y",
    };

    private IRenderedComponent<CinematicHeroCarousel> RenderHero(DiscoveryHeroViewModel hero) =>
        Render<CinematicHeroCarousel>(p => p.Add(c => c.Items, [hero]));

    [Theory]
    [InlineData("Book")]
    [InlineData("Comic")]
    [InlineData("Audiobook")]
    public void ABookComicOrAudiobookHeroShowsALargeCoverOverABlurredCopyOfIt(string kind)
    {
        var cut = RenderHero(Book(kind));

        Assert.Contains("cinematic-hero--cover-led", cut.Find(".cinematic-hero-surface").ClassName);
        Assert.Contains("cinematic-hero-carousel--cover-led", cut.Find(".cinematic-hero-carousel").ClassName);
        var cover = cut.Find("img.tl-detail-media-stage__cover");
        Assert.Contains("/engine-image/stream/artwork/book-cover", cover.GetAttribute("src"));
        var blurred = cut.Find(".tl-detail-media-stage__cover-atmosphere");
        Assert.NotNull(blurred);
        Assert.Contains("book-cover", cut.Find(".tl-detail-media-stage").GetAttribute("style"));
        // No landscape backdrop picture and no empty placeholder stage: the cover is the picture.
        Assert.Empty(cut.FindAll(".tl-detail-media-stage__background"));
        Assert.Empty(cut.FindAll(".tl-detail-media-stage__placeholder"));
    }

    [Fact]
    public void AnAudiobookCoverIsDrawnSquareAndABookCoverPortrait()
    {
        Assert.Contains("cinematic-hero--cover-led-square", RenderHero(Book("Audiobook")).Find(".cinematic-hero-surface").ClassName);
        Assert.DoesNotContain("cinematic-hero--cover-led-square", RenderHero(Book("Book")).Find(".cinematic-hero-surface").ClassName);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AnOtherVideoWithAFrameDrawsTheFrameAsTheBackdropAndNoPortraitCoverBox(bool composed)
    {
        var cut = RenderHero(VideoWithFrame(composed));

        var backdrop = cut.Find("img.tl-detail-media-stage__background");
        Assert.Contains("/engine-image/stream/artwork/frame", backdrop.GetAttribute("src"));
        Assert.Empty(cut.FindAll(".tl-detail-media-stage__cover-wrap"));
        Assert.Empty(cut.FindAll(".tl-detail-media-stage__cover"));
        Assert.Empty(cut.FindAll(".tl-detail-media-stage__cover-atmosphere"));
        Assert.DoesNotContain("cinematic-hero--cover-led", cut.Find(".cinematic-hero-surface").ClassName);
    }

    [Fact]
    public void AStandInPictureOnAVideoIsNotStretchedIntoABackdrop()
    {
        var hero = VideoWithFrame(composed: false);
        var standIn = new DiscoveryHeroViewModel
        {
            WorkId = hero.WorkId,
            Title = hero.Title,
            MediaKind = hero.MediaKind,
            PreviewImageUrl = hero.PreviewImageUrl,
            CoverWidthPx = 100,
            CoverHeightPx = 150,
            SurfaceKind = MediaTileSurfaceKind.CoverPortrait,
        };

        var cut = RenderHero(standIn);

        Assert.Empty(cut.FindAll("img.tl-detail-media-stage__background"));
    }

    [Fact]
    public void AMovieHeroIsUnchanged_BackdropWhenItHasOneAndThePosterCoverWithoutTheBookTreatmentWhenItDoesNot()
    {
        var withBackdrop = RenderHero(Movie(withBackdrop: true));
        Assert.Contains("/engine-image/stream/artwork/backdrop", withBackdrop.Find("img.tl-detail-media-stage__background").GetAttribute("src"));
        Assert.Empty(withBackdrop.FindAll(".tl-detail-media-stage__cover-wrap"));
        Assert.DoesNotContain("cinematic-hero--cover-led", withBackdrop.Find(".cinematic-hero-surface").ClassName);

        var posterOnly = RenderHero(Movie(withBackdrop: false));
        Assert.DoesNotContain("cinematic-hero--cover-led", posterOnly.Find(".cinematic-hero-surface").ClassName);
        Assert.DoesNotContain("tl-detail-media-stage__book-pages", posterOnly.Markup);
    }

    [Fact]
    public void AnOtherVideoDetailPageDrawsItsFrameAsTheBackdropWithNoPortraitCoverBox()
    {
        // The Engine sends a video's extracted frame as the page's backdrop (and as its only cover); the page draws it like a film's backdrop.
        var art = new ArtworkSet
        {
            BackdropUrl = "/engine-image/stream/artwork/frame",
            CoverUrl = "/engine-image/stream/artwork/frame",
            HeroArtwork = new HeroArtworkViewModel
            {
                HasImage = true,
                Url = "/engine-image/stream/artwork/frame",
                Mode = HeroArtworkMode.BackdropWithRenderedTitle,
            },
        };

        var detail = Render<HeroBackdrop>(p => p.Add(c => c.Artwork, art).Add(c => c.EntityType, DetailEntityType.Movie));

        Assert.Contains("/engine-image/stream/artwork/frame", detail.Find("img.tl-detail-media-stage__background").GetAttribute("src"));
        Assert.Empty(detail.FindAll(".tl-detail-media-stage__cover-wrap"));
        Assert.Empty(detail.FindAll(".tl-detail-media-stage__cover"));
    }

    [Fact]
    public async Task OnAPhoneABookCoverIsLargeOverABlurredCopyAndAVideoFrameIsTheBackdrop()
    {
        await UsePhoneAsync();

        var book = RenderHero(Book());
        Assert.Equal(book.Find(".phone-hero__cover").GetAttribute("src"), book.Find(".phone-hero__cover-blur").GetAttribute("src"));
        Assert.Equal("phone-hero__card phone-hero__card--cover", book.Find(".phone-hero__card").ClassName);

        var video = RenderHero(VideoWithFrame(composed: false));
        Assert.Contains("/engine-image/stream/artwork/frame", video.Find(".phone-hero__backdrop").GetAttribute("src"));
        Assert.Empty(video.FindAll(".phone-hero__cover"));
    }

    [Fact]
    public void ThePhoneArtLadderTreatsAVideoFrameAsABackdropButAStandInAsAPlaceholder()
    {
        Assert.Equal(PhoneHeroArt.Backdrop, PhoneHeroPresentation.ResolveArt(VideoWithFrame(composed: false)).Mode);
        var standIn = new DiscoveryHeroViewModel { MediaKind = "Videos", PreviewImageUrl = "/x", CoverWidthPx = 100, CoverHeightPx = 150 };
        Assert.Equal(PhoneHeroArt.Placeholder, PhoneHeroPresentation.ResolveArt(standIn).Mode);
        Assert.Equal(PhoneHeroArt.Cover, PhoneHeroPresentation.ResolveArt(Book()).Mode);
    }

    [Fact]
    public void TheCoverLedStylesSizeTheCoverFromTheHeroAndStackItAboveTheTextOnNarrowStages()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var cinematic = Path.Combine(root, "src", "MediaEngine.Web", "Components", "Cinematic");
        var backdropCss = File.ReadAllText(Path.Combine(root, "src", "MediaEngine.Web", "Components", "Details", "HeroBackdrop.razor.css"));
        var carouselCss = File.ReadAllText(Path.Combine(cinematic, "CinematicHeroCarousel.razor.css"));
        var phoneCss = File.ReadAllText(Path.Combine(cinematic, "PhoneHeroCard.razor.css"));

        Assert.Contains("cinematic-hero--cover-led .tl-detail-media-stage .tl-detail-media-stage__cover-wrap", backdropCss);
        Assert.Contains("--lc-narrow-h", carouselCss);
        Assert.Contains("--lc-content-top", carouselCss);
        Assert.Contains(".phone-hero__cover-blur", phoneCss);
    }
}
