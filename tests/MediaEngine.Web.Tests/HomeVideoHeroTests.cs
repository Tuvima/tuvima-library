using Bunit;
using MediaEngine.Contracts.Display;
using MediaEngine.Domain.Services;
using MediaEngine.Web.Components.Cinematic;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Playback;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>The Home hero for a folder of videos and for a single video: a small label, one muted line, then Play or Resume (Home hero mock).</summary>
public sealed class HomeVideoHeroTests : AsyncBunitContext
{
    private readonly DeviceContextService _device;

    public HomeVideoHeroTests()
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

    private static DiscoveryHeroViewModel Folder(bool started) => new()
    {
        Title = "Python Course",
        MediaKind = VideoFolderWording.FolderMediaType,
        RepresentativeEntityId = Guid.NewGuid(),
        ContinuationState = started ? DisplayContinuationState.InProgress : DisplayContinuationState.Unstarted,
        ProgressPct = started ? 40 : null,
        RemainingSeconds = started ? 1500 : null,
        StatusText = started ? "40% · 25 min remaining" : null,
        Subtitle = started
            ? "88 videos · 14 sections · Next up: Variables · 25 minutes left"
            : "88 videos · 14 sections",
        MetaPills = ["2024", "21 min", "88 videos"],
        PrimaryActionLabel = started ? "Resume" : "Play",
        PrimaryNavigationUrl = "/watch/player/next",
        SecondaryNavigationUrl = "/details/tvshow/folder?context=watch",
    };

    private static DiscoveryHeroViewModel Single(bool started) => new()
    {
        Title = "Sample Talk",
        MediaKind = "Video",
        WorkId = Guid.NewGuid(),
        ContinuationState = started ? DisplayContinuationState.InProgress : DisplayContinuationState.Unstarted,
        ProgressPct = started ? 40 : null,
        RemainingSeconds = started ? 2280 : null,
        MetaPills = ["21 min"],
        PrimaryActionLabel = started ? "Continue Reading" : "Play",
        PrimaryNavigationUrl = "/watch/player/x",
    };

    private IRenderedComponent<CinematicHeroCarousel> RenderHero(DiscoveryHeroViewModel hero) =>
        Render<CinematicHeroCarousel>(p => p.Add(c => c.Items, [hero]));

    [Fact]
    public void AFolderNobodyHasStarted_DesktopHeroSaysVideoFolderOverItsTitleThenItsCountsThenPlay()
    {
        var cut = RenderHero(Folder(started: false));

        var label = cut.Find(".tl-detail-hero__identity .cinematic-hero-carousel__context--above-title");
        Assert.Equal("Video folder", label.TextContent.Trim());
        Assert.Equal("88 videos · 14 sections", cut.Find(".tl-detail-hero__subtitle").TextContent.Trim());
        Assert.Equal("Play", cut.Find(".tl-detail-action--primary").TextContent.Trim());
        // Look v3 D12: the folder is an Open button with a folder icon, not a text link.
        Assert.Equal("Open folder", cut.Find(".cinematic-hero-carousel__folder-link").GetAttribute("aria-label"));
        Assert.Equal("/details/tvshow/folder?context=watch", cut.Find(".cinematic-hero-carousel__folder-link").GetAttribute("href"));
        AssertNeverReading(cut.Markup);
    }

    [Fact]
    public void AFolderWithAStartedVideo_DesktopHeroSaysContinueWatchingNextUpAndResume()
    {
        var cut = RenderHero(Folder(started: true));

        Assert.Equal("Continue Watching", cut.Find(".cinematic-hero-carousel__context--above-title").TextContent.Trim());
        Assert.Contains("Next up: Variables", cut.Find(".tl-detail-hero__subtitle").TextContent);
        Assert.Contains("25 minutes left", cut.Find(".tl-detail-hero__subtitle").TextContent);
        Assert.StartsWith("Resume", cut.Find(".tl-detail-action--primary").TextContent.Trim());
        Assert.NotEmpty(cut.FindAll(".cinematic-hero-carousel__folder-link"));
        AssertNeverReading(cut.Markup);
    }

    [Fact]
    public void AFolderWhoseButtonOpensItAsTheFallback_KeepsOpenFolderAndAddsNoSecondLink()
    {
        var hero = Folder(started: false);
        var open = new DiscoveryHeroViewModel
        {
            Title = hero.Title,
            MediaKind = hero.MediaKind,
            RepresentativeEntityId = hero.RepresentativeEntityId,
            PrimaryActionLabel = "Open folder",
            PrimaryNavigationUrl = "/details/tvshow/folder?context=watch",
            Subtitle = "14 sections",
        };

        var cut = RenderHero(open);

        Assert.Equal("Open folder", cut.Find(".tl-detail-action--primary").TextContent.Trim());
        Assert.Empty(cut.FindAll(".cinematic-hero-carousel__folder-link"));
    }

