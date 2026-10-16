using Bunit;
using MediaEngine.Contracts.Display;
using MediaEngine.Web.Components.Cinematic;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Playback;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

public sealed class PhoneHomeHeroTests : AsyncBunitContext
{
    private readonly DeviceContextService _device;

    public PhoneHomeHeroTests()
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

    private Task UseAsync(string formFactor) =>
        _device.InitialiseAsync(new DeviceContextSnapshot(formFactor == "phone" ? "mobile" : "web", formFactor, formFactor != "desktop"));

    private static DiscoveryHeroViewModel Book() => new()
    {
        WorkId = Guid.NewGuid(),
        Title = "Leviathan Wakes",
        Subtitle = "James S. A. Corey",
        MediaKind = "Book",
        MetaPills = ["2011", "592 pages"],
        PreviewImageUrl = "/engine-image/stream/artwork/a1",
        CoverWidthPx = 1691,
        CoverHeightPx = 2500,
        PrimaryActionLabel = "Read",
        PrimaryNavigationUrl = "/read/a",
        SurfaceKind = MediaTileSurfaceKind.CoverPortrait,
    };

    private static DiscoveryHeroViewModel Movie() => new()
    {
        WorkId = Guid.NewGuid(),
        Title = "Dune: Part Two",
        MediaKind = "Movie",
        MetaPills = ["2024", "166 min", "★ 8.5", "PG-13"],
        HeroBackgroundImageUrl = "/engine-image/stream/artwork/b1",
        LogoUrl = "/engine-image/stream/artwork/b2",
        SurfaceKind = MediaTileSurfaceKind.BannerLandscape,
        PrimaryActionLabel = "Play",
        PrimaryNavigationUrl = "/watch/b",
    };

    private static DiscoveryHeroViewModel Tv() => new()
    {
        WorkId = Guid.NewGuid(),
        Title = "The Continental",
        MediaKind = "TV",
        MetaPills = ["2023", "1 season", "5250.3 min"],
        SurfaceKind = MediaTileSurfaceKind.BannerLandscape,
        PrimaryActionLabel = "Watch Episode",
        PrimaryNavigationUrl = "/watch/c",
        IsSettling = true,
    };

    private static DiscoveryHeroViewModel Album() => new()
    {
        WorkId = Guid.NewGuid(),
        Title = "Under Rug Swept",
        Subtitle = "Alanis Morissette",
        MediaKind = "Music",
        MetaPills = ["2002", "1 tracks", "1 min"],
        PreviewImageUrl = "/engine-image/stream/d/cover",
        CoverWidthPx = 100,
        CoverHeightPx = 150,
        PrimaryActionLabel = "Play",
        PrimaryNavigationUrl = "/listen/d",
    };

    private static DiscoveryHeroViewModel Audiobook() => new()
    {
        WorkId = Guid.NewGuid(),
        Title = "The Martian (Unabridged)",
        Subtitle = "Andy Weir",
        MediaKind = "Audiobook",
        MetaPills = ["2020", "10h 53m"],
        PreviewImageUrl = "/engine-image/stream/e/cover",
        CoverWidthPx = 2026,
        CoverHeightPx = 2026,
        PrimaryActionLabel = "Listen",
        PrimaryNavigationUrl = "/listen/e",
    };

    private IRenderedComponent<CinematicHeroCarousel> RenderPhone(params DiscoveryHeroViewModel[] items) =>
        Render<CinematicHeroCarousel>(p => p.Add(c => c.Items, items));

    [Fact]
    public async Task EverySlideUsesTheSameCardWithTheSameFixedSlots()
    {
        await UseAsync("phone");
        var items = new[] { Book(), Movie(), Tv(), Album(), Audiobook() };
        var cut = RenderPhone(items);
        for (var index = 0; index < items.Length; index++)
        {
            cut.FindAll(".phone-hero__dot")[index].Click();
            Assert.Equal(items[index].Title, cut.Find("h1").TextContent.Trim());
            // The same slots on every slide: one card, a title, a meta line, one primary button and at most two icon buttons.
            Assert.Single(cut.FindAll(".phone-hero__card"));
            Assert.Single(cut.FindAll(".phone-hero__title"));
            Assert.Single(cut.FindAll(".phone-hero__meta"));
            Assert.Single(cut.FindAll(".phone-hero__primary"));
            Assert.InRange(cut.FindAll(".phone-hero__icon-button").Count, 0, 2);
            Assert.Single(cut.FindAll(".phone-hero__actions"));
        }
    }

