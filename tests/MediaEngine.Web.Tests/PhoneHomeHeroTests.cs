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
    public async Task AStandInCoverShowsTheTintedTileAndARealCoverShowsTheCover()
    {
        await UseAsync("phone");
        var cut = RenderPhone(
            Album(),
            Book(),
            Made("Book", title: "Small but real", coverWidth: 300),
            Made("Book", title: "Unmeasured", coverWidth: null));
        // The Engine stand-in is a 100 x 150 picture: it is never drawn at its natural size.
        Assert.Empty(cut.FindAll(".phone-hero__cover"));
        Assert.Equal("Album", cut.Find(".phone-hero__placeholder-word").TextContent.Trim());
        Assert.Contains("phone-hero__card--placeholder", cut.Find(".phone-hero__card").ClassName);
        cut.FindAll(".phone-hero__dot")[1].Click();
        Assert.NotEmpty(cut.FindAll(".phone-hero__cover"));
        Assert.Empty(cut.FindAll(".phone-hero__placeholder"));
        cut.FindAll(".phone-hero__dot")[2].Click();
        Assert.NotEmpty(cut.FindAll(".phone-hero__cover"));
        cut.FindAll(".phone-hero__dot")[3].Click();
        Assert.Empty(cut.FindAll(".phone-hero__cover"));
        Assert.Equal("Book", cut.Find(".phone-hero__placeholder-word").TextContent.Trim());
    }

    private static DiscoveryHeroViewModel Made(
        string kind,
        string title = "A title",
        int? coverWidth = 1200,
        DisplayContinuationState state = DisplayContinuationState.Unstarted,
        double? progress = null,
        double? remaining = null,
        DisplayEpisodeContextDto? episode = null,
        string engineLabel = "Play") => new()
        {
            WorkId = Guid.NewGuid(),
            Title = title,
            MediaKind = kind,
            PreviewImageUrl = "/engine-image/stream/artwork/x",
            CoverWidthPx = coverWidth,
            CoverHeightPx = 1800,
            SurfaceKind = MediaTileSurfaceKind.CoverPortrait,
            ContinuationState = state,
            ProgressPct = progress,
            RemainingSeconds = remaining,
            EpisodeContext = episode,
            PrimaryActionLabel = engineLabel,
            PrimaryNavigationUrl = "/x",
        };

    private static readonly DisplayEpisodeContextDto UnstartedEpisode =
        new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Show", "Episode", 1, 1, DisplayContinuationState.Unstarted, null, null);

    private static readonly DisplayEpisodeContextDto StartedEpisode =
        new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Show", "Episode", 5, 1, DisplayContinuationState.InProgress, 2400, 3780);

    public static TheoryData<string, string, string> PrimaryLabels() => new()
    {
        { "book-unstarted", "Start Reading", "" },
        { "book-started", "Continue Reading", "32% read" },
        { "comic-unstarted", "Start Reading", "" },
        { "comic-started", "Continue Reading", "32% read" },
        { "movie-unstarted", "Play", "" },
        { "movie-started", "Resume", "23 min left" },
        { "tv-unstarted", "Watch S1 E1", "" },
        { "tv-started", "Resume S5 E1", "23 min left" },
        { "episode-unstarted", "Watch S1 E1", "" },
        { "episode-started", "Resume S5 E1", "23 min left" },
        { "audiobook-unstarted", "Listen", "" },
        { "audiobook-started", "Continue Listening", "1h 5m left" },
        { "album-unstarted", "Play album", "" },
        { "album-started", "Play album", "" },
    };

    private static DiscoveryHeroViewModel ForLabel(string key)
    {
        var started = key.EndsWith("-started", StringComparison.Ordinal);
        var state = started ? DisplayContinuationState.InProgress : DisplayContinuationState.Unstarted;
        var pct = started ? 32d : (double?)null;
        return key.Split('-')[0] switch
        {
            "book" => Made("Book", state: state, progress: pct, engineLabel: started ? "Continue Reading" : "Read"),
            "comic" => Made("Comic", state: state, progress: pct, engineLabel: started ? "Continue Reading" : "Read"),
            "movie" => Made("Movie", state: state, progress: pct, remaining: started ? 1380 : null, engineLabel: started ? "Continue" : "Play"),
            // A show with no episode context takes its position from the label the Engine sent.
            "tv" => Made("TV", state: state, progress: pct, remaining: started ? 1380 : null, engineLabel: started ? "Resume S5 E1" : "Watch S1 E1"),
            "episode" => Made("TV", state: state, progress: pct, episode: started ? StartedEpisode : UnstartedEpisode, engineLabel: "Watch"),
            "audiobook" => Made("Audiobook", state: state, progress: pct, remaining: started ? 3900 : null, engineLabel: started ? "Continue" : "Play"),
            _ => Made("Music", state: state, progress: pct, engineLabel: started ? "Continue" : "Play"),
        };
    }

    [Theory]
    [MemberData(nameof(PrimaryLabels))]
    public async Task ThePrimaryButtonSaysTheRightThingForEachTypeStartedOrNot(string key, string label, string detail)
    {
        await UseAsync("phone");
        var cut = RenderPhone(ForLabel(key));
        Assert.Equal(label, cut.Find(".phone-hero__primary-label").TextContent.Trim());
        var details = cut.FindAll(".phone-hero__primary-detail");
        if (detail.Length == 0)
        {
            Assert.Empty(details);
        }
        else
        {
            Assert.Equal(detail, Assert.Single(details).TextContent.Trim());
        }

        if (key.EndsWith("-unstarted", StringComparison.Ordinal))
        {
            // An unstarted item never says Continue or Resume and never shows a 0 percent.
            var text = cut.Find(".phone-hero__primary").TextContent;
            Assert.DoesNotContain("Continue", text);
            Assert.DoesNotContain("Resume", text);
            Assert.DoesNotContain("0%", text);
        }
    }

    [Fact]
    public async Task StartedWithoutANumberDropsTheSuffixInsteadOfInventingOne()
    {
        await UseAsync("phone");
        var cut = RenderPhone(Made("Movie", state: DisplayContinuationState.InProgress));
        Assert.Equal("Resume", cut.Find(".phone-hero__primary-label").TextContent.Trim());
        Assert.Empty(cut.FindAll(".phone-hero__primary-detail"));
        Assert.Equal("Resume", cut.Find(".phone-hero__primary").GetAttribute("aria-label"));
    }

    [Fact]
    public async Task AnOpenActionKeepsTheEngineWording()
    {
        await UseAsync("phone");
        var cut = RenderPhone(Made("Book", engineLabel: "Open Series"));
        Assert.Equal("Open Series", cut.Find(".phone-hero__primary-label").TextContent.Trim());
    }

    [Fact]
    public async Task ATitleWithNoLogoIsDrawnAsTextInTheLogoZoneAndALogoSitsInTheSameZone()
    {
        await UseAsync("phone");
        var cut = RenderPhone(Made("Book", title: "No Logo Here"), Movie());
        var zone = cut.Find(".phone-hero__title-zone");
        Assert.Empty(zone.QuerySelectorAll(".phone-hero__logo"));
        var title = zone.QuerySelector("h1.phone-hero__title");
        Assert.NotNull(title);
        Assert.Equal("No Logo Here", title!.TextContent.Trim());
        Assert.DoesNotContain("sr-only", title.ClassName);

        cut.FindAll(".phone-hero__dot")[1].Click();
        zone = cut.Find(".phone-hero__title-zone");
        Assert.Single(zone.QuerySelectorAll("img.phone-hero__logo"));
        // The title stays in the page for screen readers while the logo is drawn.
        Assert.Contains("sr-only", zone.QuerySelector("h1")!.ClassName);
    }

    [Fact]
    public void LogoAndButtonsAreBoundedSoTheCardHeightNeverChanges()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var css = File.ReadAllText(Path.Combine(root, "src", "MediaEngine.Web", "Components", "Cinematic", "PhoneHeroCard.razor.css"));
        var zone = Rule(css, ".phone-hero__title-zone");
        Assert.Contains("height: calc(var(--tl-font-size-xl, 1.5rem) * 2.4)", zone);
        Assert.Contains("overflow: hidden", zone);
        // A very wide logo is held by the width cap and a very tall one by the height cap; both keep their shape.
        var logo = Rule(css, ".phone-hero__logo");
        Assert.Contains("max-width: 70%", logo);
        Assert.Contains("max-height: 100%", logo);
        Assert.Contains("object-fit: contain", logo);
        // The card keeps one height formula and its content is positioned inside it, so nothing the text does can resize it.
        Assert.Contains("--phone-hero-card-height: calc(var(--phone-hero-visible) * 0.55)", css);
        Assert.Contains("height: var(--phone-hero-card-height)", Rule(css, ".phone-hero__card"));
        Assert.Contains("position: absolute", Rule(css, ".phone-hero__content"));
        var appCss = File.ReadAllText(Path.Combine(root, "src", "MediaEngine.Web", "wwwroot", "app.css"));
        Assert.Contains("max-height: var(--phone-hero-tap)", Rule(appCss, ".phone-hero__primary"));
    }

    private static string Rule(string css, string selector)
    {
        var start = css.IndexOf(selector + " {", StringComparison.Ordinal);
        Assert.True(start >= 0, $"{selector} rule not found");
        return css[start..css.IndexOf('}', start)];
    }

    [Fact]
    public async Task EveryTypeKeepsTheSameFixedSlotsWhateverTheLabelSays()
    {
        await UseAsync("phone");
        var items = new[] { ForLabel("book-started"), ForLabel("movie-started"), ForLabel("tv-unstarted"), ForLabel("album-started"), ForLabel("audiobook-started") };
        var cut = RenderPhone(items);
        for (var index = 0; index < items.Length; index++)
        {
            cut.FindAll(".phone-hero__dot")[index].Click();
            Assert.Equal("phone-hero__card", cut.Find(".phone-hero__card").ClassList.First());
            Assert.Single(cut.FindAll(".phone-hero__primary"));
            Assert.Single(cut.FindAll(".phone-hero__title-zone"));
        }
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

    private static DiscoveryHeroViewModel WithCover(int? width) => new()
    {
        MediaKind = "Book",
        PreviewImageUrl = "/engine-image/stream/artwork/c1",
        CoverWidthPx = width,
        SurfaceKind = MediaTileSurfaceKind.CoverPortrait,
    };

    [Theory]
    [InlineData(100, PhoneHeroArt.Placeholder)]
    [InlineData(null, PhoneHeroArt.Placeholder)]
    [InlineData(239, PhoneHeroArt.Placeholder)]
    [InlineData(240, PhoneHeroArt.Cover)]
    [InlineData(300, PhoneHeroArt.Cover)]
    [InlineData(1691, PhoneHeroArt.Cover)]
    public void OnlyARealCoverIsDrawnAndAStandInOrUnmeasuredOneGetsTheTile(int? width, PhoneHeroArt expected)
        => Assert.Equal(expected, PhoneHeroPresentation.ResolveArt(WithCover(width)).Mode);

    [Fact]
    public void ACoverUrlIsNeverUsedForAPlaceholder()
        => Assert.Null(PhoneHeroPresentation.ResolveArt(WithCover(100)).ImageUrl);

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