    [Theory]
    [InlineData(false, "Video", "21 min", "Play")]
    [InlineData(true, "Continue Watching", "38 minutes left", "Resume")]
    public void ASingleVideo_DesktopHeroSaysVideoOrContinueWatchingItsLengthOrTimeLeftAndPlayOrResume(bool started, string label, string line, string button)
    {
        var cut = RenderHero(Single(started));

        Assert.Equal(label, cut.Find(".cinematic-hero-carousel__context--above-title").TextContent.Trim());
        Assert.Equal(line, cut.Find(".tl-detail-hero__subtitle").TextContent.Trim());
        Assert.StartsWith(button, cut.Find(".tl-detail-action--primary").TextContent.Trim());
        Assert.Empty(cut.FindAll(".cinematic-hero-carousel__folder-link"));
        AssertNeverReading(cut.Markup);
    }

    [Fact]
    public void AnotherKindKeepsItsLabelAtTheTopRight_AndItsOwnMetadataPills()
    {
        var movie = new DiscoveryHeroViewModel
        {
            Title = "A Movie",
            MediaKind = "Movie",
            PrimaryActionLabel = "Resume",
            ContinuationState = DisplayContinuationState.InProgress,
            ProgressPct = 30,
            MetaPills = ["2024", "98 min"],
        };

        var cut = RenderHero(movie);

        Assert.Empty(cut.FindAll(".cinematic-hero-carousel__context--above-title"));
        Assert.Equal("Continue Watching", cut.Find(".cinematic-hero-carousel__context").TextContent.Trim());
        Assert.Empty(cut.FindAll(".tl-detail-hero__subtitle"));
    }

    [Fact]
    public async Task APhoneFolderHero_ShowsTheLabelTheCountsAndPlay()
    {
        await UsePhoneAsync();

        var cut = RenderHero(Folder(started: false));

        Assert.Equal("Video folder", cut.Find(".phone-hero__tag").TextContent.Trim());
        Assert.Equal("88 videos · 14 sections", cut.Find(".phone-hero__meta").TextContent.Trim());
        Assert.Equal("Play", cut.Find(".tl-detail-action-label > span:first-child").TextContent.Trim());
        AssertNeverReading(cut.Markup);
    }

    [Fact]
    public async Task APhoneFolderHeroWithAStartedVideo_ShowsContinueWatchingNextUpAndResumeWithTheTimeLeft()
    {
        await UsePhoneAsync();

        var cut = RenderHero(Folder(started: true));

        Assert.Equal("Continue Watching", cut.Find(".phone-hero__tag").TextContent.Trim());
        Assert.Contains("Next up: Variables", cut.Find(".phone-hero__meta").TextContent);
        Assert.Equal("Resume", cut.Find(".tl-detail-action-label > span:first-child").TextContent.Trim());
        Assert.Equal("25 min left", cut.Find(".tl-detail-action-progress__label").TextContent.Trim());
    }

    [Theory]
    [InlineData(false, "Video", "21 min", "Play")]
    [InlineData(true, "Continue Watching", "38 minutes left", "Resume")]
    public async Task APhoneSingleVideoHero_SaysVideoOrContinueWatchingItsLengthOrTimeLeftAndPlayOrResume(bool started, string label, string line, string button)
    {
        await UsePhoneAsync();

        var cut = RenderHero(Single(started));

        Assert.Equal(label, cut.Find(".phone-hero__tag").TextContent.Trim());
        Assert.Equal(line, cut.Find(".phone-hero__meta").TextContent.Trim());
        Assert.Equal(button, cut.Find(".tl-detail-action-label > span:first-child").TextContent.Trim());
        AssertNeverReading(cut.Markup);
    }

    [Fact]
    public void TheVideoLabelAndLine_ArePureRulesThatLeaveOtherKindsAlone()
    {
        Assert.Equal("Video folder", PhoneHeroPresentation.VideoLabel(Folder(started: false)));
        Assert.Equal("Video", PhoneHeroPresentation.VideoLabel(Single(started: false)));
        Assert.Equal("Continue Watching", PhoneHeroPresentation.VideoLabel(Single(started: true)));
        var book = new DiscoveryHeroViewModel { Title = "B", MediaKind = "Book", MetaPills = ["592 pages"] };
        Assert.Null(PhoneHeroPresentation.VideoLabel(book));
        Assert.Null(PhoneHeroPresentation.VideoLine(book));
        Assert.Equal(PhoneHeroPresentation.MetaLine(book), PhoneHeroPresentation.HeroLine(book));
    }

    [Fact]
    public void AFolderHeroWithoutALineFromTheEngine_FallsBackToItsCountsAndNeverToTheLengthOfOneVideo()
    {
        var hero = new DiscoveryHeroViewModel { Title = "F", MediaKind = "Videos", MetaPills = ["2024", "21 min", "14 sections", "88 videos"] };

        Assert.Equal("88 videos · 14 sections", PhoneHeroPresentation.VideoLine(hero));
    }

    private static void AssertNeverReading(string markup)
    {
        Assert.DoesNotContain("Reading", markup, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Book", markup, StringComparison.OrdinalIgnoreCase);
    }
}