    [Fact]
    public async Task PhoneHasNoArrowsPauseButtonOrRotationWhileDesktopAndTabletKeepThem()
    {
        await UseAsync("phone");
        var phone = RenderPhone(Book(), Movie());
        Assert.Empty(phone.FindAll("[aria-label='Previous featured item']"));
        Assert.Empty(phone.FindAll("[aria-label='Next featured item']"));
        Assert.Empty(phone.FindAll("[aria-label*='rotation']"));
        Assert.Equal(2, phone.FindAll(".phone-hero__dot").Count);

        foreach (var formFactor in new[] { "desktop", "tablet" })
        {
            await UseAsync(formFactor);
            var other = RenderPhone(Book(), Movie());
            Assert.NotEmpty(other.FindAll("[aria-label='Previous featured item']"));
            Assert.NotEmpty(other.FindAll("[aria-label='Next featured item']"));
            Assert.NotEmpty(other.FindAll("[aria-label='Pause featured item rotation']"));
            Assert.Empty(other.FindAll(".phone-hero"));
        }
    }

    [Fact]
    public async Task DotsAreLabelledButtonsAndTheRegionHasAnAccessibleName()
    {
        await UseAsync("phone");
        var cut = RenderPhone(Book(), Movie(), Tv());
        var region = cut.Find("section.cinematic-hero-carousel--phone");
        Assert.Equal("Featured media", region.GetAttribute("aria-label"));
        Assert.Equal("carousel", region.GetAttribute("aria-roledescription"));
        var dots = cut.FindAll(".phone-hero__dot");
        Assert.Equal(3, dots.Count);
        Assert.All(dots, dot => Assert.Equal("BUTTON", dot.TagName));
        Assert.Equal("Show featured item 2 of 3: Dune: Part Two", dots[1].GetAttribute("aria-label"));
        dots[1].Click();
        Assert.Equal("true", cut.FindAll(".phone-hero__dot")[1].GetAttribute("aria-current"));
    }

    [Fact]
    public async Task ArtLadderShowsBackdropWithLogoThenCoverThenAnHonestPlaceholder()
    {
        await UseAsync("phone");
        var cut = RenderPhone(Movie(), Book(), Album(), Tv());
        Assert.NotEmpty(cut.FindAll(".phone-hero__backdrop"));
        Assert.NotEmpty(cut.FindAll(".phone-hero__logo"));
        Assert.Contains("size=m", cut.Find(".phone-hero__backdrop").GetAttribute("src"));
        cut.FindAll(".phone-hero__dot")[1].Click();
        Assert.NotEmpty(cut.FindAll(".phone-hero__cover"));
        Assert.Contains("size=m", cut.Find(".phone-hero__cover").GetAttribute("src"));
        cut.FindAll(".phone-hero__dot")[2].Click();
        // A tiny stand-in cover is never drawn scaled up.
        Assert.Empty(cut.FindAll(".phone-hero__cover"));
        Assert.Equal("Album", cut.Find(".phone-hero__placeholder-word").TextContent.Trim());
        cut.FindAll(".phone-hero__dot")[3].Click();
        Assert.Equal("TV show", cut.Find(".phone-hero__placeholder-word").TextContent.Trim());
        Assert.Equal("Still being matched", cut.Find(".phone-hero__tag").TextContent.Trim());
    }

    [Fact]
    public async Task RateAndThumbsMoveIntoMoreAndTheMetaLineIsReadable()
    {
        await UseAsync("phone");
        var cut = RenderPhone(Book());
        Assert.Empty(cut.FindAll("[aria-label='Rate']"));
        Assert.Empty(cut.FindAll("[aria-label='I like this']"));
        cut.Find("[aria-label='More actions']").Click();
        Assert.Contains("I like this", cut.Markup);
        Assert.Contains("Not for me", cut.Markup);
        Assert.Equal("James S. A. Corey · 2011 · 592 pages", cut.Find(".phone-hero__meta").TextContent.Trim());
    }

    [Fact]
    public async Task SwipingAndTouchHoldWorkOnThePhoneCardThroughTheCarouselsOwnHandler()
    {
        await UseAsync("phone");
        var cut = RenderPhone(Book(), Movie(), Tv());
        // The swipe handler is registered on the carousel section that wraps the card, so it covers the card.
        JSInterop.VerifyInvoke("registerHeroSwipe");
        await cut.InvokeAsync(() => cut.Instance.OnHeroSwipe("next"));
        Assert.Equal("Dune: Part Two", cut.Find("h1").TextContent.Trim());
        await cut.InvokeAsync(() => cut.Instance.OnHeroSwipe("next"));
        Assert.Equal("The Continental", cut.Find("h1").TextContent.Trim());
        await cut.InvokeAsync(() => cut.Instance.OnHeroSwipe("previous"));
        await cut.InvokeAsync(() => cut.Instance.OnHeroSwipe("previous"));
        Assert.Equal("Leviathan Wakes", cut.Find("h1").TextContent.Trim());
        // Swiping backwards from the first slide wraps to the last, like the dots.
        await cut.InvokeAsync(() => cut.Instance.OnHeroSwipe("previous"));
        Assert.Equal("The Continental", cut.Find("h1").TextContent.Trim());
        await cut.InvokeAsync(() => cut.Instance.OnHeroTouchHold(true));
        await cut.InvokeAsync(() => cut.Instance.OnHeroTouchHold(false));
        Assert.Equal("true", cut.FindAll(".phone-hero__dot")[2].GetAttribute("aria-current"));
    }

