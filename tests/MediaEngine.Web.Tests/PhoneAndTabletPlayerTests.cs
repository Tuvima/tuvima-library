using Bunit;
using MediaEngine.Contracts.Playback;
using MediaEngine.Web.Components.Player;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Playback;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;

namespace MediaEngine.Web.Tests;

/// <summary>
/// The phone and the tablet (unified player v2 D26, A12; mockup screen 15). The collapsed bar, the phone's video overlay, the space the page keeps for the
/// bar, the touch targets and the tablet's sheets. The expanded player has its own tests in <see cref="PlaybackFullPlayerTests"/>.
/// </summary>
public sealed class PhoneAndTabletPlayerTests : AsyncBunitContext
{
    public PhoneAndTabletPlayerTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        Services.AddLocalization();
    }

    private static ListenQueueItem Item(PlaybackBarMedia media) => new()
    {
        WorkId = Guid.NewGuid(),
        AssetId = Guid.NewGuid(),
        MediaType = media switch { PlaybackBarMedia.Music => "Music", PlaybackBarMedia.Audiobook => "Audiobook", PlaybackBarMedia.Tv => "TV", _ => "Movie" },
        Title = media == PlaybackBarMedia.Tv ? "Shimmer and Shine" : "Stay a Little Longer",
        Subtitle = "The Midnight Coast",
        Album = "Northern Lights",
        Year = "2026",
        EpisodeTitle = "The First Wish",
        SeasonNumber = "1",
        EpisodeNumber = "1",
        AuthorName = "Maya Ellis",
    };

    private IRenderedComponent<PlaybackShell> RenderBar(PlaybackBarMedia media, bool compact = false, bool phone = false, PlaybackBarState? state = null, Action? expand = null)
    {
        var item = Item(media);
        return Render<PlaybackShell>(p => p
            .Add(x => x.Media, media)
            .Add(x => x.Compact, compact)
            .Add(x => x.Phone, phone)
            .Add(x => x.Identity, PlaybackBarIdentity.Build(media, item))
            .Add(x => x.State, state ?? new PlaybackBarState(CanPrevious: true, CanNext: true, HasChapters: true, HasEpisodes: media == PlaybackBarMedia.Tv, CanBookmark: true))
            .Add(x => x.ArtworkClick, EventCallback.Factory.Create(this, () => expand?.Invoke()))
            .Add(x => x.IdentityActions, (RenderFragment)(b => b.AddMarkupContent(0, "<button type=\"button\" data-test-favorite>Favorite</button>"))));
    }

    private static IReadOnlyList<string> TransportLabels(IRenderedComponent<PlaybackShell> cut) =>
        cut.FindAll("[data-playback-zone='transport'] button").Select(button => button.GetAttribute("aria-label")!).ToList();

    // ---- The collapsed bar (marker 1) ----

    [Fact]
    public void TheCollapsedMusicBarHasPlayAndNextAfterItsArtAndText()
    {
        var cut = RenderBar(PlaybackBarMedia.Music, compact: true);

        Assert.Equal(["Play", "Next track"], TransportLabels(cut));
        Assert.Equal("Stay a Little Longer", cut.Find(".playback-identity__title").TextContent.Trim());
        Assert.NotEmpty(cut.Find(".playback-identity__secondary").TextContent.Trim());
        Assert.NotNull(cut.Find(".playback-identity__expand .playback-identity__art"));
    }

    [Fact]
    public void TheCollapsedAudiobookBarHasPlayAndForward30Seconds()
    {
        var cut = RenderBar(PlaybackBarMedia.Audiobook, compact: true);

        Assert.Equal(["Play", "Skip forward 30 seconds"], TransportLabels(cut));
    }

    [Theory]
    [InlineData(PlaybackBarMedia.Tv)]
    [InlineData(PlaybackBarMedia.Movie)]
    public void TheCollapsedVideoBarHasPlayAlone(PlaybackBarMedia media)
    {
        var cut = RenderBar(media, compact: true);

        Assert.Equal(["Play"], TransportLabels(cut));
    }

    [Theory]
    [InlineData(PlaybackBarMedia.Music)]
    [InlineData(PlaybackBarMedia.Audiobook)]
    [InlineData(PlaybackBarMedia.Tv)]
    [InlineData(PlaybackBarMedia.Movie)]
    public void TheCollapsedBarHasNoFavoriteAndNoToolsButTheFullBarKeepsBoth(PlaybackBarMedia media)
    {
        var collapsed = RenderBar(media, compact: true);
        var full = RenderBar(media);

        Assert.Empty(collapsed.FindAll("[data-test-favorite],[data-playback-identity-actions]"));
        Assert.Empty(collapsed.FindAll("[data-playback-zone='tools']"));
        Assert.NotEmpty(full.FindAll("[data-test-favorite]"));
        Assert.NotEmpty(full.FindAll("[data-playback-zone='tools']"));
    }

    [Fact]
    public async Task TappingTheArtOrTheTextOfTheCollapsedBarExpandsThePlayerAndNeverFollowsAnIdentityLink()
    {
        var expanded = 0;
        var cut = RenderBar(PlaybackBarMedia.Music, compact: true, expand: () => expanded++);

        var button = cut.Find("button[data-playback-expand]");
        await button.ClickAsync();

        Assert.Equal(1, expanded);
        Assert.Contains("Stay a Little Longer", button.GetAttribute("aria-label"));
        Assert.Empty(cut.FindAll("a[href]"));
        Assert.NotNull(button.QuerySelector(".playback-identity__art"));
        Assert.NotNull(button.QuerySelector(".playback-identity__text"));
    }

    [Fact]
    public void TheCollapsedBarIs56PixelsTallWithA44PixelPictureAndA15PixelTitle()
    {
        var shell = RepoFile("src", "MediaEngine.Web", "Components", "Player", "PlaybackShell.razor.css");
        var identity = RepoFile("src", "MediaEngine.Web", "Components", "Player", "PlaybackIdentity.razor.css");

        var row = shell.Split(".playback-shell--compact .playback-shell__row {", StringSplitOptions.None)[1].Split('}')[0];
        Assert.Contains("min-height: 56px;", row);
        var art = identity.Split(".playback-identity--compact .playback-identity__art {", StringSplitOptions.None)[1].Split('}')[0];
        Assert.Contains("height: 44px;", art);
        Assert.Contains("width: 44px;", art);
        var title = identity.Split(".playback-identity--compact .playback-identity__title {", StringSplitOptions.None)[1].Split('}')[0];
        Assert.Contains("font-size: 15px;", title);
        var button = identity.Split(".playback-identity ::deep .playback-identity__expand {", StringSplitOptions.None)[1].Split('}')[0];
        Assert.Contains("min-height: 56px;", button);
    }

    [Fact]
    public void TheCollapsedBarKeepsTwentyFourPixelGlyphsIn44PixelTargetsWhileTheBarTokensAreBigger()
    {
        var shell = RepoFile("src", "MediaEngine.Web", "Components", "Player", "PlaybackShell.razor.css");

        var compact = shell.Split(".playback-shell--compact {", StringSplitOptions.None)[1].Split('}')[0];
        Assert.Contains("--playback-control-target: 44px;", compact);
        Assert.Contains("--playback-control-glyph: 24px;", compact);
        Assert.Contains("--playback-play-target: 48px;", compact);
        Assert.Contains("--playback-play-glyph: 32px;", compact);
        var row = shell.Split(".playback-shell__row {", StringSplitOptions.None)[1].Split('}')[0];
        Assert.Contains("min-height: 72px;", row);
    }

    // ---- Video on a phone keeps its overlay (D26) ----

    [Fact]
    public void ATvEpisodeOnAPhoneKeepsTheFiveTransportButtonsAndHasAudioSubtitlesEpisodesBookmarksFullscreenAsItsRow()
    {
        var cut = RenderBar(PlaybackBarMedia.Tv, phone: true);

        Assert.Equal(["Previous episode", "Skip back 10 seconds", "Play", "Skip forward 30 seconds", "Next episode"], TransportLabels(cut));
        var tools = cut.FindAll("[data-playback-zone='tools'] button").Select(button => button.GetAttribute("aria-label")).ToList();
        Assert.Equal(["Audio & subtitles", "Episodes", "Bookmarks", "Fullscreen"], tools);
    }

    [Fact]
    public void AMovieOnAPhoneHasNoEpisodesToolAndAFolderVideoCallsItLessons()
    {
        var movie = RenderBar(PlaybackBarMedia.Movie, phone: true);
        var lessons = RenderBar(PlaybackBarMedia.Movie, phone: true, state: new PlaybackBarState(HasEpisodes: true, IsFolderVideo: true, CanBookmark: true));

        Assert.Equal(["Audio & subtitles", "Bookmarks", "Fullscreen"], movie.FindAll("[data-playback-zone='tools'] button").Select(b => b.GetAttribute("aria-label")).ToList());
        Assert.Equal(["Audio & subtitles", "Lessons", "Bookmarks", "Fullscreen"], lessons.FindAll("[data-playback-zone='tools'] button").Select(b => b.GetAttribute("aria-label")).ToList());
    }

    [Fact]
    public void TheVideoRowOnAPhoneHasNoVolumeNoPlaybackSettingsAndNoCloseBecauseTheHeaderHoldsTheLastTwo()
    {
        var cut = RenderBar(PlaybackBarMedia.Tv, phone: true);

        var labels = cut.FindAll("[data-playback-zone='tools'] button").Select(button => button.GetAttribute("aria-label")).ToList();
        Assert.DoesNotContain("Playback settings", labels);
        Assert.DoesNotContain("Close", labels);
        Assert.Empty(cut.FindAll("[data-playback-zone='tools'] input,[data-playback-zone='tools'] [aria-label='Volume']"));
        var host = RepoFile("src", "MediaEngine.Web", "Components", "Player", "VideoPlaybackHost.razor");
        Assert.Contains("HeaderContent=\"@(IsPhoneVideo ? PhoneHeader : null)\"", host);
        Assert.Contains("@SettingsTool", host);
    }

    [Fact]
    public void TheToolRowOfEveryMediaHoldsAtMostFourTools()
    {
        var everything = new PlaybackBarState(CanPrevious: true, CanNext: true, HasChapters: true, HasEpisodes: true, CanBookmark: true, IsSleepTimerActive: true, HasQueue: true);
        foreach (var media in Enum.GetValues<PlaybackBarMedia>())
        {
            Assert.InRange(PlaybackControlCatalog.BuildPhoneTools(media, everything).Count, 1, PlaybackControlCatalog.PhoneToolRowLimit);
        }
    }

    // ---- The page keeps room for the bar (marker 1) ----

    [Fact]
    public void TheBarHasItsOwnRowOfThePageFrameUnderTheScrollingContentSoItNeverCoversTheLastItem()
    {
        var layout = RepoFile("src", "MediaEngine.Web", "Shared", "MainLayout.razor.css");

        var frame = layout.Split(".playback-app-frame {", StringSplitOptions.None)[1].Split('}')[0];
        Assert.Contains("grid-template-rows: auto minmax(0, 1fr) auto;", frame);
        Assert.Contains("overflow: hidden;", frame);
        var content = layout.Split(".playback-app-frame__content {", StringSplitOptions.None)[1].Split('}')[0];
        Assert.Contains("grid-row: 2;", content);
        var dock = layout.Split(".playback-app-frame__audio-dock {", StringSplitOptions.None)[1].Split('}')[0];
        Assert.Contains("grid-row: 3;", dock);
        Assert.DoesNotContain("position: fixed", dock);
        Assert.DoesNotContain("position: absolute", dock);
        // The bar itself is in the flow (never fixed over the page) and clears the home indicator by its own padding.
        var shell = RepoFile("src", "MediaEngine.Web", "Components", "Player", "PlaybackShell.razor.css");
        var shellRule = shell.Split(".playback-shell {", StringSplitOptions.None)[1].Split('}')[0];
        Assert.Contains("position: relative;", shellRule);
        var compact = shell.Split(".playback-shell--compact {", StringSplitOptions.None)[1].Split('}')[0];
        Assert.Contains("var(--tl-safe-area-bottom", compact);
    }

    [Fact]
    public void TheBottomNavigationAlreadyClearsTheSafeAreaSoTheBarAddsNoSecondGapAboveIt()
    {
        var layout = RepoFile("src", "MediaEngine.Web", "Shared", "MainLayout.razor.css");

        Assert.Contains("html[data-form-factor=\"phone\"] .playback-app-frame:has(.layout-shell__intent-dock) .playback-app-frame__audio-dock ::deep .playback-shell--compact", layout);
    }

    // ---- One phone rule ----

    [Theory]
    [InlineData("phone", null, true)]
    [InlineData("phone", 1280d, true)]
    [InlineData("desktop", 390d, true)]
    [InlineData("desktop", 720d, true)]
    [InlineData("desktop", 721d, false)]
    [InlineData("tablet", 800d, false)]
    [InlineData("desktop", null, false)]
    [InlineData("desktop", 0d, false)]
    public async Task APlayerIsDrawnForAPhoneWhenTheDeviceIsOneOrTheWindowIs720PixelsOrNarrower(string formFactor, double? width, bool phone)
    {
        var device = new MediaEngine.Web.Services.Theming.DeviceContextService(MediaEngine.Web.Tests.Support.EngineApiClientStub.Create(_ => { }));
        await device.SwitchDeviceAsync(new MediaEngine.Web.Services.Theming.DeviceContextSnapshot(formFactor == "phone" ? "mobile" : "web", formFactor, formFactor != "desktop"));

        Assert.Equal(phone, PlaybackFormFactor.IsPhone(device, width));
        Assert.False(PlaybackFormFactor.IsPhone(null, null));
    }

    [Fact]
    public void TheDeviceContextTellsListenersOnlyWhenTheWidthCrossesThePhoneWidth()
    {
        var device = new MediaEngine.Web.Services.Theming.DeviceContextService(MediaEngine.Web.Tests.Support.EngineApiClientStub.Create(_ => { }));
        var changes = 0;
        device.OnChanged += () => changes++;

        device.SetViewportWidth(1280);
        device.SetViewportWidth(1100);
        Assert.Equal(0, changes);
        device.SetViewportWidth(700);
        Assert.Equal(1, changes);
        device.SetViewportWidth(500);
        Assert.Equal(1, changes);
        device.SetViewportWidth(900);
        Assert.Equal(2, changes);
        Assert.Equal(900d, device.ViewportWidth);
        device.SetViewportWidth(double.NaN);
        device.SetViewportWidth(-5);
        Assert.Equal(900d, device.ViewportWidth);
    }

    // ---- Tablet and touch targets (A12) ----

    [Fact]
    public void PanelsAreBottomSheetsOnAPhoneAndOnTouchTabletsUpTo1024PixelsButAMouseKeepsThePopover()
    {
        var script = RepoFile("src", "MediaEngine.Web", "wwwroot", "js", "playback-popover.js");

        Assert.Contains("matchMedia('(max-width:720px), (max-width:1024px) and (pointer:coarse)')", script);
    }

    [Fact]
    public void EveryPlayerTouchTargetIsAtLeast44PixelsAndTheReactionButtonsStillDrawTheirFillFortyPixelsWide()
    {
        var tokens = RepoFile("src", "MediaEngine.Web", "wwwroot", "tuvima.tokens.css");
        var row = RepoFile("src", "MediaEngine.Web", "Components", "Player", "PlaybackToolRow.razor.css");
        var full = RepoFile("src", "MediaEngine.Web", "Components", "Player", "PlaybackFullPlayer.razor.css");

        Assert.Contains("--playback-control-target: 48px;", tokens);
        Assert.Contains("--playback-control-glyph: 28px;", tokens);
        Assert.Contains("--playback-play-target: 56px;", tokens);
        Assert.Contains("--playback-play-glyph: 40px;", tokens);
        var slot = row.Split(".playback-tool-row__slot {", StringSplitOptions.None)[1].Split('}')[0];
        Assert.Contains("min-height: var(--playback-control-target);", slot);
        Assert.Contains("min-width: var(--playback-control-target);", slot);
        var trigger = row.Split(".playback-tool-row ::deep .playback-popover-trigger {", StringSplitOptions.None)[1].Split('}')[0];
        Assert.Contains("height: var(--playback-control-target);", trigger);
        Assert.Contains("width: var(--playback-control-target);", trigger);
        Assert.Contains("--playback-control-target:48px;", full);
        Assert.Contains("--playback-control-glyph:28px; --playback-play-target:56px; --playback-play-glyph:40px;", full);
        var actions = full.Split(".playback-full__actions {", StringSplitOptions.None)[1].Split('}')[0];
        Assert.Contains("--playback-control-target:44px;", actions);
        Assert.DoesNotContain("--playback-control-target:40px;", full);
        // The fill is drawn 2 px inside the 44 px button, so it still looks 40 px wide as the mockup shows.
        Assert.Contains("--playback-icon-button-fill-clip:content-box;", actions);
        Assert.Contains("--playback-icon-button-fill-inset:2px;", actions);
        var button = RepoFile("src", "MediaEngine.Web", "Components", "Player", "PlaybackIconButton.razor.css");
        Assert.Contains("background-clip: var(--playback-icon-button-fill-clip, border-box);", button);
        Assert.Contains("padding: var(--playback-icon-button-fill-inset, 0);", button);
        Assert.Contains("width:44px; height:44px;", full); // The collapse chevron.
    }

    private static string RepoFile(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MediaEngine.slnx")))
        {
            directory = directory.Parent;
        }

        return LocalizedSource.ReadAllText(Path.Combine(new[] { directory?.FullName ?? throw new DirectoryNotFoundException("The repository root was not found.") }.Concat(parts).ToArray()));
    }
}
