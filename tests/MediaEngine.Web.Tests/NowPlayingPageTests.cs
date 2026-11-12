using Bunit;
using MediaEngine.Contracts.Playback;
using MediaEngine.Web.Components.Listen;
using MediaEngine.Web.Components.Navigation;
using MediaEngine.Web.Components.Pages;
using MediaEngine.Web.Components.Player;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Navigation;
using MediaEngine.Web.Services.Playback;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Tests.Support;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;

namespace MediaEngine.Web.Tests;

/// <summary>
/// The Now playing page of unified player v2 (D24, acceptance A4; mockup screen 13): music and audiobooks, reached from the bar's Now playing tool, the
/// cover and title click and the navbar icon, with Back to library and Close returning to where the person came from without touching playback.
/// </summary>
public sealed class NowPlayingPageTests : AsyncBunitContext
{
    private static readonly string RepoRoot = FindRepoRoot();

    public NowPlayingPageTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        JSInterop.SetupModule("./js/context-sidebar.js");
        JSInterop.SetupModule("./js/playback-lyrics.js");
        JSInterop.SetupModule("./js/playback-audio-presentation.js");
        Services.AddLogging();
        Services.AddLocalization();
        Services.AddNativeUiServices();
    }

    // ---- The pages ----

    [Fact]
    public void MusicPage_ShowsTheCoverTheTitleTheArtistAsALinkAndAlbumAndYear()
    {
        var artist = Guid.NewGuid();
        var playback = AddServices(Song("Stay a Little Longer", "The Midnight Coast", "Northern Lights", "2026") with { ArtistPersonId = artist }, Song("Next song", "The Midnight Coast", "Northern Lights", "2026"));

        var cut = Render<NowPlayingPage>();

        Assert.Equal("Stay a Little Longer", cut.Find("h1").TextContent);
        Assert.Equal($"/details/person/{artist:D}", cut.Find("[data-now-playing-artist] a").GetAttribute("href"));
        Assert.Equal("Northern Lights · 2026", cut.Find("[data-now-playing-album]").TextContent.Trim());
        Assert.Single(cut.FindAll("[data-now-playing-cover] img[data-image-display='now-playing-artwork']"));
        Assert.Equal("Back to library", cut.Find("[data-now-playing-back]").TextContent.Trim());
        Assert.Single(cut.FindAll("[data-now-playing-close][aria-label='Close']"));
        Assert.True(playback.IsPlaying);
    }

    [Fact]
    public void MusicPage_FollowsTheMockupsOrder_TheTopLineThenTheCoverTitleArtistAndAlbumThenTheTabsAndTheirPanel()
    {
        AddServices(Song("Stay a Little Longer", "The Midnight Coast", "Northern Lights", "2026") with { ArtistPersonId = Guid.NewGuid() }, Song("Next song", "The Midnight Coast"));

        var cut = Render<NowPlayingPage>();

        var order = cut.FindAll("[data-now-playing-back], [data-now-playing-close], [data-now-playing-cover], h1, [data-now-playing-artist], [data-now-playing-album], [role='tablist'], [data-now-playing-panel]")
            .Select(element => element.GetAttribute("role") ?? (element.TagName == "H1" ? "title" : element.Attributes.First(attribute => attribute.Name.StartsWith("data-now-playing-", StringComparison.Ordinal)).Name))
            .ToArray();
        Assert.Equal(["data-now-playing-back", "data-now-playing-close", "data-now-playing-cover", "title", "data-now-playing-artist", "data-now-playing-album", "tablist", "tabpanel"], order);
        // The cover and the facts are the left column and the tabs the right one, so the tabs never sit above the cover.
        Assert.NotNull(cut.Find(".np__body > .np__facts [data-now-playing-cover]"));
        Assert.NotNull(cut.Find(".np__body > .np__tabs-column [role='tablist']"));
    }

    [Fact]
    public async Task MusicPage_HasLyricsQueueAndHistoryTabsInThatOrderAndChoosingOneShowsItsList()
    {
        AddServices(Song("Current", "Artist"), Song("Next", "Artist"));
        var cut = Render<NowPlayingPage>();

        Assert.Equal(["Lyrics", "Queue", "History"], cut.FindAll("[role='tab']").Select(tab => tab.TextContent.Trim()).ToArray());
        Assert.Equal("lyrics", cut.Find("[data-now-playing-panel]").GetAttribute("data-now-playing-panel"));
        Assert.NotEmpty(cut.FindAll("[data-playback-lyrics]"));

        await cut.Find("[role='tab'][data-playback-list-tab='queue']").ClickAsync();
        cut.WaitForAssertion(() =>
        {
            Assert.Equal("queue", cut.Find("[data-now-playing-panel]").GetAttribute("data-now-playing-panel"));
            Assert.Equal("true", cut.Find("[role='tab'][data-playback-list-tab='queue']").GetAttribute("aria-selected"));
            Assert.NotEmpty(cut.FindAll("[data-playback-list-row]"));
        });

        await cut.Find("[role='tab'][data-playback-list-tab='history']").ClickAsync();
        cut.WaitForAssertion(() => Assert.Equal("history", cut.Find("[data-now-playing-panel]").GetAttribute("data-now-playing-panel")));
    }

    [Fact]
    public async Task AudiobookPage_ShowsBookAuthorNarratorAndTheChapterLineWithChaptersAndAboutTabs()
    {
        var asset = Guid.NewGuid();
        var book = Book("The Way Beyond", asset, position: 100);
        AddServices(book, audiobook: true, position: 100, duration: 240);

        var cut = Render<NowPlayingPage>();

        Assert.Equal("The Way Beyond", cut.Find("h1").TextContent);
        Assert.Equal("By Maya Ellis", cut.Find("[data-now-playing-authors]").TextContent.Trim());
        Assert.Equal("Narrated by Sam Rowe", cut.Find("[data-now-playing-narrators]").TextContent.Trim());
        Assert.Equal("Chapter 2 of 4 · Beyond the ridge", cut.Find("[data-now-playing-chapter]").TextContent.Trim());
        Assert.Equal(["Chapters", "About"], cut.FindAll("[role='tab']").Select(tab => tab.TextContent.Trim()).ToArray());
        Assert.Equal("chapters", cut.Find("[data-now-playing-panel]").GetAttribute("data-now-playing-panel"));
        // The book's cover and title are already beside the list, so the Chapters list does not repeat them.
        Assert.Empty(cut.FindAll("[data-chapters-identity]"));

        await cut.Find("[role='tab'][data-playback-list-tab='about']").ClickAsync();
        cut.WaitForAssertion(() =>
        {
            var about = cut.Find("[data-now-playing-about]");
            Assert.Contains("A walk across the high country.", about.TextContent);
            Assert.Contains("Maya Ellis", about.TextContent);
            Assert.Contains("Sam Rowe", about.TextContent);
            Assert.Contains("2024", about.TextContent);
            Assert.Contains("8h 12m", about.TextContent);
        });
    }

    [Fact]
    public void AudiobookWithoutChapters_ShowsOnlyAbout()
    {
        var asset = Guid.NewGuid();
        AddServices(Book("One piece", asset, position: 10) with { Chapters = [] }, audiobook: true, position: 10, duration: 240);

        var cut = Render<NowPlayingPage>();

        Assert.Equal(["About"], cut.FindAll("[role='tab']").Select(tab => tab.TextContent.Trim()).ToArray());
        Assert.Empty(cut.FindAll("[data-now-playing-chapter]"));
    }

    [Fact]
    public void ThePageDrawsNoTransportAndNoAudioElementOfItsOwn()
    {
        AddServices(Song("Current", "Artist"), Song("Next", "Artist"));

        var cut = Render<NowPlayingPage>();

        // The persistent bar is the only transport (unified player v2 D24): no play button, no timeline, no volume, no media element on the page.
        Assert.Empty(cut.FindAll("audio, video"));
        Assert.Empty(cut.FindAll("[data-playback-zone]"));
        Assert.Empty(cut.FindAll("input[type='range']"));
        Assert.Empty(cut.FindAll("button[aria-label='Play'], button[aria-label='Pause'], button[aria-label='Next'], button[aria-label='Previous']"));
    }

    // ---- Back to library and Close ----

    [Theory]
    [InlineData("[data-now-playing-back]")]
    [InlineData("[data-now-playing-close]")]
    public async Task BackToLibraryAndClose_ReturnToThePageThePersonCameFromAndNeverStopPlayback(string button)
    {
        var playback = AddServices(Song("Current", "Artist"), Song("Next", "Artist"));
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo(NowPlayingRoute.Build("/listen/music/playlists/12345678-1234-1234-1234-123456789012"));
        var cut = Render<NowPlayingPage>();
        var position = playback.CurrentTimeSeconds;

        await cut.Find(button).ClickAsync();

        Assert.EndsWith("/listen/music/playlists/12345678-1234-1234-1234-123456789012", navigation.Uri);
        Assert.True(playback.IsPlaying);
        Assert.True(playback.HasQueue);
        Assert.Equal(position, playback.CurrentTimeSeconds);
    }

    [Theory]
    [InlineData("")]
    [InlineData("?return=https%3A%2F%2Fexample.com%2Fphish")]
    [InlineData("?return=%2F%2Fexample.com")]
    [InlineData("?return=%2Flisten%2Fnow-playing")]
    public async Task BackToLibrary_WithNothingSafeToReturnTo_GoesToListen(string query)
    {
        AddServices(Song("Current", "Artist"));
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo(NowPlayingRoute.Path + query);
        var cut = Render<NowPlayingPage>();

        await cut.Find("[data-now-playing-back]").ClickAsync();

        Assert.EndsWith("/listen", new Uri(navigation.Uri).AbsolutePath);
    }

    [Fact]
    public void TheReturnAddressOnlyEverHoldsAPathOnThisSite()
    {
        Assert.Equal("/listen/now-playing", NowPlayingRoute.Build(null));
        Assert.Equal("/listen/now-playing", NowPlayingRoute.Build("/listen"));
        Assert.Equal("/listen/now-playing?return=%2Fdetails%2Fwork%2F1%3Fcontext%3Dlisten", NowPlayingRoute.Build("/details/work/1?context=listen"));
        Assert.Equal("/listen", NowPlayingRoute.Sanitize("https://example.com/"));
        Assert.Equal("/listen", NowPlayingRoute.Sanitize("//example.com"));
        Assert.Equal("/listen", NowPlayingRoute.Sanitize("/\\example.com"));
        Assert.Equal("/listen", NowPlayingRoute.Sanitize("/listen/now-playing?return=/x"));
        Assert.True(NowPlayingRoute.IsNowPlaying("http://localhost:5016/listen/now-playing?return=%2F"));
        Assert.False(NowPlayingRoute.IsNowPlaying("http://localhost:5016/listen/nowhere"));
    }

    // ---- The old pop-out is gone ----

    [Fact]
    public void TheOldPopOutPageAndItsRouteAreGone()
    {
        var pages = Directory.EnumerateFiles(Path.Combine(RepoRoot, "src", "MediaEngine.Web"), "*.razor", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .ToList();

        Assert.DoesNotContain(pages, path => File.ReadAllText(path).Contains("@page \"/listen/player-popup\"", StringComparison.Ordinal));
        Assert.False(File.Exists(Path.Combine(RepoRoot, "src/MediaEngine.Web/Components/Pages/ListenPlayerPopupPage.razor")));
        Assert.False(File.Exists(Path.Combine(RepoRoot, "src/MediaEngine.Web/Components/Listen/PlaybackDesktopScene.razor")));
        Assert.False(File.Exists(Path.Combine(RepoRoot, "src/MediaEngine.Web/Components/Player/PlaybackDesktopScene.razor")));
        Assert.Contains(pages, path => File.ReadAllText(path).Contains("@page \"/listen/now-playing\"", StringComparison.Ordinal));
    }

    // ---- The bar, the cover and the navbar icon open the page ----

    [Fact]
    public async Task TheNowPlayingToolTheCoverAndTheNavbarIconAllOpenThePageAndRememberWhereYouWere()
    {
        AddServices(Song("Current", "Artist"), Song("Next", "Artist"));
        var navigation = Services.GetRequiredService<NavigationManager>();
        Render<AppPopoverHost>();
        var bar = Render<ListenNowPlayingBar>();
        var navbar = Render<NowPlayingNavButton>();

        navigation.NavigateTo("/listen/music");
        await bar.Find("[data-playback-zone='tools'] button[aria-label='Now playing']").ClickAsync();
        AssertOnThePage(navigation, "/listen/music");

        navigation.NavigateTo("/listen/audiobooks");
        await bar.Find("button[aria-label='Open Now Playing']").ClickAsync();
        AssertOnThePage(navigation, "/listen/audiobooks");

        navigation.NavigateTo("/watch");
        await navbar.Find("button").ClickAsync();
        AssertOnThePage(navigation, "/watch");
    }

    [Fact]
    public async Task TheNowPlayingToolIsSelectedWhileThePageIsOpenAndPressingItAgainGoesBack()
    {
        var playback = AddServices(Song("Current", "Artist"), Song("Next", "Artist"));
        var navigation = Services.GetRequiredService<NavigationManager>();
        Render<AppPopoverHost>();
        var bar = Render<ListenNowPlayingBar>();
        navigation.NavigateTo("/listen/music");
        const string tool = "[data-playback-zone='tools'] button[aria-label='Now playing']";
        Assert.DoesNotContain("is-selected", bar.Find(tool).ClassName);
        Assert.Equal("false", bar.Find(tool).GetAttribute("aria-pressed"));

        await bar.Find(tool).ClickAsync();

        // The page is a place, not a popup: the tool is pressed and does not claim to expand anything.
        bar.WaitForAssertion(() =>
        {
            Assert.Contains("is-selected", bar.Find(tool).ClassName);
            Assert.Equal("true", bar.Find(tool).GetAttribute("aria-pressed"));
            Assert.Null(bar.Find(tool).GetAttribute("aria-expanded"));
        });

        await bar.Find(tool).ClickAsync();

        bar.WaitForAssertion(() => Assert.DoesNotContain("is-selected", bar.Find(tool).ClassName));
        Assert.EndsWith("/listen/music", new Uri(navigation.Uri).AbsolutePath);
        Assert.True(playback.IsPlaying);
    }

    [Fact]
    public async Task TheBarsQueueToolChoosesThePagesQueueTabInsteadOfOpeningASecondPanel()
    {
        AddServices(Song("Current", "Artist"), Song("Next", "Artist"));
        var navigation = Services.GetRequiredService<NavigationManager>();
        Render<AppPopoverHost>();
        var bar = Render<ListenNowPlayingBar>();
        navigation.NavigateTo(NowPlayingRoute.Build("/listen/music"));
        var page = Render<NowPlayingPage>();
        Assert.Equal("lyrics", page.Find("[data-now-playing-panel]").GetAttribute("data-now-playing-panel"));

        await bar.Find("[data-playback-zone='tools'] button[aria-label='Queue']").ClickAsync();

        page.WaitForAssertion(() => Assert.Equal("queue", page.Find("[data-now-playing-panel]").GetAttribute("data-now-playing-panel")));
        Assert.Empty(bar.FindAll(".listen-player-panel"));
    }

    // ---- A4: opening Now playing, closing it and going fullscreen never changes the session ----

    [Fact]
    public async Task OpeningAndClosingNowPlayingAndTogglingFullscreenLeaveAMusicSessionUntouched()
    {
        var playback = AddServices(Song("One", "Artist"), Song("Two", "Artist"), Song("Three", "Artist"));
        await playback.SetPlaybackRateAsync(1.25);
        playback.UpdateTransportState(currentTimeSeconds: 73, isPlaying: true, volume: 0.35);
        var before = SessionFingerprint(playback);

        await OpenCloseAndToggleFullscreenAsync(playback);

        Assert.Equal(before, SessionFingerprint(playback));
        Assert.Equal(3, playback.Queue.Count);
        Assert.Equal(1.25, playback.PlaybackRate);
        Assert.Equal(0.35, playback.Volume);
        Assert.Equal(73, playback.CurrentTimeSeconds);
    }

    [Fact]
    public async Task OpeningAndClosingNowPlayingAndTogglingFullscreenLeaveAnAudiobookSessionWithItsSleepTimerUntouched()
    {
        var asset = Guid.NewGuid();
        var profile = Guid.NewGuid();
        var book = Book("The Way Beyond", asset, position: 100);
        var playback = AddServices(book, audiobook: true, position: 100, duration: 240, profile: profile,
            sleepTimer: new AudiobookSleepTimerStateDto
            {
                ProfileId = profile,
                WorkId = book.AudiobookWorkId!.Value,
                BoundAssetId = asset,
                PlaybackRequestVersion = 1,
                Mode = AudiobookSleepTimerModes.Timer,
                ChosenMinutes = 30,
                DeadlineUtc = DateTimeOffset.UtcNow.AddMinutes(30),
                TimerSessionId = Guid.NewGuid(),
                TimerGeneration = 1,
            });
        await playback.SetPlaybackRateAsync(1.5);
        playback.UpdateTransportState(volume: 0.5);
        var deadline = playback.SleepTimerState.DeadlineUtc;
        Assert.NotNull(deadline);
        var before = SessionFingerprint(playback);

        await OpenCloseAndToggleFullscreenAsync(playback);

        Assert.Equal(before, SessionFingerprint(playback));
        Assert.Equal(deadline, playback.SleepTimerState.DeadlineUtc);
        Assert.Equal(AudiobookSleepTimerModes.Timer, playback.SleepTimerState.Mode);
        Assert.Equal(1.5, playback.PlaybackRate);
        Assert.Equal(100, playback.CurrentTimeSeconds);
    }

    private async Task OpenCloseAndToggleFullscreenAsync(PlaybackSessionController playback)
    {
        var navigation = Services.GetRequiredService<NavigationManager>();
        Render<AppPopoverHost>();
        var bar = Render<ListenNowPlayingBar>();
        navigation.NavigateTo("/listen/music");

        // Open the page and use it, then close it.
        await bar.InvokeAsync(() => navigation.NavigateTo(NowPlayingRoute.Build("/listen/music")));
        var page = Render<NowPlayingPage>();
        Assert.NotEmpty(page.FindAll("[data-now-playing-page]"));
        // The page and the bar share one media element: the page adds none, and the bar still has exactly the one.
        Assert.Empty(page.FindAll("audio"));
        Assert.Single(bar.FindAll("audio#listen-audio-engine"));
        await page.Find("[data-now-playing-back]").ClickAsync();
        bar.WaitForAssertion(() => Assert.EndsWith("/listen/music", new Uri(navigation.Uri).AbsolutePath));

        // Fullscreen belongs to the shell's video bar; toggling it (and the presentation surface it leads to) must not touch an audio session.
        var tools = new List<PlaybackControlKey>();
        var shell = Render<PlaybackShell>(parameters => parameters
            .Add(s => s.Media, PlaybackBarMedia.Movie)
            .Add(s => s.Identity, PlaybackBarIdentity.Build(PlaybackBarMedia.Movie, Song("Film", "Studio"), chapterTitle: null))
            .Add(s => s.State, new PlaybackBarState(CanPrevious: true, CanNext: true))
            .Add(s => s.OnTool, EventCallback.Factory.Create<PlaybackControlActivation>(this, activation => tools.Add(activation.Control.Key))));
        await shell.Find("button[aria-label='Fullscreen']").ClickAsync();
        await shell.InvokeAsync(() => playback.SetPresentationSurface(PlaybackPresentationSurface.Fullscreen));
        await shell.InvokeAsync(() => playback.SetPresentationSurface(PlaybackPresentationSurface.Docked));
        Assert.Contains(PlaybackControlKey.Fullscreen, tools);

        Assert.Single(bar.FindAll("audio#listen-audio-engine"));
        Assert.Single(bar.FindAll("audio"));
    }

    private static string SessionFingerprint(PlaybackSessionController playback) => string.Join('|',
        playback.CurrentItem?.QueueEntryId,
        string.Join(',', playback.Queue.Select(item => item.QueueEntryId)),
        playback.CurrentIndex,
        playback.CurrentTimeSeconds,
        playback.Volume,
        playback.IsMuted,
        playback.PlaybackRate,
        playback.IsPlaying,
        playback.ShuffleEnabled,
        playback.RepeatMode,
        playback.PlaybackRequestVersion,
        playback.SleepTimerState.Mode,
        playback.SleepTimerState.ChosenMinutes,
        playback.SleepTimerState.DeadlineUtc,
        playback.SleepTimerState.TimerSessionId);

    private static void AssertOnThePage(NavigationManager navigation, string cameFrom)
    {
        var uri = new Uri(navigation.Uri);
        Assert.Equal(NowPlayingRoute.Path, uri.AbsolutePath);
        Assert.Equal(cameFrom, NowPlayingRoute.ReturnTarget(navigation, null));
        navigation.NavigateTo(cameFrom);
    }

    // ---- The bar follows the page's tab ----

    private const string LyricsTool = "[data-playback-zone='tools'] button[aria-label='Lyrics']";
    private const string QueueTool = "[data-playback-zone='tools'] button[aria-label='Queue']";
    private const string ChaptersTool = "[data-playback-zone='tools'] button[aria-label='Chapters']";
    private const string NowPlayingTool = "[data-playback-zone='tools'] button[aria-label='Now playing']";

    [Fact]
    public async Task WhenThePageOpensOnLyricsOnlyTheNowPlayingToolIsPressedAndNotTheQueueTool()
    {
        AddServices(Song("Current", "Artist"), Song("Next", "Artist"));
        var navigation = Services.GetRequiredService<NavigationManager>();
        Render<AppPopoverHost>();
        var bar = Render<ListenNowPlayingBar>();
        navigation.NavigateTo("/listen/music");

        await bar.InvokeAsync(() => navigation.NavigateTo(NowPlayingRoute.Build("/listen/music")));
        var page = Render<NowPlayingPage>();

        Assert.Equal("lyrics", page.Find("[data-now-playing-panel]").GetAttribute("data-now-playing-panel"));
        bar.WaitForAssertion(() => Assert.Equal("true", bar.Find(NowPlayingTool).GetAttribute("aria-pressed")));
        Assert.Equal("false", bar.Find(QueueTool).GetAttribute("aria-pressed"));
        Assert.DoesNotContain("is-selected", bar.Find(QueueTool).ClassName);
        Assert.DoesNotContain("is-selected", bar.Find(LyricsTool).ClassName);
    }

    [Fact]
    public async Task ChoosingATabOnThePagePressesThatToolInTheBarAndOnlyThatOne()
    {
        AddServices(Song("Current", "Artist"), Song("Next", "Artist"));
        var navigation = Services.GetRequiredService<NavigationManager>();
        Render<AppPopoverHost>();
        var bar = Render<ListenNowPlayingBar>();
        navigation.NavigateTo("/listen/music");
        await bar.InvokeAsync(() => navigation.NavigateTo(NowPlayingRoute.Build("/listen/music")));
        var page = Render<NowPlayingPage>();

        await page.Find("[role='tab'][data-playback-list-tab='queue']").ClickAsync();

        bar.WaitForAssertion(() =>
        {
            Assert.Equal("true", bar.Find(QueueTool).GetAttribute("aria-pressed"));
            Assert.Equal("false", bar.Find(LyricsTool).GetAttribute("aria-pressed"));
            Assert.Equal("true", bar.Find(NowPlayingTool).GetAttribute("aria-pressed"));
        });

        await page.Find("[role='tab'][data-playback-list-tab='lyrics']").ClickAsync();

        bar.WaitForAssertion(() =>
        {
            Assert.Equal("true", bar.Find(LyricsTool).GetAttribute("aria-pressed"));
            Assert.Equal("false", bar.Find(QueueTool).GetAttribute("aria-pressed"));
        });
    }

    [Fact]
    public async Task OnTheAudiobookPageTheChaptersToolIsPressedOnlyWhileTheChaptersTabShowsAndNeverForAbout()
    {
        var asset = Guid.NewGuid();
        AddServices(Book("The Way Beyond", asset, position: 100), audiobook: true, position: 100, duration: 240);
        var navigation = Services.GetRequiredService<NavigationManager>();
        Render<AppPopoverHost>();
        var bar = Render<ListenNowPlayingBar>();
        navigation.NavigateTo("/listen/audiobooks");
        await bar.InvokeAsync(() => navigation.NavigateTo(NowPlayingRoute.Build("/listen/audiobooks")));
        var page = Render<NowPlayingPage>();
        bar.WaitForAssertion(() => Assert.Equal("true", bar.Find(NowPlayingTool).GetAttribute("aria-pressed")));

        // Opening on Chapters: the page shows Chapters, but nothing was chosen yet, so only Now playing is pressed.
        Assert.Equal("chapters", page.Find("[data-now-playing-panel]").GetAttribute("data-now-playing-panel"));
        Assert.Equal("false", bar.Find(ChaptersTool).GetAttribute("aria-pressed"));

        await page.Find("[role='tab'][data-playback-list-tab='about']").ClickAsync();
        bar.WaitForAssertion(() => Assert.Equal("false", bar.Find(ChaptersTool).GetAttribute("aria-pressed")));
        Assert.Equal("true", bar.Find(NowPlayingTool).GetAttribute("aria-pressed"));

        await page.Find("[role='tab'][data-playback-list-tab='chapters']").ClickAsync();
        bar.WaitForAssertion(() => Assert.Equal("true", bar.Find(ChaptersTool).GetAttribute("aria-pressed")));
    }

    [Fact]
    public async Task LeavingThePageReleasesTheBarsTabTools()
    {
        AddServices(Song("Current", "Artist"), Song("Next", "Artist"));
        var navigation = Services.GetRequiredService<NavigationManager>();
        Render<AppPopoverHost>();
        var bar = Render<ListenNowPlayingBar>();
        navigation.NavigateTo("/listen/music");
        await bar.InvokeAsync(() => navigation.NavigateTo(NowPlayingRoute.Build("/listen/music")));
        var page = Render<NowPlayingPage>();
        await page.Find("[role='tab'][data-playback-list-tab='queue']").ClickAsync();
        bar.WaitForAssertion(() => Assert.Equal("true", bar.Find(QueueTool).GetAttribute("aria-pressed")));

        // Leaving the address removes the page from the screen, which disposes it.
        await page.InvokeAsync(() => ((IDisposable)page.Instance).Dispose());

        Assert.Null(Services.GetRequiredService<ListenContextSidebarState>().NowPlayingTab);
        bar.WaitForAssertion(() => Assert.Equal("false", bar.Find(QueueTool).GetAttribute("aria-pressed")));
    }

    // ---- A details window on top of the page ----

    [Fact]
    public async Task PressingTheNowPlayingToolUnderAnOpenDetailsWindowReturnsToThePageBeforeNowPlaying()
    {
        AddServices(Song("Current", "Artist"), Song("Next", "Artist"));
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/listen/music");
        var overlay = NewOverlay(navigation);
        Render<AppPopoverHost>();
        var bar = Render<ListenNowPlayingBar>(parameters => parameters.AddCascadingValue(overlay));
        await bar.InvokeAsync(() => navigation.NavigateTo(NowPlayingRoute.Build("/listen/music")));
        bar.WaitForAssertion(() => Assert.Equal("true", bar.Find(NowPlayingTool).GetAttribute("aria-pressed")));

        // The artist's details open as a window over the Now playing page; the address bar now names the artist, not the page.
        await bar.InvokeAsync(() => navigation.NavigateTo($"/details/person/{Guid.NewGuid():D}"));
        Assert.True(overlay.IsOpen);
        bar.WaitForAssertion(() => Assert.Equal("true", bar.Find(NowPlayingTool).GetAttribute("aria-pressed")));

        await bar.Find(NowPlayingTool).ClickAsync();

        bar.WaitForAssertion(() => Assert.Equal("/listen/music", new Uri(navigation.Uri).AbsolutePath));
    }

    [Fact]
    public async Task ThePagesCloseUnderAnOpenDetailsWindowGoesToThePageBeforeNowPlayingNotToTheArtist()
    {
        AddServices(Song("Current", "Artist"), Song("Next", "Artist"));
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/listen/music");
        var overlay = NewOverlay(navigation);
        navigation.NavigateTo(NowPlayingRoute.Build("/listen/music"));
        var page = Render<NowPlayingPage>(parameters => parameters.AddCascadingValue(overlay));
        navigation.NavigateTo($"/details/person/{Guid.NewGuid():D}");
        Assert.True(overlay.IsOpen);

        await page.Find("[data-now-playing-close]").ClickAsync();

        Assert.Equal("/listen/music", new Uri(navigation.Uri).AbsolutePath);
    }

    [Fact]
    public void WhenPlaybackEndsUnderAnOpenDetailsWindowThePageLeavesOnlyAfterTheWindowCloses()
    {
        var playback = AddServices(Song("Current", "Artist"));
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/listen/music");
        var overlay = NewOverlay(navigation);
        navigation.NavigateTo(NowPlayingRoute.Build("/listen/music"));
        var page = Render<NowPlayingPage>(parameters => parameters.AddCascadingValue(overlay));
        var details = $"/details/person/{Guid.NewGuid():D}";
        navigation.NavigateTo(details);
        Assert.True(overlay.IsOpen);

        page.InvokeAsync(playback.ClosePlayer);

        // The details window stays; the page shows its empty state in the meantime.
        Assert.Equal(details, new Uri(navigation.Uri).AbsolutePath);
        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll("[data-now-playing-empty]")));

        navigation.NavigateTo(NowPlayingRoute.Build("/listen/music"));

        page.WaitForAssertion(() => Assert.Equal("/listen/music", new Uri(navigation.Uri).AbsolutePath));
    }

    // ---- The navbar icon uses the bar's own rule ----

    [Fact]
    public async Task TheNavbarIconOnANarrowBarViewportTogglesThePhoneNowPlayingSurfaceInsteadOfTheDesktopPage()
    {
        var playback = AddServices(Song("Current", "Artist"), Song("Next", "Artist"));
        var navigation = Services.GetRequiredService<NavigationManager>();
        Render<AppPopoverHost>();
        var bar = Render<ListenNowPlayingBar>();
        var navbar = Render<NowPlayingNavButton>();
        navigation.NavigateTo("/listen/music");
        await bar.InvokeAsync(() => bar.Instance.SetAudioPresentationViewport(390));

        await navbar.Find("button").ClickAsync();

        bar.WaitForAssertion(() => Assert.Equal(PlaybackPresentationSurface.NowPlaying, playback.PresentationSurface));
        Assert.Equal("/listen/music", new Uri(navigation.Uri).AbsolutePath);
    }

    [Fact]
    public async Task TheNavbarIconRunsTheBarsStaleOverlayResetWhenItOpensThePage()
    {
        var playback = AddServices(Song("Current", "Artist"), Song("Next", "Artist"));
        var navigation = Services.GetRequiredService<NavigationManager>();
        Render<AppPopoverHost>();
        var bar = Render<ListenNowPlayingBar>();
        var navbar = Render<NowPlayingNavButton>();
        navigation.NavigateTo("/listen/music");
        await bar.InvokeAsync(() => bar.Instance.SetAudioPresentationViewport(390));
        await bar.InvokeAsync(() => playback.SetPresentationSurface(PlaybackPresentationSurface.NowPlaying));
        await bar.InvokeAsync(() => bar.Instance.SetAudioPresentationViewport(1280));

        await navbar.Find("button").ClickAsync();

        // A phone overlay left over from a narrower window is dropped as the page opens.
        bar.WaitForAssertion(() => Assert.Equal(NowPlayingRoute.Path, new Uri(navigation.Uri).AbsolutePath));
        Assert.NotEqual(PlaybackPresentationSurface.NowPlaying, playback.PresentationSurface);
    }

    // ---- Starting media from a details window on a phone or a tablet (issue 705; unified player v1 D6) ----

    [Fact]
    public async Task OnAPhoneAnAudiobookStartedFromADetailsWindowClosesTheWindowShowsTheExpandedPlayerAndCollapsingReturnsToTheWindow()
    {
        var asset = Guid.NewGuid();
        var book = Book("The Way Beyond", asset, position: 0);
        var playback = AddServices(book, audiobook: true, position: 0, duration: 240);
        Services.AddSingleton(provider => NewOverlayWithBack(provider.GetRequiredService<NavigationManager>(), "/listen/audiobooks"));
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/listen/audiobooks");
        var overlay = Services.GetRequiredService<DetailsOverlayState>();
        Render<AppPopoverHost>();
        var bar = Render<ListenNowPlayingBar>(parameters => parameters.AddCascadingValue(overlay));
        await bar.InvokeAsync(() => bar.Instance.SetAudioPresentationViewport(390));
        var details = $"/details/work/{book.AudiobookWorkId!.Value:D}";
        await bar.InvokeAsync(() => navigation.NavigateTo(details));
        Assert.True(overlay.IsOpen);

        // The person presses Play on the details window: a new playback request starts.
        await bar.InvokeAsync(() => { playback.ReservePlaybackRequest(); playback.SetAudioCapabilities(true, true); });

        bar.WaitForAssertion(() =>
        {
            Assert.False(overlay.IsOpen);
            Assert.Equal(PlaybackPresentationSurface.NowPlaying, playback.PresentationSurface);
            Assert.NotEmpty(bar.FindAll("[data-playback-full]"));
        });
        Assert.Equal("/listen/audiobooks", new Uri(navigation.Uri).AbsolutePath);

        await bar.Find("[data-playback-full-collapse]").ClickAsync();

        bar.WaitForAssertion(() => Assert.Equal(details, new Uri(navigation.Uri).AbsolutePath));
        Assert.Equal(PlaybackPresentationSurface.Docked, playback.PresentationSurface);
    }

    [Fact]
    public async Task OnATabletAnAudiobookStartedFromADetailsWindowClosesTheWindowAndOpensTheNowPlayingPage()
    {
        var asset = Guid.NewGuid();
        var book = Book("The Way Beyond", asset, position: 0);
        var playback = AddServices(book, audiobook: true, position: 0, duration: 240);
        Services.AddSingleton(provider => NewOverlay(provider.GetRequiredService<NavigationManager>(), "/listen/audiobooks"));
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/listen/audiobooks");
        var overlay = Services.GetRequiredService<DetailsOverlayState>();
        Render<AppPopoverHost>();
        var bar = Render<ListenNowPlayingBar>(parameters => parameters.AddCascadingValue(overlay));
        await bar.InvokeAsync(() => bar.Instance.SetAudioPresentationViewport(800));
        await bar.InvokeAsync(() => navigation.NavigateTo($"/details/work/{book.AudiobookWorkId!.Value:D}"));
        Assert.True(overlay.IsOpen);

        await bar.InvokeAsync(() => { playback.ReservePlaybackRequest(); playback.SetAudioCapabilities(true, true); });

        bar.WaitForAssertion(() =>
        {
            Assert.False(overlay.IsOpen);
            Assert.Equal(NowPlayingRoute.Path, new Uri(navigation.Uri).AbsolutePath);
        });
        Assert.Empty(bar.FindAll("[data-playback-full]"));
    }

    [Fact]
    public async Task OnAPhoneMusicStartedFromADetailsWindowLeavesTheWindowAloneBecauseTheBarNeverCoversThePage()
    {
        var playback = AddServices(Song("Current", "Artist"), Song("Next", "Artist"));
        Services.AddSingleton(provider => NewOverlay(provider.GetRequiredService<NavigationManager>(), "/listen/music"));
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/listen/music");
        var overlay = Services.GetRequiredService<DetailsOverlayState>();
        Render<AppPopoverHost>();
        var bar = Render<ListenNowPlayingBar>(parameters => parameters.AddCascadingValue(overlay));
        await bar.InvokeAsync(() => bar.Instance.SetAudioPresentationViewport(390));
        await bar.InvokeAsync(() => navigation.NavigateTo($"/details/musicalbum/{Guid.NewGuid():D}"));

        await bar.InvokeAsync(() => { playback.ReservePlaybackRequest(); playback.SetAudioCapabilities(true, true); });

        Assert.True(overlay.IsOpen);
        Assert.NotEqual(PlaybackPresentationSurface.NowPlaying, playback.PresentationSurface);
        Assert.Empty(bar.FindAll("[data-playback-full]"));
    }

    // ---- The page keeps room for the bar (unified player v2 D26) ----

    [Fact]
    public async Task OnAPhoneTheCollapsedBarTakesItsOwnRowOfThePageWhileShownAndNoRowWhenHidden()
    {
        var playback = AddServices(Song("Current", "Artist"), Song("Next", "Artist"));
        Render<AppPopoverHost>();
        var bar = Render<ListenNowPlayingBar>();
        await bar.InvokeAsync(() => bar.Instance.SetAudioPresentationViewport(390));

        // Shown: the bar is a block in the dock row (the page frame gives that row its height), collapsed to its 56 px form.
        bar.WaitForAssertion(() => Assert.Single(bar.FindAll(".listen-player-shell .playback-shell.playback-shell--compact")));
        Assert.Empty(bar.FindAll("[style*='position:fixed'],[style*='position: fixed']"));

        // Hidden: closing the player removes the bar and the shell that holds its row, so the frame reserves nothing.
        await bar.InvokeAsync(playback.ClosePlayer);

        bar.WaitForAssertion(() => Assert.Empty(bar.FindAll(".listen-player-shell")));
        Assert.Empty(bar.FindAll(".playback-shell"));
    }

    [Theory]
    [InlineData("phone", 844, true)]
    [InlineData("desktop", 600, true)]
    [InlineData("desktop", 720, true)]
    [InlineData("desktop", 721, false)]
    [InlineData("tablet", 800, false)]
    [InlineData("desktop", 1280, false)]
    public async Task TheBarIsCollapsedForAPhoneFormFactorOrAWindowOf720PixelsOrLess(string formFactor, int width, bool collapsed)
    {
        AddServices(Song("Current", "Artist"), Song("Next", "Artist"));
        await Services.GetRequiredService<DeviceContextService>().SwitchDeviceAsync(new DeviceContextSnapshot(formFactor == "phone" ? "mobile" : "web", formFactor, formFactor != "desktop"));
        Render<AppPopoverHost>();
        var bar = Render<ListenNowPlayingBar>();

        await bar.InvokeAsync(() => bar.Instance.SetAudioPresentationViewport(width));

        bar.WaitForAssertion(() => Assert.Equal(collapsed, bar.FindAll(".playback-shell--compact").Count == 1));
    }

    [Fact]
    public async Task WhenTheDeviceStopsBeingAPhoneWhileTheExpandedPlayerIsOpenThePlayerClosesCleanly()
    {
        var playback = AddServices(Song("Current", "Artist"), Song("Next", "Artist"));
        var device = Services.GetRequiredService<DeviceContextService>();
        await device.SwitchDeviceAsync(new DeviceContextSnapshot("mobile", "phone", true));
        Render<AppPopoverHost>();
        var bar = Render<ListenNowPlayingBar>();
        await bar.InvokeAsync(() => playback.SetPresentationSurface(PlaybackPresentationSurface.NowPlaying));
        bar.WaitForAssertion(() => Assert.Single(bar.FindAll("[data-playback-full]")));

        await bar.InvokeAsync(() => device.SwitchDeviceAsync(new DeviceContextSnapshot("web", "desktop", false)));

        bar.WaitForAssertion(() => Assert.Empty(bar.FindAll("[data-playback-full]")));
        Assert.NotEqual(PlaybackPresentationSurface.NowPlaying, playback.PresentationSurface);
    }

    [Fact]
    public async Task TheLyricsSheetOfThePhonesNowPlayingOffersNoOpenNowPlayingButtonButTheDesktopDockDoes()
    {
        var playback = AddServices(Song("Current", "Artist"), Song("Next", "Artist"));
        Render<AppPopoverHost>();
        var bar = Render<ListenNowPlayingBar>();
        await bar.InvokeAsync(() => bar.Instance.SetAudioPresentationViewport(1280));
        await bar.InvokeAsync(() => bar.FindComponent<PlaybackLyricsPanel>().Instance.OpenAsync());
        bar.WaitForAssertion(() => Assert.Single(bar.FindAll("button[aria-label='Open Now playing']")));

        await bar.InvokeAsync(() => bar.FindComponent<PlaybackLyricsPanel>().Instance.OpenAsync());
        await bar.InvokeAsync(() => bar.Instance.SetAudioPresentationViewport(390));
        await bar.InvokeAsync(() => playback.SetPresentationSurface(PlaybackPresentationSurface.NowPlaying));
        bar.WaitForAssertion(() => Assert.Single(bar.FindAll("[data-playback-full]")));
        await bar.InvokeAsync(() => bar.FindComponent<PlaybackLyricsPanel>().Instance.OpenAsync());

        bar.WaitForAssertion(() => Assert.NotEmpty(bar.FindAll("[data-playback-lyrics-panel],.playback-panel")));
        Assert.Empty(bar.FindAll("button[aria-label='Open Now playing']"));
    }

    [Fact]
    public async Task OnATabletTheBarKeepsTheDesktopRowWithItsToolsAndNoCollapsedForm()
    {
        AddServices(Song("Current", "Artist"), Song("Next", "Artist"));
        Render<AppPopoverHost>();
        var bar = Render<ListenNowPlayingBar>();
        await bar.InvokeAsync(() => bar.Instance.SetAudioPresentationViewport(800));

        bar.WaitForAssertion(() => Assert.Single(bar.FindAll(".playback-shell")));
        Assert.Empty(bar.FindAll(".playback-shell--compact"));
        Assert.NotEmpty(bar.FindAll("[data-playback-zone='tools'] button"));
        Assert.NotEmpty(bar.FindAll("[data-playback-identity-actions]"));
    }

    // ---- Small page details ----

    [Fact]
    public void WhenNothingIsPlayingThePageShowsItsEmptyStateAsAHeadingThatCanTakeFocus()
    {
        var api = EngineApiClientStub.CreateDefault();
        var playback = new PlaybackSessionController(null!, null!, preferences: new TestPreferences(Guid.NewGuid()));
        Services.AddSingleton(api);
        Services.AddSingleton(playback);
        Services.AddSingleton(new ListenContextSidebarState(new MemorySidebarPreferences()));
        Services.AddSingleton<IPlaybackCommandSink>(provider => new DirectPlaybackCommandSink(new ListenPlaybackCommandOwner(provider, playback)));
        Services.AddSingleton(new PlaybackLyricsPresenter(api));
        Services.AddSingleton(new PlaybackLyricsSelectionOwner(playback, api));
        Services.AddSingleton<IUserPlaybackPreferencesAccessor>(new TestPreferences(Guid.NewGuid()));

        var cut = Render<NowPlayingPage>();

        var empty = cut.Find("h1[data-now-playing-empty]");
        Assert.Equal("-1", empty.GetAttribute("tabindex"));
        Assert.Single(cut.FindAll("[data-now-playing-back]"));
    }

    [Fact]
    public void TheByLinesComeFromFormatStringsWithThePeopleInPlaceOfTheNumberSlot()
    {
        var asset = Guid.NewGuid();
        AddServices(Book("The Way Beyond", asset, position: 100), audiobook: true, position: 100, duration: 240);

        var cut = Render<NowPlayingPage>();

        Assert.Equal("By Maya Ellis", cut.Find("[data-now-playing-authors]").TextContent.Trim());
        Assert.Equal("Narrated by Sam Rowe", cut.Find("[data-now-playing-narrators]").TextContent.Trim());
        var resx = File.ReadAllText(Path.Combine(RepoRoot, "src/MediaEngine.Web/Resources/SharedStrings.resx"));
        Assert.Contains("<value>By {0}</value>", resx, StringComparison.Ordinal);
        Assert.Contains("<value>Narrated by {0}</value>", resx, StringComparison.Ordinal);
    }

    [Fact]
    public void TheBarsOpenAndCloseNowPlayingLabelsLiveInTheSharedStrings()
    {
        var resx = File.ReadAllText(Path.Combine(RepoRoot, "src/MediaEngine.Web/Resources/SharedStrings.resx"));
        var bar = File.ReadAllText(Path.Combine(RepoRoot, "src/MediaEngine.Web/Components/Player/ListenNowPlayingBar.razor"));

        Assert.Contains("name=\"ListenNowPlayingBar_OpenNowPlaying\"", resx, StringComparison.Ordinal);
        Assert.Contains("name=\"ListenNowPlayingBar_CloseNowPlaying\"", resx, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Open Now Playing\"", bar, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Close Now Playing\"", bar, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheCoverStartsItsRetriesAgainWhenTheSongChanges()
    {
        var playback = AddServices(Song("First", "Artist"), Song("Second", "Artist"));
        var cut = Render<NowPlayingPage>();
        await cut.Find("[data-now-playing-cover] img").TriggerEventAsync("onerror", new Microsoft.AspNetCore.Components.Web.ErrorEventArgs());
        cut.WaitForAssertion(() => Assert.Contains("retry=1", cut.Find("[data-now-playing-cover] img").GetAttribute("src")), TimeSpan.FromSeconds(5));

        await cut.InvokeAsync(() => playback.RestoreState(new ListenPlaybackSnapshot
        {
            ProfileId = Services.GetRequiredService<IUserPlaybackPreferencesAccessor>().ActiveProfileId!.Value,
            Queue = [.. playback.Queue],
            CurrentIndex = 1,
            Experience = PlayerExperienceModes.Music,
            DurationSeconds = 200,
            IsPlaying = true,
            PlaybackRequestVersion = 2,
        }));

        cut.WaitForAssertion(() =>
        {
            Assert.Equal("Second", cut.Find("h1").TextContent);
            Assert.DoesNotContain("retry=", cut.Find("[data-now-playing-cover] img").GetAttribute("src"));
        });
    }

    // ---- Setup ----

    private static ListenQueueItem Song(string title, string artist, string? album = null, string? year = null) => new()
    {
        QueueEntryId = Guid.NewGuid(),
        WorkId = Guid.NewGuid(),
        AssetId = Guid.NewGuid(),
        MediaType = "Music",
        Title = title,
        Subtitle = artist,
        Album = album,
        Year = year,
        Duration = "3:24",
        CoverUrl = "/api/v1/images/test?size=m",
    };

    private static ListenQueueItem Book(string title, Guid asset, double position) => new()
    {
        QueueEntryId = Guid.NewGuid(),
        WorkId = Guid.NewGuid(),
        AudiobookWorkId = Guid.NewGuid(),
        AssetId = asset,
        MediaType = "Audiobook",
        Title = title,
        Subtitle = "Maya Ellis",
        AuthorName = "Maya Ellis",
        NarratorName = "Sam Rowe",
        Authors = [new("Maya Ellis")],
        Narrators = [new("Sam Rowe")],
        Synopsis = "A walk across the high country.",
        Year = "2024",
        Duration = "8h 12m",
        CoverUrl = "/api/v1/images/test?size=m",
        Chapters =
        [
            new() { Index = 0, AssetId = asset, Title = "The trailhead", StartSeconds = 0, EndSeconds = 60 },
            new() { Index = 1, AssetId = asset, Title = "Beyond the ridge", StartSeconds = 60, EndSeconds = 150 },
            new() { Index = 2, AssetId = asset, Title = "The valley", StartSeconds = 150, EndSeconds = 200 },
            new() { Index = 3, AssetId = asset, Title = "Home", StartSeconds = 200, EndSeconds = 240 },
        ],
    };

    private PlaybackSessionController AddServices(params ListenQueueItem[] items) => AddServices(items[0], audiobook: false, position: 0, duration: 200, queue: items);

    private PlaybackSessionController AddServices(ListenQueueItem current, bool audiobook, double position, double duration, Guid? profile = null,
        AudiobookSleepTimerStateDto? sleepTimer = null, ListenQueueItem[]? queue = null)
    {
        var profileId = profile ?? Guid.NewGuid();
        var api = EngineApiClientStub.CreateDefault();
        var playback = new PlaybackSessionController(null!, null!, preferences: new TestPreferences(profileId));
        playback.RestoreState(new ListenPlaybackSnapshot
        {
            ProfileId = profileId,
            Queue = [.. queue ?? [current]],
            CurrentIndex = 0,
            Experience = audiobook ? PlayerExperienceModes.Audiobook : PlayerExperienceModes.Music,
            CurrentTimeSeconds = position,
            DurationSeconds = duration,
            IsPlaying = true,
            PlaybackRequestVersion = 1,
            SleepTimerState = sleepTimer ?? new(),
        });
        Services.AddSingleton(api);
        Services.AddSingleton(playback);
        Services.AddSingleton(new ListenContextSidebarState(new MemorySidebarPreferences()));
        Services.AddSingleton<NowPlayingLauncher>();
        Services.AddSingleton(new PlaybackLyricsPresenter(api));
        Services.AddSingleton(new PlaybackLyricsSelectionOwner(playback, api));
        Services.AddSingleton<IUserPlaybackPreferencesAccessor>(new TestPreferences(profileId));
        Services.AddSingleton<IPlaybackCommandSink>(provider => new DirectPlaybackCommandSink(new ListenPlaybackCommandOwner(provider, playback)));
        Services.AddSingleton(new DeviceContextService(api));
        Services.AddSingleton<ListenAudioDragService>();
        Services.AddSingleton(new MediaReactionService(api));
        Services.AddSingleton(new SavedItemService(api));
        Services.AddSingleton(new PlaybackTransientToolCoordinator(playback));
        var bookmarks = new AudiobookBookmarkActionService(api);
        Services.AddSingleton(bookmarks);
        Services.AddSingleton<IAudiobookBookmarkActions>(bookmarks);
        Services.AddSingleton<IAudiobookBookmarkLeaseInvalidator>(bookmarks);
        Services.AddSingleton<ActiveProfileSessionService>(provider => new ActiveProfileSessionService(provider.GetRequiredService<IJSRuntime>(), api));
        Services.AddSingleton<UIOrchestratorService>(provider => new UIOrchestratorService(
            api, new UniverseStateContainer(), provider.GetRequiredService<ActiveProfileSessionService>(),
            new ConfigurationBuilder().Build(), NullLogger<UIOrchestratorService>.Instance));
        Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        Services.AddSingleton(provider => new PlaybackTimelineScope(provider.GetRequiredService<IJSRuntime>(), playback));
        return playback;
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MediaEngine.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("The repository root was not found.");
    }

    // The page under a details window must be one the overlay has seen, as it is in the running app.
    private static DetailsOverlayState NewOverlay(NavigationManager navigation, params string[] otherPages)
    {
        var overlay = new DetailsOverlayState(navigation, new NoHistory());
        foreach (var path in otherPages.Prepend(NowPlayingRoute.Path))
        {
            overlay.RememberPage(path, new Microsoft.AspNetCore.Components.RouteData(typeof(NowPlayingPage), new Dictionary<string, object?>()));
        }

        return overlay;
    }

    // A details window closed with the browser's Back: the history of the running app takes the address back to the page under it.
    private static DetailsOverlayState NewOverlayWithBack(NavigationManager navigation, string underlay)
    {
        var overlay = new DetailsOverlayState(navigation, new BackToPage(navigation, underlay));
        overlay.RememberPage(underlay, new Microsoft.AspNetCore.Components.RouteData(typeof(NowPlayingPage), new Dictionary<string, object?>()));
        return overlay;
    }

    private sealed class BackToPage(NavigationManager navigation, string underlay) : IDetailsOverlayHistory
    {
        public ValueTask<bool> GoBackAsync(int steps)
        {
            navigation.NavigateTo(underlay, replace: true);
            return ValueTask.FromResult(true);
        }
    }

    private sealed class NoHistory : IDetailsOverlayHistory
    {
        public ValueTask<bool> GoBackAsync(int steps) => ValueTask.FromResult(true);
    }

    private sealed class MemorySidebarPreferences : IContextSidebarPreferences
    {
        public Task<ContextSidebarLayoutDto> GetAsync(string context, CancellationToken ct = default) =>
            Task.FromResult(ContextSidebarPreferences.Default(context));

        public Task<bool> SaveAsync(string context, ContextSidebarLayoutDto layout, CancellationToken ct = default) =>
            Task.FromResult(true);
    }

    private sealed class TestPreferences(Guid profileId) : IUserPlaybackPreferencesAccessor
    {
        public Guid? ActiveProfileId => profileId;

        public Task<UserPlaybackSettingsDto?> GetAsync(CancellationToken ct = default) =>
            Task.FromResult<UserPlaybackSettingsDto?>(UserPlaybackSettingsDto.CreateDefaults(profileId));

        public void UpdateCache(UserPlaybackSettingsDto settings) { }
        public void Invalidate() { }
    }
}
