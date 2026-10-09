using Bunit;
using MediaEngine.Contracts.Details;
using MediaEngine.Contracts.Display;
using MediaEngine.Web.Components.Cinematic;
using MediaEngine.Web.Components.Details;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Playback;
using MediaEngine.Web.Tests.Support;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

public sealed class CinematicHeroCarouselRenderTests : AsyncBunitContext
{
    public CinematicHeroCarouselRenderTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        Services.AddNativeUiServices();
        var api = EngineApiClientStub.CreateDefault();
        Services.AddSingleton(api);
        Services.AddSingleton(new PlaybackSessionController(null!, api));
    }

    [Fact]
    public void ControlsWrapAndPositionSelectionUpdatesIdentity()
    {
        var cut = Render<CinematicHeroCarousel>(p => p.Add(c => c.Items, [Hero("First"), Hero("Second"), Hero("Third")]));
        cut.Find("[aria-label='Previous featured item']").Click();
        Assert.Equal("Third", cut.Find("h1").TextContent);
        cut.Find("[aria-label='Next featured item']").Click();
        Assert.Equal("First", cut.Find("h1").TextContent);
        cut.FindAll(".cinematic-hero-carousel__timeline-item")[1].Click();
        Assert.Equal("Second", cut.Find("h1").TextContent);
        Assert.Equal("true", cut.FindAll(".cinematic-hero-carousel__timeline-item")[1].GetAttribute("aria-current"));
    }

    [Fact]
    public void RefreshKeepsSelectedHeroWhenTheOrderingChanges()
    {
        var first = new DiscoveryHeroViewModel { WorkId = Guid.NewGuid(), Title = "First", MediaKind = "Movie" };
        var second = new DiscoveryHeroViewModel { WorkId = Guid.NewGuid(), Title = "Second", MediaKind = "Movie" };
        var cut = Render<CinematicHeroCarousel>(p => p.Add(c => c.Items, [first, second]));
        cut.Find("[aria-label='Next featured item']").Click();
        cut.Render(p => p.Add(c => c.Items, [second, first]));
        Assert.Equal("Second", cut.Find("h1").TextContent);
        Assert.Equal("true", cut.FindAll(".cinematic-hero-carousel__timeline-item")[0].GetAttribute("aria-current"));
    }

    [Theory]
    [InlineData(DisplayContinuationState.Unstarted, false)]
    [InlineData(DisplayContinuationState.InProgress, true)]
    [InlineData(DisplayContinuationState.Completed, false)]
    public void ContinuationUsesExplicitStateEvenWhenLabelAndPercentSuggestResume(DisplayContinuationState state, bool expected)
    {
        var hero = new DiscoveryHeroViewModel
        {
            Title = "Movie",
            MediaKind = "Movie",
            PrimaryActionLabel = "Resume",
            ProgressPct = 40,
            ContinuationState = state,
            PrimaryNavigationUrl = "/watch/asset",
            StatusText = "40% watched",
        };
        var cut = Render<CinematicHeroCarousel>(p => p.Add(c => c.Items, [hero]));
        Assert.Equal(expected ? "Continue Watching" : "Featured Content", cut.Find(".cinematic-hero-carousel__context").TextContent);
        Assert.Equal(expected, cut.FindAll("[role='progressbar']").Count > 0);
    }

    [Fact]
    public void NextOwnedEpisodeKeepsShowAndEpisodeIdentityWithTruthfulStart()
    {
        var hero = new DiscoveryHeroViewModel
        {
            Title = "The Show",
            Subtitle = "S2 E6 · The Next Chapter",
            Description = "Episode synopsis",
            Tagline = "Show tagline",
            MediaKind = "TV",
            Subject = DisplaySubjectKind.TvShow,
            ContinuationState = DisplayContinuationState.Unstarted,
            PrimaryActionLabel = "Watch S2 E6",
            LogoUrl = "/show-logo.png",
            PrimaryNavigationUrl = "/watch/episode",
            EpisodeContext = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "The Show", "The Next Chapter", 2, 6,
                DisplayContinuationState.Unstarted, null, null),
        };
        var cut = Render<CinematicHeroCarousel>(p => p.Add(c => c.Items, [hero]));
        Assert.Equal("The Show", cut.Find("h1").TextContent);
        Assert.Equal(hero.Subtitle, cut.Find(".tl-detail-hero__subtitle").TextContent);
        Assert.Contains("Episode synopsis", cut.Markup);
        Assert.DoesNotContain("Show tagline", cut.Markup);
        Assert.Contains("Watch S2 E6", cut.Find(".tl-detail-action--primary").TextContent);
        Assert.Empty(cut.FindAll("[role='progressbar']"));
        Assert.Empty(cut.FindAll(".tl-detail-hero__logo"));
    }

    [Fact]
    public void ExplicitPausePersistsAfterFocusAndHoverLeave()
    {
        var cut = Render<CinematicHeroCarousel>(p => p.Add(c => c.Items, [Hero("First"), Hero("Second")]));
        var carousel = cut.Find(".cinematic-hero-carousel");
        carousel.TriggerEvent("onmouseenter", new MouseEventArgs());
        Assert.Contains("is-rotation-paused", carousel.ClassName);
        cut.Find("[aria-label='Pause featured item rotation']").Click();
        carousel.TriggerEvent("onmouseleave", new MouseEventArgs());
        carousel.TriggerEvent("onfocusout", new FocusEventArgs());
        Assert.Contains("is-rotation-paused", carousel.ClassName);
        cut.Find("[aria-label='Resume featured item rotation']").Click();
        Assert.DoesNotContain("is-rotation-paused", carousel.ClassName);
        carousel.TriggerEvent("onfocusin", new FocusEventArgs());
        Assert.Contains("is-rotation-paused", carousel.ClassName);
    }

    [Fact]
    public void ReducedMotionPausesAutomaticRotationButAllowsManualNext()
    {
        JSInterop.Setup<bool>("tuvimaPrefersReducedMotion").SetResult(true);
        var cut = Render<CinematicHeroCarousel>(p => p.Add(c => c.Items, [Hero("First"), Hero("Second")]));
        cut.WaitForAssertion(() => Assert.Contains("is-rotation-paused", cut.Find(".cinematic-hero-carousel").ClassName));
        cut.Find("[aria-label='Next featured item']").Click();
        cut.WaitForAssertion(() =>
        {
            Assert.Equal("Second", cut.Find("h1").TextContent);
            Assert.Contains("is-rotation-paused", cut.Find(".cinematic-hero-carousel").ClassName);
        });
    }

    [Fact]
    public void HomeCropIsCenteredAndDetailCropRetainsTopCenter()
    {
        var art = new ArtworkSet { HeroArtwork = new HeroArtworkViewModel { HasImage = true, Url = "/art.jpg", Mode = HeroArtworkMode.BackdropWithRenderedTitle } };
        var detail = Render<HeroBackdrop>(p => p.Add(c => c.Artwork, art));
        Assert.Contains("--hero-image-position:center top", detail.Find(".tl-detail-media-stage").GetAttribute("style"));
        var home = Render<HeroBackdrop>(p => p.Add(c => c.Artwork, art).Add(c => c.CenterLandscape, true));
        Assert.Contains("--hero-image-position:center center", home.Find(".tl-detail-media-stage").GetAttribute("style"));
    }

    [Fact]
    public void LandscapeUsesBoundedResponsiveRenditions()
    {
        var cut = Render<CinematicHeroCarousel>(p => p.Add(c => c.Items, [new DiscoveryHeroViewModel
        {
            Title = "Artwork", MediaKind = "Movie", HeroBackgroundImageUrl = "/api/v1/display/artwork/assets/asset/backdrop",
            BackgroundWidthPx = 3840, BackgroundHeightPx = 2160,
        }]));
        var image = cut.Find(".tl-detail-media-stage__background");
        Assert.EndsWith("?size=m", image.GetAttribute("src"));
        Assert.Contains("size=s 320w", image.GetAttribute("srcset"));
        Assert.Contains("size=l 2160w", image.GetAttribute("srcset"));
        Assert.Equal("100vw", image.GetAttribute("sizes"));
    }

    [Theory]
    [InlineData("Book", 1000, 1500, "213w", "640w", "1000w")]
    [InlineData("Music", 1200, 1200, "320w", "960w", "1200w")]
    [InlineData("Book", 200, 300, "200w", null, null)]
    public void CoverRenditionsUseNativeWidthWithoutUpscaling(string kind, int width, int height,
        string smallWidth, string? mediumWidth, string? largeWidth)
    {
        var cut = Render<CinematicHeroCarousel>(p => p.Add(c => c.Items, [new DiscoveryHeroViewModel
        {
            Title = "Cover", MediaKind = kind, SurfaceKind = kind == "Music" ? MediaTileSurfaceKind.CoverSquare : MediaTileSurfaceKind.CoverPortrait,
            PreviewImageUrl = "/stream/artwork/cover", CoverWidthPx = width, CoverHeightPx = height,
        }]));
        var image = cut.Find(".tl-detail-media-stage__cover");
        Assert.EndsWith("?size=m", image.GetAttribute("src"));
        var set = image.GetAttribute("srcset");
        Assert.Contains(smallWidth, set);
        if (mediumWidth is not null)
        {
            Assert.Contains(mediumWidth, set);
        }
        if (largeWidth is not null)
        {
            Assert.Contains(largeWidth, set);
        }
        Assert.DoesNotContain("2160w", set);
    }

    [Fact]
    public void UnknownNativeDimensionsKeepBoundedImageWithoutInventedWidthDescriptors()
    {
        var cut = Render<CinematicHeroCarousel>(p => p.Add(c => c.Items, [new DiscoveryHeroViewModel
        {
            Title = "Unknown", MediaKind = "Movie", HeroBackgroundImageUrl = "/stream/artwork/unknown",
        }]));
        var image = cut.Find(".tl-detail-media-stage__background");
        Assert.EndsWith("?size=m", image.GetAttribute("src"));
        Assert.True(string.IsNullOrEmpty(image.GetAttribute("srcset")));
    }

    [Fact]
    public void NativeButtonStylesCrossTheComponentScopeBoundary()
    {
        var folder = new DirectoryInfo(AppContext.BaseDirectory);
        while (folder is not null && !File.Exists(Path.Combine(folder.FullName, "MediaEngine.slnx")))
        {
            folder = folder.Parent;
        }
        Assert.NotNull(folder);
        var css = File.ReadAllText(Path.Combine(folder.FullName, "src/MediaEngine.Web/Components/Cinematic/CinematicHeroCarousel.razor.css"));
        foreach (var selector in new[] { "__control {", "__control--previous {", "__control--next {", "__control--pause {", "__timeline-item {", "__timeline-item.is-active {" })
        {
            Assert.Contains(".cinematic-hero-carousel ::deep .cinematic-hero-carousel" + selector, css);
        }
        Assert.Contains("height: var(--tl-touch-target-min, 48px)", css);
        Assert.Contains("width: var(--tl-touch-target-min, 48px)", css);
    }

    [Fact]
    public void EnlargedPhoneTextRetainsFivePositionTargetsAndAReadableResumeAction()
    {
        var heroes = Enumerable.Range(1, 5).Select(i => new DiscoveryHeroViewModel { Title = $"Book {i}", MediaKind = "Book", Genres = ["Adventure", "Science Fiction"], PrimaryActionLabel = "Continue Reading", PrimaryNavigationUrl = "/read/book", ContinuationState = DisplayContinuationState.InProgress, ProgressPct = 42 }).ToList();
        var cut = Render<CinematicHeroCarousel>(p => p.Add(c => c.Items, heroes));
        Assert.Equal(5, cut.FindAll(".cinematic-hero-carousel__timeline-item").Count);
        Assert.Contains("Continue Reading", cut.Find(".tl-detail-action--primary").TextContent);
        Assert.Equal(new[] { "Adventure", "Science Fiction" }, cut.FindAll(".tl-detail-hero-genre").Select(genre => genre.TextContent.Trim()));
        var folder = new DirectoryInfo(AppContext.BaseDirectory);
        while (folder is not null && !File.Exists(Path.Combine(folder.FullName, "MediaEngine.slnx")))
        {
            folder = folder.Parent;
        }
        Assert.NotNull(folder);
        var css = File.ReadAllText(Path.Combine(folder.FullName, "src/MediaEngine.Web/Components/Cinematic/CinematicHeroCarousel.razor.css"));
        Assert.Contains("padding-left: calc(var(--tl-safe-area-left, 0px) + 56px)", css);
        Assert.Contains("grid-template-columns: minmax(0, 1fr)", css);
        Assert.Contains("flex: 0 0 48px", css);
        Assert.Contains("max-width: calc(100% - 16px)", css);
        Assert.Contains(".cinematic-hero-carousel ::deep .tl-detail-metadata-stack", css);
        Assert.Contains(".cinematic-hero-carousel ::deep .tl-detail-hero-genres", css);
        Assert.Contains(".cinematic-hero-carousel ::deep .tl-detail-hero-genre", css);
        Assert.Contains("flex-wrap: wrap;", css);
        Assert.Contains("overflow-wrap: anywhere;", css);
        Assert.DoesNotContain("+ 3.75rem", css);
    }

    private static DiscoveryHeroViewModel Hero(string title) => new() { Title = title, MediaKind = "Movie", PrimaryActionLabel = "Watch" };
}
