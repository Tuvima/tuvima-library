using Bunit;
using MediaEngine.Contracts.Playback;
using MediaEngine.Web.Components.Listen;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Playback;
using MediaEngine.Web.Tests.Support;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>
/// Unified player v2 D17 and acceptance A10 (mockup screen 7): the Lyrics panel in each of its five states, its header chip, identity row, footer and
/// "..." menu, a timed line that seeks, and the Resume following pill that a hand scroll brings and Resume following takes away. The browser module that
/// follows the song is tested on its own (playback-lyrics-follow.test.mjs); here its two messages to the page are played by hand.
/// </summary>
public sealed class PlaybackLyricsPanelTests : AsyncBunitContext
{
    private readonly Guid _track = Guid.NewGuid();
    private readonly SeekSpy _commands = new();
    private BunitJSModuleInterop _lyricsModule = null!;

    public PlaybackLyricsPanelTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        _lyricsModule = JSInterop.SetupModule("./js/playback-lyrics.js");
        Services.AddLogging();
        Services.AddLocalization();
        Services.AddNativeUiServices();
    }

    private static ListenPlaybackSnapshot Snapshot() => new()
    {
        ProfileId = Guid.NewGuid(),
        CurrentIndex = 0,
        PlaybackRequestVersion = 9,
        DurationSeconds = 60,
        Queue = [new() { WorkId = Guid.NewGuid(), AssetId = Guid.NewGuid(), MediaType = "Music", Title = "Stay a Little Longer", Subtitle = "The Midnight Coast", StreamUrl = "stream://song" }],
    };

    private static TextTrackDto Track(Guid id, string provider, bool userOwned = false) =>
        new() { Id = id, Kind = "Lyrics", Provider = provider, IsUserOwned = userOwned, IsPreferred = true };

    private IEngineApiClient Api(IReadOnlyList<TextTrackDto> tracks, string? content = "[00:01.00]The city settles into blue\n[00:05.00]I leave a little light for you",
        bool findLyrics = false, Func<object?[]?, Task>? tracksRead = null) => EngineApiClientStub.Create(stub =>
    {
        stub.SetHandler(nameof(IEngineApiClient.GetTextTracksAsync), args => tracksRead is null ? Task.FromResult(tracks) : tracksRead(args).ContinueWith(_ => tracks));
        stub.SetHandler(nameof(IEngineApiClient.GetTextTrackContentAsync), _ => Task.FromResult(content));
        if (findLyrics)
        {
            stub.SetHandler(nameof(IEngineApiClient.GetPlayerPluginActionsAsync), _ =>
                Task.FromResult<IReadOnlyList<PlayerPluginActionDto>>([new() { kind = "find_lyrics", slot = "player_core" }]));
        }
    });

    private async Task<IRenderedComponent<PlaybackLyricsPanel>> OpenAsync(IEngineApiClient api, EventCallback? openNowPlaying = null, ListenPlaybackSnapshot? snapshot = null)
    {
        Services.AddSingleton(new PlaybackTransientToolCoordinator());
        Services.AddSingleton(new PlaybackSessionController(null!, null!));
        Services.AddSingleton(new PlaybackLyricsPresenter(api));
        Render<AppPopoverHost>();
        var cut = Render<PlaybackLyricsPanel>(parameters =>
        {
            parameters.Add(p => p.Snapshot, snapshot ?? Snapshot()).Add(p => p.Commands, _commands);
            if (openNowPlaying is { } callback)
            {
                parameters.Add(p => p.OnOpenNowPlaying, callback);
            }
        });
        await cut.InvokeAsync(() => cut.Instance.OpenAsync());
        cut.WaitForElement("[data-playback-lyrics]");
        return cut;
    }

    private static string View(IRenderedComponent<PlaybackLyricsPanel> cut) => cut.Find("[data-playback-lyrics]").GetAttribute("data-lyrics-view")!;

    private static string[] FooterMenuLabels(IRenderedComponent<PlaybackLyricsPanel> cut) =>
        cut.FindAll("[data-playback-lyrics-menu] [role=menuitem]").Select(row => row.TextContent.Trim()).ToArray();

    private async Task OpenFooterMenuAsync(IRenderedComponent<PlaybackLyricsPanel> cut)
    {
        var menu = cut.FindComponents<PlaybackPanel>().Single(panel => panel.Instance.Kind == "menu");
        await cut.InvokeAsync(() => menu.Instance.OpenAsync(true));
        cut.WaitForElement("[data-playback-lyrics-menu]");
    }

    // ---- The five states ----

    [Fact]
    public async Task Synced_ShowsTheSyncedChipTheSourceTheSongAndTimedLinesAsButtons()
    {
        var cut = await OpenAsync(Api([Track(_track, "local", userOwned: true)]));
        cut.WaitForAssertion(() => Assert.Equal("synced", View(cut)));

        Assert.Equal("Synced", cut.Find("[data-lyrics-chip]").TextContent.Trim());
        Assert.Equal("synced", cut.Find("[data-lyrics-chip]").GetAttribute("data-lyrics-chip"));
        Assert.Equal("Synced lyrics · Local file", cut.Find("[data-lyrics-source]").TextContent.Trim());
        Assert.Equal("Stay a Little Longer", cut.Find(".playback-lyrics__track").TextContent);
        Assert.Equal("The Midnight Coast", cut.Find(".playback-lyrics__artist").TextContent);
        var lines = cut.FindAll("button.playback-lyrics__line");
        Assert.Equal(2, lines.Count);
        Assert.Equal("Seek to The city settles into blue", lines[0].GetAttribute("aria-label"));
        Assert.Empty(cut.FindAll("[data-lyrics-resume]"));
    }

    [Fact]
    public async Task Unsynced_ShowsTheUnsyncedChipPlainTextAndNoSeekingHint()
    {
        var cut = await OpenAsync(Api([Track(_track, "lrclib")], content: "Plain words\nMore plain words"));
        cut.WaitForAssertion(() => Assert.Equal("unsynced", View(cut)));

        Assert.Equal("Unsynced", cut.Find("[data-lyrics-chip]").TextContent.Trim());
        Assert.Equal("Unsynced lyrics · LRCLIB", cut.Find("[data-lyrics-source]").TextContent.Trim());
        Assert.Empty(cut.FindAll("button.playback-lyrics__line"));
        Assert.Equal(["Plain words", "More plain words"], cut.FindAll("p.playback-lyrics__line--static").Select(line => line.TextContent));
        await cut.InvokeAsync(() => cut.FindComponent<PlaybackLyrics>().Instance.FollowChanged(true));
        Assert.Empty(cut.FindAll("[data-lyrics-resume]"));
        Assert.DoesNotContain("Select a line to jump", cut.Markup);
    }

    [Fact]
    public async Task Loading_SaysItIsLookingForLyricsAndShowsNoChip()
    {
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cut = await OpenAsync(Api([Track(_track, "lrclib")], tracksRead: _ => pending.Task));

        cut.WaitForAssertion(() => Assert.Equal("loading", View(cut)));
        Assert.Equal("Looking for lyrics…", cut.Find("[data-lyrics-state=loading]").TextContent.Trim());
        Assert.Empty(cut.FindAll("[data-lyrics-chip]"));
        pending.SetResult();
        cut.WaitForAssertion(() => Assert.Equal("synced", View(cut)));
    }

    [Fact]
    public async Task Unavailable_IsOneLineWithFindLyricsAndAddALyricsFile()
    {
        var cut = await OpenAsync(Api([], findLyrics: true));
        cut.WaitForAssertion(() => Assert.Equal("unavailable", View(cut)));

        Assert.Equal("No lyrics found for this song.", cut.Find("[data-lyrics-state=unavailable] .playback-lyrics__message").TextContent.Trim());
        var actions = cut.FindAll("[data-lyrics-state=unavailable] button").Select(button => button.TextContent.Trim()).ToArray();
        Assert.Equal(["Find lyrics", "Add a lyrics file"], actions);
        Assert.Empty(cut.FindAll("[data-lyrics-chip]"));
        Assert.Empty(cut.Find("[data-lyrics-source]").TextContent.Trim());
    }

    [Fact]
    public async Task Unavailable_WithoutAFindLyricsPluginOffersOnlyAddALyricsFile()
    {
        var cut = await OpenAsync(Api([]));
        cut.WaitForAssertion(() => Assert.Equal("unavailable", View(cut)));

        Assert.Equal(["Add a lyrics file"], cut.FindAll("[data-lyrics-state=unavailable] button").Select(button => button.TextContent.Trim()));
    }

    [Fact]
    public async Task Error_ShowsThePresentersMessageWithTryAgain_AndTryAgainLoadsTheLyrics()
    {
        var reads = 0;
        var api = EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetTextTracksAsync), _ => ++reads == 1
                ? Task.FromException<IReadOnlyList<TextTrackDto>>(new InvalidOperationException("down"))
                : Task.FromResult<IReadOnlyList<TextTrackDto>>([Track(_track, "lrclib")]));
            stub.SetHandler(nameof(IEngineApiClient.GetTextTrackContentAsync), _ => Task.FromResult<string?>("[00:01.00]Words"));
        });
        var cut = await OpenAsync(api);

        cut.WaitForAssertion(() => Assert.Equal("error", View(cut)));
        Assert.Equal("Lyrics could not be loaded.", cut.Find("[data-lyrics-state=error] [role=alert]").TextContent.Trim());
        Assert.Empty(cut.FindAll("[data-lyrics-chip]"));

        await cut.ClickAsync("[data-lyrics-state=error] button");

        cut.WaitForAssertion(() => Assert.Equal("synced", View(cut)));
        Assert.Equal(2, reads);
    }

    // ---- Header, footer and menu ----

    [Fact]
    public async Task TheHeaderHoldsTheTitleTheChipOpenNowPlayingAndCloseInThatOrder()
    {
        var opened = 0;
        var cut = await OpenAsync(Api([Track(_track, "local", userOwned: true)]), EventCallback.Factory.Create(this, () => opened++));
        cut.WaitForAssertion(() => Assert.Equal("synced", View(cut)));

        var header = cut.Find(".playback-panel__header");
        Assert.Equal("Lyrics", header.QuerySelector("h2")!.TextContent);
        Assert.Equal(["Open Now playing", "Close Lyrics"], header.QuerySelectorAll("button").Select(button => button.GetAttribute("aria-label")));
        var order = header.Children.Select(child => child.ClassName).ToArray();
        Assert.StartsWith("playback-panel__heading", order[0]);
        Assert.Contains(header.QuerySelector(".playback-panel__heading")!.Children, child => child.HasAttribute("data-lyrics-chip"));

        await cut.ClickAsync(".playback-panel__header button[aria-label='Open Now playing']");

        Assert.Equal(1, opened);
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-playback-popover-panel]")));
    }

    [Fact]
    public async Task WithoutAHostActionThereIsNoOpenNowPlayingButton()
    {
        var cut = await OpenAsync(Api([Track(_track, "local", userOwned: true)]));

        Assert.Empty(cut.FindAll("button[aria-label='Open Now playing']"));
    }

    [Fact]
    public async Task TheFooterMenuOffersFindAnotherAddAFileAndAboutLyricsSources_ThenThisOneIsWrongUnderAHairline()
    {
        var cut = await OpenAsync(Api([Track(_track, "local", userOwned: true)], findLyrics: true));
        cut.WaitForAssertion(() => Assert.Equal("synced", View(cut)));

        await OpenFooterMenuAsync(cut);

        Assert.Equal(["Find another", "Add a lyrics file", "About lyrics sources", "This one is wrong"], FooterMenuLabels(cut));
        Assert.Single(cut.FindAll(".playback-lyrics-menu__divider"));
        Assert.Equal("0", cut.Find("[data-lyrics-action='find-another']").GetAttribute("tabindex"));
        Assert.Equal("-1", cut.Find("[data-lyrics-action='about']").GetAttribute("tabindex"));
    }

    [Fact]
    public async Task WithNoLyricsAndNoFindPluginTheMenuOffersAddAFileAndAbout()
    {
        var cut = await OpenAsync(Api([]));
        cut.WaitForAssertion(() => Assert.Equal("unavailable", View(cut)));

        await OpenFooterMenuAsync(cut);

        Assert.Equal(["Add a lyrics file", "About lyrics sources"], FooterMenuLabels(cut));
    }

    [Fact]
    public async Task AboutLyricsSources_SlidesInAsANestedViewHoldingTheLongExplanation_AndBackReturns()
    {
        var cut = await OpenAsync(Api([Track(_track, "lrclib")], content: "[00:01.00]Words"));
        cut.WaitForAssertion(() => Assert.Equal("synced", View(cut)));
        Assert.DoesNotContain("a free lyrics service", cut.Markup);

        await OpenFooterMenuAsync(cut);
        await cut.ClickAsync("[data-lyrics-action='about']");

        cut.WaitForAssertion(() => Assert.Equal("About lyrics sources", cut.Find(".playback-panel__title").TextContent));
        Assert.Contains("a free lyrics service", cut.Find("[data-lyrics-about]").TextContent);
        Assert.Empty(cut.FindAll("[data-lyrics-chip]"));

        await cut.ClickAsync("button[aria-label='Back']");

        cut.WaitForAssertion(() => Assert.Equal("Lyrics", cut.Find(".playback-panel__title").TextContent));
        Assert.Empty(cut.FindAll("[data-lyrics-about]"));
    }

    [Fact]
    public async Task FindAnother_ListsTheOffersWithOneTabStop_AndGoBackReturnsToTheLyrics()
    {
        var api = EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetTextTracksAsync), _ => Task.FromResult<IReadOnlyList<TextTrackDto>>([Track(_track, "lrclib")]));
            stub.SetHandler(nameof(IEngineApiClient.GetTextTrackContentAsync), _ => Task.FromResult<string?>("[00:01.00]Words"));
            stub.SetHandler(nameof(IEngineApiClient.GetPlayerPluginActionsAsync), _ =>
                Task.FromResult<IReadOnlyList<PlayerPluginActionDto>>([new() { kind = "find_lyrics", slot = "player_core" }]));
            stub.SetHandler(nameof(IEngineApiClient.GetTextTrackAlternativesAsync), _ => Task.FromResult<TextTrackAlternativesResponse?>(new()
            {
                status = "Found",
                items = [new() { provider = "lrclib", provider_name = "LRCLIB", source_id = "1", is_timed = true, is_current = true }, new() { provider = "lrclib", provider_name = "LRCLIB", source_id = "2" }, new() { provider = "lrclib", provider_name = "LRCLIB", source_id = "3" }],
            }));
        });
        var cut = await OpenAsync(api);
        cut.WaitForAssertion(() => Assert.Equal("synced", View(cut)));
        await OpenFooterMenuAsync(cut);

        await cut.ClickAsync("[data-lyrics-action='find-another']");

        cut.WaitForAssertion(() => Assert.Equal("alternatives", View(cut)));
        var offers = cut.FindAll("[data-lyrics-offer]");
        Assert.Equal(3, offers.Count);
        Assert.True(offers[0].HasAttribute("disabled"));
        Assert.Equal(["-1", "0", "-1"], offers.Select(offer => offer.GetAttribute("tabindex")));
        Assert.Contains("In use", offers[0].TextContent);

        await cut.ClickAsync(".playback-lyrics-alternatives__back");

        cut.WaitForAssertion(() => Assert.Equal("synced", View(cut)));
    }

    // ---- Following, Resume following and selecting a line (A10) ----

    [Fact]
    public async Task SelectingATimedLineSeeksThere_AndTheListFollowsAgain()
    {
        var cut = await OpenAsync(Api([Track(_track, "local", userOwned: true)]));
        cut.WaitForAssertion(() => Assert.Equal("synced", View(cut)));

        await cut.ClickAsync("button.playback-lyrics__line", index: 1);

        var seek = Assert.Single(_commands.Sent);
        Assert.Equal(ListenPlaybackCommandActions.Seek, seek.Action);
        Assert.Equal(5d, seek.Value);
        _lyricsModule.VerifyInvoke("follow");
    }

    [Fact]
    public async Task AHandScroll_ShowsResumeFollowingWithTheHint_AndResumeFollowingTakesThemAway()
    {
        var cut = await OpenAsync(Api([Track(_track, "local", userOwned: true)]));
        cut.WaitForAssertion(() => Assert.Equal("synced", View(cut)));
        Assert.Empty(cut.FindAll("[data-lyrics-resume]"));
        var lyrics = cut.FindComponent<PlaybackLyrics>();

        await cut.InvokeAsync(() => lyrics.Instance.FollowChanged(true)); // the page says the person scrolled by hand

        var pill = cut.Find("[data-lyrics-resume]");
        Assert.Equal("Resume following", pill.QuerySelector("button")!.TextContent.Trim());
        Assert.Equal("Select a line to jump to that moment.", pill.QuerySelector("p")!.TextContent.Trim());

        await cut.ClickAsync("[data-lyrics-resume] button");
        _lyricsModule.VerifyInvoke("follow");
        await cut.InvokeAsync(() => lyrics.Instance.FollowChanged(false)); // the page has re-centred and follows again

        Assert.Empty(cut.FindAll("[data-lyrics-resume]"));
    }

    // ---- Phone ----

    [Fact]
    public async Task OnAPhoneThePanelIsTheBottomSheetWithTheSameChipActionFooterAndLyrics()
    {
        var cut = await OpenAsync(Api([Track(_track, "local", userOwned: true)]), EventCallback.Factory.Create(this, () => { }));
        cut.WaitForAssertion(() => Assert.Equal("synced", View(cut)));

        await cut.InvokeAsync(() => cut.FindComponent<PlaybackPanel>().Instance.SetViewport(true));

        cut.WaitForElement(".playback-tool-sheet--lyrics");
        var header = cut.Find(".playback-tool-sheet__header");
        Assert.Equal("Lyrics", header.QuerySelector("h2")!.TextContent);
        Assert.Equal("Synced", header.QuerySelector("[data-lyrics-chip]")!.TextContent.Trim());
        Assert.NotNull(header.QuerySelector("button[aria-label='Open Now playing']"));
        Assert.Equal("Synced lyrics · Local file", cut.Find(".playback-tool-sheet__footer [data-lyrics-source]").TextContent.Trim());
        Assert.Equal(2, cut.FindAll("button.playback-lyrics__line").Count);
    }

    // ---- The part the Now playing page places ----

    [Fact]
    public async Task TheLyricsContentStandsAloneWithItsOwnFooterAndAboutBlock()
    {
        var api = Api([Track(_track, "lrclib")], content: "[00:01.00]Words");
        Services.AddSingleton(new PlaybackTransientToolCoordinator());
        Services.AddSingleton(new PlaybackSessionController(null!, null!));
        Services.AddSingleton(new PlaybackLyricsPresenter(api));
        Render<AppPopoverHost>();

        var cut = Render<PlaybackLyrics>(parameters => parameters.Add(p => p.Snapshot, Snapshot()).Add(p => p.Commands, _commands));

        cut.WaitForAssertion(() => Assert.Equal("synced", cut.Find("[data-playback-lyrics]").GetAttribute("data-lyrics-view")));
        Assert.Empty(cut.FindAll(".playback-lyrics__identity"));
        Assert.Equal("Synced lyrics · LRCLIB", cut.Find("[data-lyrics-source]").TextContent.Trim());
        var menu = cut.FindComponents<PlaybackPanel>().Single(panel => panel.Instance.Kind == "menu");
        await cut.InvokeAsync(() => menu.Instance.OpenAsync(true));
        cut.WaitForElement("[data-playback-lyrics-menu]");

        await cut.ClickAsync("[data-lyrics-action='about']");

        cut.WaitForAssertion(() => Assert.Equal("about", cut.Find("[data-playback-lyrics]").GetAttribute("data-lyrics-view")));
        Assert.Contains("a free lyrics service", cut.Find("[data-lyrics-about]").TextContent);
        await cut.ClickAsync("[data-lyrics-state=about] button");
        cut.WaitForAssertion(() => Assert.Equal("synced", cut.Find("[data-playback-lyrics]").GetAttribute("data-lyrics-view")));
    }

    // ---- The presenter behind the states ----

    [Fact]
    public async Task ThePresenterTellsSyncedFromUnsyncedByTheTimesInTheLines()
    {
        using var synced = new PlaybackLyricsPresenter(Api([Track(_track, "local", userOwned: true)]));
        await synced.EnsureAsync(Snapshot(), _commands);
        using var unsynced = new PlaybackLyricsPresenter(Api([Track(_track, "lrclib")], content: "Just words"));
        await unsynced.EnsureAsync(Snapshot(), _commands);

        Assert.True(synced.IsSynced);
        Assert.False(unsynced.IsSynced);
        Assert.False(synced.LoadFailed);
    }

    [Fact]
    public async Task ALyricsFileThatCannotBeLoadedIsAFailure_AndRetryClearsItBeforeLoadingAgain()
    {
        var reads = 0;
        var api = EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetTextTracksAsync), _ => ++reads == 1
                ? Task.FromException<IReadOnlyList<TextTrackDto>>(new InvalidOperationException("down"))
                : Task.FromResult<IReadOnlyList<TextTrackDto>>([Track(_track, "lrclib")]));
            stub.SetHandler(nameof(IEngineApiClient.GetTextTrackContentAsync), _ => Task.FromResult<string?>("[00:01.00]Words"));
        });
        using var presenter = new PlaybackLyricsPresenter(api);

        await presenter.EnsureAsync(Snapshot(), _commands);
        Assert.True(presenter.LoadFailed);
        Assert.Equal("Lyrics could not be loaded.", presenter.Status);

        await presenter.RetryAsync();
        Assert.False(presenter.LoadFailed);
        Assert.Null(presenter.Status);
        Assert.Equal("Words", Assert.Single(presenter.Lines).Text);
    }

    [Fact]
    public async Task NothingFoundForTheSongIsNotAFailure()
    {
        using var presenter = new PlaybackLyricsPresenter(Api([]));

        await presenter.EnsureAsync(Snapshot(), _commands);

        Assert.False(presenter.LoadFailed);
        Assert.Empty(presenter.Lines);
    }

    private sealed class SeekSpy : IPlaybackCommandSink
    {
        public List<ListenPlaybackCommandDto> Sent { get; } = [];
        public bool Supports(string action, ListenPlaybackSnapshot snapshot) => true;

        public Task<ListenPlaybackCommandReplyDto?> SendAsync(ListenPlaybackSnapshot snapshot, ListenPlaybackCommandDto command, CancellationToken ct = default)
        {
            Sent.Add(command);
            return Task.FromResult<ListenPlaybackCommandReplyDto?>(new());
        }
    }
}
