using Bunit;
using MediaEngine.Contracts.Playback;
using MediaEngine.Web.Components.Listen;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Playback;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Web.Services.Theming;
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
    private BunitJSModuleInterop _popoverModule = null!;

    public PlaybackLyricsPanelTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        _lyricsModule = JSInterop.SetupModule("./js/playback-lyrics.js");
        _popoverModule = JSInterop.SetupModule("./js/playback-popover.js");
        Services.AddLogging();
        Services.AddLocalization();
        Services.AddNativeUiServices();
    }

    private static ListenPlaybackSnapshot Snapshot(string title = "Stay a Little Longer") => new()
    {
        ProfileId = Guid.NewGuid(),
        CurrentIndex = 0,
        PlaybackRequestVersion = 9,
        DurationSeconds = 60,
        Queue = [new() { WorkId = Guid.NewGuid(), AssetId = Guid.NewGuid(), MediaType = "Music", Title = title, Subtitle = "The Midnight Coast", StreamUrl = "stream://song" }],
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

    // ---- Who may edit the lyrics (CLAUDE.md Never 1: editing is desktop-only and the Engine still decides) ----

    private static async Task<DeviceContextService> DeviceAsync(DeviceContextSnapshot snapshot)
    {
        var device = new DeviceContextService(EngineApiClientStub.CreateDefault());
        await device.InitialiseAsync(snapshot);
        return device;
    }

    private static DashboardSessionAccessor Session(bool administrator)
    {
        var profile = Guid.NewGuid();
        var session = new DashboardSessionAccessor();
        session.Set("token", Guid.NewGuid(), profile, Guid.NewGuid(),
            new DashboardAuthorityResponse(profile, profile, true, true, 1, 1, administrator, administrator, null, 1, [], [], []));
        return session;
    }

    [Theory]
    [InlineData("web", "desktop", true, true, true, true)]
    [InlineData("web", "desktop", true, true, true, false)]
    [InlineData("mobile", "phone", false, false, true, true)]
    [InlineData("web", "tablet", false, false, true, true)]
    [InlineData("web", "desktop", false, false, false, true)]
    [InlineData("web", "desktop", false, true, false, true)]
    public async Task EditLyricsIsOfferedOnlyToAnAdministratorOnADesktopWithAFineHoverPointer(
        string deviceClass, string formFactor, bool touch, bool finePointer, bool hover, bool administrator)
    {
        var device = await DeviceAsync(new DeviceContextSnapshot(deviceClass, formFactor, touch, FinePointer: finePointer, Hover: hover));
        Services.AddSingleton(device);
        Services.AddSingleton(Session(administrator));
        var cut = await OpenAsync(Api([Track(_track, "local", userOwned: true)]));
        cut.WaitForAssertion(() => Assert.Equal("synced", View(cut)));

        await OpenFooterMenuAsync(cut);

        var expected = deviceClass == "web" && formFactor == "desktop" && finePointer && hover && administrator;
        Assert.Equal(expected, FooterMenuLabels(cut).Contains("Edit lyrics"));
        Assert.Contains("This one is wrong", FooterMenuLabels(cut));
    }

    // ---- Focus never falls to the page ----

    private IReadOnlyList<JSRuntimeInvocation> Calls(BunitJSModuleInterop module, string identifier) =>
        module.Invocations.Where(call => call.Identifier == identifier).ToArray();

    [Fact]
    public async Task ChoosingAboutClosesTheMenuBackOntoTheMoreButton_ThenFocusGoesIntoTheNestedView_AndBackReturnsToWhatOpenedIt()
    {
        var cut = await OpenAsync(Api([Track(_track, "lrclib")], content: "[00:01.00]Words"));
        cut.WaitForAssertion(() => Assert.Equal("synced", View(cut)));
        await OpenFooterMenuAsync(cut);

        await cut.ClickAsync("[data-lyrics-action='about']");

        cut.WaitForAssertion(() => Assert.Equal("About lyrics sources", cut.Find(".playback-panel__title").TextContent));
        // The menu panel released with focus restored to its trigger (the "..." button): update(root, null, false, true).
        Assert.Contains(Calls(_popoverModule, "update"), call => call.Arguments is [_, null, false, true]);
        // The nested view took focus in (push), without leaving it on the page.
        cut.WaitForAssertion(() => Assert.Contains(Calls(_popoverModule, "navigated"), call => call.Arguments is [_, _, true]));

        await cut.ClickAsync("button[aria-label='Back']");

        cut.WaitForAssertion(() => Assert.Equal("Lyrics", cut.Find(".playback-panel__title").TextContent));
        cut.WaitForAssertion(() => Assert.Equal(false, Calls(_popoverModule, "navigated").Last().Arguments[2]));
    }

    [Fact]
    public async Task FindAnotherMovesFocusIntoTheOffers_AndGoBackReturnsItToTheMoreButton()
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
                items = [new() { provider = "lrclib", provider_name = "LRCLIB", source_id = "2" }],
            }));
        });
        var cut = await OpenAsync(api);
        cut.WaitForAssertion(() => Assert.Equal("synced", View(cut)));
        await OpenFooterMenuAsync(cut);

        await cut.ClickAsync("[data-lyrics-action='find-another']");

        cut.WaitForAssertion(() => Assert.Equal("alternatives", View(cut)));
        cut.WaitForAssertion(() => Assert.Contains(Calls(_lyricsModule, "focusView"), call => call.Arguments is [_, "alternatives", _]));
        Assert.Contains(Calls(_popoverModule, "update"), call => call.Arguments is [_, null, false, true]);
        Assert.Empty(Calls(_lyricsModule, "focusMore"));

        await cut.ClickAsync(".playback-lyrics-alternatives__back");

        cut.WaitForAssertion(() => Assert.Equal("synced", View(cut)));
        cut.WaitForAssertion(() => Assert.NotEmpty(Calls(_lyricsModule, "focusMore")));
    }

    // ---- A song change while the panel is open ----

    [Fact]
    public async Task ThePanelStaysOpenWhenTheSongChanges_AndTheLyricsStartOverForTheNewSong()
    {
        var tracksReads = 0;
        var cut = await OpenAsync(Api([Track(_track, "local", userOwned: true)], tracksRead: _ => { Interlocked.Increment(ref tracksReads); return Task.CompletedTask; }));
        cut.WaitForAssertion(() => Assert.Equal("synced", View(cut)));

        cut.Render(parameters => parameters.Add(p => p.Snapshot, Snapshot("A New Song")));

        cut.WaitForAssertion(() => Assert.Equal("A New Song", cut.Find(".playback-lyrics__track").TextContent));
        cut.WaitForAssertion(() => Assert.Equal(2, Volatile.Read(ref tracksReads)));
        Assert.NotEmpty(cut.FindAll("[data-playback-popover-panel]"));
        Assert.Equal("synced", View(cut));
    }

    [Fact]
    public void TheBarDoesNotKeyTheLyricsPanelBySong_SoTheSongChangingNeverClosesIt()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var bar = File.ReadAllText(Path.Combine(root, "src", "MediaEngine.Web", "Components", "Listen", "ListenNowPlayingBar.razor"));
        var tag = bar[bar.IndexOf("<PlaybackLyricsPanel", StringComparison.Ordinal)..];
        tag = tag[..tag.IndexOf("/>", StringComparison.Ordinal)];

        Assert.DoesNotContain("ItemKey", tag);
    }

    // ---- A stale reply and a reload while old lines are showing ----

    private static ListenPlaybackSnapshot SnapshotFor(Guid asset, string title)
    {
        var snapshot = Snapshot(title);
        snapshot.Queue[0] = new() { WorkId = Guid.NewGuid(), AssetId = asset, MediaType = "Music", Title = title, Subtitle = "The Midnight Coast", StreamUrl = "stream://" + asset };
        return snapshot;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ALyricsReplyThatArrivesAfterTheSongChangedIsNeitherShownNorCountedAsAFailure(bool theStaleReplyFails)
    {
        var firstAsset = Guid.NewGuid();
        var secondAsset = Guid.NewGuid();
        var firstTrack = Guid.NewGuid();
        var secondTrack = Guid.NewGuid();
        var staleReply = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var staleAsked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var api = EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetTextTracksAsync), args =>
                Task.FromResult<IReadOnlyList<TextTrackDto>>([Track((Guid)args![0]! == firstAsset ? firstTrack : secondTrack, "lrclib")]));
            stub.SetHandler(nameof(IEngineApiClient.GetTextTrackContentAsync), args =>
            {
                if ((Guid)args![0]! != firstAsset) return Task.FromResult<string?>("[00:01.00]The new song");
                staleAsked.TrySetResult();
                return staleReply.Task;
            });
        });
        using var presenter = new PlaybackLyricsPresenter(api);

        var first = presenter.EnsureAsync(SnapshotFor(firstAsset, "First"), _commands);
        await staleAsked.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await presenter.EnsureAsync(SnapshotFor(secondAsset, "Second"), _commands);
        if (theStaleReplyFails) staleReply.SetException(new InvalidOperationException("late failure"));
        else staleReply.SetResult("[00:01.00]The old song");
        await first;

        Assert.Equal("The new song", Assert.Single(presenter.Lines).Text);
        Assert.Equal(secondTrack, presenter.SelectedTrackId);
        Assert.False(presenter.LoadFailed);
        Assert.Null(presenter.Status);
        Assert.False(presenter.Loading);
    }

    [Fact]
    public async Task TheChipStepsAsideWhileTheLyricsAreLookedUpAgainAndComesBackWithThem()
    {
        var search = new TaskCompletionSource<RefreshTextTracksResponse?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var api = EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetTextTracksAsync), _ => Task.FromResult<IReadOnlyList<TextTrackDto>>([Track(_track, "local", userOwned: true)]));
            stub.SetHandler(nameof(IEngineApiClient.GetTextTrackContentAsync), _ => Task.FromResult<string?>("[00:01.00]Words"));
            stub.SetHandler(nameof(IEngineApiClient.RefreshTextTracksAsync), _ => search.Task);
        });
        var cut = await OpenAsync(api);
        cut.WaitForAssertion(() => Assert.Equal("synced", View(cut)));
        Assert.NotEmpty(cut.FindAll("[data-lyrics-chip]"));
        var presenter = Services.GetRequiredService<PlaybackLyricsPresenter>();

        var refresh = cut.InvokeAsync(presenter.RefreshAsync);

        cut.WaitForAssertion(() => Assert.Equal("loading", View(cut)));
        Assert.NotEmpty(presenter.Lines);
        Assert.Empty(cut.FindAll("[data-lyrics-chip]"));

        search.SetResult(new RefreshTextTracksResponse());
        await refresh;

        cut.WaitForAssertion(() => Assert.Equal("synced", View(cut)));
        Assert.NotEmpty(cut.FindAll("[data-lyrics-chip]"));
    }

    // ---- Adding a lyrics file ----

    private sealed class PickedFile(string name, long size, bool unreadable = false) : Microsoft.AspNetCore.Components.Forms.IBrowserFile
    {
        public string Name => name;
        public DateTimeOffset LastModified => DateTimeOffset.UnixEpoch;
        public long Size => size;
        public string ContentType => "text/plain";
        public Stream OpenReadStream(long maxAllowedSize = 512000, CancellationToken cancellationToken = default) =>
            unreadable ? throw new IOException("gone") : new MemoryStream("[00:01.00]Words"u8.ToArray());
    }

    private async Task<(PlaybackLyricsPresenter Presenter, List<string> Uploaded)> PresenterWithUploadSpyAsync()
    {
        var uploaded = new List<string>();
        var api = EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetTextTracksAsync), _ => Task.FromResult<IReadOnlyList<TextTrackDto>>([]));
            stub.SetHandler(nameof(IEngineApiClient.ImportTextTrackAsync), args =>
            {
                uploaded.Add((string)args![3]!);
                return Task.FromResult<RefreshTextTracksResponse?>(new RefreshTextTracksResponse());
            });
        });
        var presenter = new PlaybackLyricsPresenter(api);
        await presenter.EnsureAsync(Snapshot(), _commands);
        return (presenter, uploaded);
    }

    [Theory]
    [InlineData("words.lrc")]
    [InlineData("WORDS.LRC")]
    [InlineData("Words.Txt")]
    public async Task ALrcOrTxtFileOfAnyCapitalsWithinFiveMegabytesIsUploaded(string name)
    {
        var (presenter, uploaded) = await PresenterWithUploadSpyAsync();
        using var _ = presenter;

        var problem = await presenter.ImportBrowserFileAsync(new PickedFile(name, 1024));

        Assert.Equal(PlaybackLyricsFileProblem.None, problem);
        Assert.Equal([name], uploaded);
    }

    [Theory]
    [InlineData("cover.png", 1024, PlaybackLyricsFileProblem.WrongType)]
    [InlineData("lyrics.lrc.exe", 1024, PlaybackLyricsFileProblem.WrongType)]
    [InlineData("lyrics", 1024, PlaybackLyricsFileProblem.WrongType)]
    [InlineData("big.lrc", 5 * 1024 * 1024 + 1, PlaybackLyricsFileProblem.TooBig)]
    public async Task AFileThatIsTheWrongTypeOrTooBigIsNeverUploaded_AndTheResultSaysWhy(string name, long size, PlaybackLyricsFileProblem expected)
    {
        var (presenter, uploaded) = await PresenterWithUploadSpyAsync();
        using var _ = presenter;

        var problem = await presenter.ImportBrowserFileAsync(new PickedFile(name, size));

        Assert.Equal(expected, problem);
        Assert.Empty(uploaded);
    }

    [Fact]
    public async Task AFileTheBrowserCannotReadIsReportedAsUnreadable()
    {
        var (presenter, uploaded) = await PresenterWithUploadSpyAsync();
        using var _ = presenter;

        var problem = await presenter.ImportBrowserFileAsync(new PickedFile("words.lrc", 1024, unreadable: true));

        Assert.Equal(PlaybackLyricsFileProblem.Unreadable, problem);
        Assert.Empty(uploaded);
    }

    [Theory]
    [InlineData("cover.png", 1024, false, "That is not a lyrics file. Choose a .lrc or .txt file.")]
    [InlineData("big.lrc", 6 * 1024 * 1024, false, "That lyrics file is too big. Lyrics files must be 5 MB or smaller.")]
    [InlineData("words.lrc", 1024, true, "That lyrics file could not be read.")]
    public async Task ThePanelSaysInPlainWordsWhyAPickedFileWasNotAdded(string name, long size, bool unreadable, string message)
    {
        var cut = await OpenAsync(Api([]));
        cut.WaitForAssertion(() => Assert.Equal("unavailable", View(cut)));
        var upload = cut.FindComponent<AppFileUpload<Microsoft.AspNetCore.Components.Forms.IBrowserFile>>();

        await cut.InvokeAsync(() => upload.Instance.FilesChanged.InvokeAsync(new PickedFile(name, size, unreadable)));

        cut.WaitForAssertion(() => Assert.Equal(message, cut.Find(".playback-lyrics__status").TextContent.Trim()));
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