    [Fact]
    public void PhoneHeroCssUsesOnlyExistingTokensAndNoForbiddenPatterns()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var css = File.ReadAllText(Path.Combine(root, "src", "MediaEngine.Web", "Components", "Cinematic", "PhoneHeroCard.razor.css"));
        Assert.DoesNotContain("::deep", css);
        Assert.DoesNotContain("!important", css);
        Assert.Contains("--phone-hero-tap", css);
        var razor = File.ReadAllText(Path.Combine(root, "src", "MediaEngine.Web", "Components", "Cinematic", "PhoneHeroCard.razor"));
        Assert.DoesNotContain(" style=", razor);
        Assert.DoesNotContain("<input", razor);
    }
}

public sealed class PhoneHeroPresentationTests
{
    [Theory]
    [InlineData(101, "1h 41m")]
    [InlineData(58, "58m")]
    [InlineData(120, "2h")]
    [InlineData(5250.3, "87h 30m")]
    [InlineData(0.4, null)]
    [InlineData(0, null)]
    [InlineData(-5, null)]
    public void RuntimeNeverShowsRawDecimalMinutes(double minutes, string? expected)
        => Assert.Equal(expected, PhoneHeroPresentation.FormatRuntime(minutes));

    [Theory]
    [InlineData(0, null)]
    [InlineData(1, "1 track")]
    [InlineData(2, "2 tracks")]
    public void PluralsAreRightAndZeroIsHidden(int count, string? expected)
        => Assert.Equal(expected, PhoneHeroPresentation.Count(count, "track"));

    private static DiscoveryHeroViewModel Hero(string kind, string? subtitle, params string[] pills) =>
        new() { MediaKind = kind, Subtitle = subtitle, MetaPills = pills };

    [Fact]
    public void BaselineBadCasesAreFixed()
    {
        // The measured baseline: a TV show with "5250.3 min" and an album with "1 tracks".
        Assert.Equal("2023 · 1 season", PhoneHeroPresentation.MetaLine(Hero("TV", null, "2023", "1 season", "5250.3 min")));
        Assert.Equal("Alanis Morissette · 1 track · 1m", PhoneHeroPresentation.MetaLine(Hero("Music", "Alanis Morissette", "2002", "1 tracks", "1 min")));
        Assert.Equal("2023 · 6 seasons", PhoneHeroPresentation.MetaLine(Hero("TV", null, "2023", "6 seasons")));
    }

    [Fact]
    public void EachMediaTypeShowsItsOwnPartsAndHidesEmptyOnes()
    {
        Assert.Equal("2024 · 2h 46m · ★ 8.5 · PG-13", PhoneHeroPresentation.MetaLine(Hero("Movie", null, "2024", "166 min", "★ 8.5", "PG-13")));
        Assert.Equal("2024", PhoneHeroPresentation.MetaLine(Hero("Movie", null, "2024", "0 min")));
        Assert.Equal("James S. A. Corey · 2011 · 592 pages", PhoneHeroPresentation.MetaLine(Hero("Book", "James S. A. Corey", "2011", "592 pages")));
        Assert.Equal("Andy Weir · 10h 53m", PhoneHeroPresentation.MetaLine(Hero("Audiobook", "Andy Weir", "2020", "10h 53m")));
        Assert.Equal("Radiohead", PhoneHeroPresentation.MetaLine(Hero("Music", "Radiohead", "0 tracks", "0 min")));
        Assert.Equal(string.Empty, PhoneHeroPresentation.MetaLine(Hero("Movie", null)));
    }

    [Fact]
    public void AnEpisodeReadsSeasonAndEpisodeThenYear()
    {
        var hero = new DiscoveryHeroViewModel
        {
            MediaKind = "TV",
            MetaPills = ["2023", "1 season"],
            EpisodeContext = new DisplayEpisodeContextDto(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Show", "Episode", 2, 6, DisplayContinuationState.Unstarted, null, null),
        };
        Assert.Equal("S2 E6 · 2023", PhoneHeroPresentation.MetaLine(hero));
    }
}
