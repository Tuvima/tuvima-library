using Bunit;
using MediaEngine.Contracts.Playback;
using MediaEngine.Web.Components.Listen;
using MediaEngine.Web.Components.Player;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Playback;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

public sealed class PlaybackFullPlayerTests : AsyncBunitContext
{
    public PlaybackFullPlayerTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging(); Services.AddLocalization(); Services.AddNativeUiServices();
        var api = EngineApiClientStub.CreateDefault();
        Services.AddSingleton<IEngineApiClient>(api); Services.AddSingleton(new PlaybackLyricsPresenter(api));
        Services.AddSingleton(new PlaybackSessionController(null!, null!));
        Render<AppPopoverHost>();
    }


    // ---- The phone's Now playing (unified player v2 D26; mockup screen 15 marker 2) ----

    private static ListenPlaybackSnapshot BookSnapshot()
    {
        var snapshot = Snapshot();
        return snapshot with { Experience = PlayerExperienceModes.Audiobook, Queue = [snapshot.Queue[0] with { MediaType = "Audiobook", Title = "The Way Beyond", AuthorName = "Maya Ellis" }] };
    }

    private IRenderedComponent<PlaybackFullPlayer> RenderPlayer(ListenPlaybackSnapshot snapshot, PlaybackBarState? state = null, Action<ComponentParameterCollectionBuilder<PlaybackFullPlayer>>? more = null) =>
        Render<PlaybackFullPlayer>(p =>
        {
            p.Add(x => x.Snapshot, snapshot).Add(x => x.Commands, new Sink())
                .Add(x => x.State, state ?? new PlaybackBarState(CanPrevious: true, CanNext: true, HasChapters: true, CanBookmark: true))
                .Add(x => x.TimelineContent, builder => builder.AddMarkupContent(0, "<div data-test-timeline></div>"))
                .Add(x => x.HostedTools, new HashSet<PlaybackControlKey> { PlaybackControlKey.Lyrics, PlaybackControlKey.Queue, PlaybackControlKey.Chapters, PlaybackControlKey.Speed, PlaybackControlKey.SleepTimer })
                .Add(x => x.ToolContent, tool => builder => builder.AddMarkupContent(0, $"<button type=\"button\" data-hosted-tool=\"{tool.Key}\" aria-label=\"{tool.AriaLabel}\"></button>"));
            more?.Invoke(p);
        });

    private static IReadOnlyList<string> Tools(IRenderedComponent<PlaybackFullPlayer> cut) =>
        cut.FindAll("[data-playback-tool-row] [data-playback-tool]").Select(slot => slot.GetAttribute("data-playback-tool")!).ToList();

    private static IReadOnlyList<string> Transport(IRenderedComponent<PlaybackFullPlayer> cut) =>
        cut.FindAll("[data-playback-full-transport] button").Select(button => button.GetAttribute("aria-label")!).ToList();

    [Fact]
    public void Music_TopLineHasTheCollapseChevronPlayingFromAndTheMoreMenu()
    {
        var cut = RenderPlayer(Snapshot() with { SourceLabel = "Northern Lights" });

        var top = cut.Find(".playback-full__top");
        Assert.NotNull(top.QuerySelector("button[aria-label='Collapse player']"));
        Assert.Equal("Playing from Northern Lights", cut.Find("[data-playback-full-from]").TextContent.Trim());
        Assert.NotNull(top.QuerySelector("button.playback-item-more"));
    }

    [Fact]
    public void WithoutASourceTheTopLineNamesTheAlbumAndWithoutAnAlbumJustNowPlaying()
    {
        var snapshot = Snapshot();
        var withAlbum = RenderPlayer(snapshot with { Queue = [snapshot.Queue[0] with { Album = "Northern Lights" }] });
        var bare = RenderPlayer(snapshot);

        Assert.Equal("Playing from Northern Lights", withAlbum.Find("[data-playback-full-from]").TextContent.Trim());
        Assert.Equal("Now playing", bare.Find("[data-playback-full-from]").TextContent.Trim());
    }

    [Fact]
    public void Music_ShowsArtworkTheTitleLineWithFavoriteLikeAndDislikeAndTheTimeline()
    {
        var cut = RenderPlayer(Snapshot(), more: p => p.Add(x => x.IsFavorite, false));

        Assert.NotNull(cut.Find("[data-playback-full-art]"));
        Assert.Equal("Current", cut.Find(".playback-full__title").TextContent.Trim());
        var actions = cut.FindAll("[data-playback-full-actions] button").Select(button => button.GetAttribute("aria-label")!).ToList();
        Assert.Equal(3, actions.Count);
        Assert.Contains("Favorites", actions[0]);
        Assert.StartsWith("Like", actions[1]);
        Assert.StartsWith("Dislike", actions[2]);
        Assert.NotNull(cut.Find("[data-playback-full-timeline] [data-test-timeline]"));
    }

    [Fact]
    public void Music_StacksTheMockupsPartsFromTheTop_TopLineArtworkTitleLineTimelineTransportThenTools()
    {
        var snapshot = Snapshot();
        var cut = RenderPlayer(snapshot with { SourceLabel = "Northern Lights", Queue = [snapshot.Queue[0] with { Subtitle = "The Midnight Coast" }, snapshot.Queue[1]] }, more: p => p.Add(x => x.IsFavorite, false));

        var order = cut.FindAll("[data-playback-full-collapse], [data-playback-full-from], .playback-item-more, [data-playback-full-art], .playback-full__title, .playback-full__secondary, [data-playback-full-actions], [data-playback-full-timeline], [data-playback-full-transport], [data-playback-tool-row]")
            .Select(element => element.GetAttribute("class")?.Split(' ').FirstOrDefault(name => name is "playback-item-more" or "playback-full__title" or "playback-full__secondary")
                ?? element.Attributes.First(attribute => attribute.Name.StartsWith("data-playback-", StringComparison.Ordinal) && attribute.Name != "data-playback-tooltip").Name)
            .ToArray();
        Assert.Equal(
        [
            "data-playback-full-collapse", "data-playback-full-from", "playback-item-more", "data-playback-full-art", "playback-full__title", "playback-full__secondary",
            "data-playback-full-actions", "data-playback-full-timeline", "data-playback-full-transport", "data-playback-tool-row",
        ], order);
        // Favorite, Like and Dislike sit at the right end of the title line, after the title and the artist, not under them.
        Assert.NotNull(cut.Find("[data-playback-full-identity] > .playback-full__text + [data-playback-full-actions]"));
    }

    [Fact]
    public void Audiobook_TitleLineHasFavoriteOnly()
    {
        var cut = RenderPlayer(BookSnapshot());

        var actions = cut.FindAll("[data-playback-full-actions] button");
        Assert.Single(actions);
        Assert.Contains("My List", actions[0].GetAttribute("aria-label"));
    }

    [Fact]
    public void Music_TransportIsPreviousPlayNextWithTheLargePlayAndTheToolRowIsShuffleLyricsQueueRepeat()
    {
        var cut = RenderPlayer(Snapshot());

        Assert.Equal(["Previous track", "Play", "Next track"], Transport(cut));
        Assert.NotNull(cut.Find("[data-playback-full-transport] .playback-phone-play"));
        Assert.Equal(["Shuffle", "Lyrics", "Queue", "Repeat"], Tools(cut));
    }

    [Fact]
    public void Audiobook_TransportIsTheFiveButtonsAndTheToolRowIsChaptersSpeedSleepTimerBookmarks()
    {
        var cut = RenderPlayer(BookSnapshot());

        Assert.Equal(["Previous chapter", "Skip back 10 seconds", "Play", "Skip forward 30 seconds", "Next chapter"], Transport(cut));
        Assert.Equal(["Chapters", "Speed", "SleepTimer", "Bookmarks"], Tools(cut));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    public void TheToolRowNeverHoldsMoreThanFourToolsWhateverTheMediaOffers(bool audiobook, bool everything)
    {
        var state = new PlaybackBarState(CanPrevious: everything, CanNext: everything, HasChapters: everything, HasEpisodes: everything, CanBookmark: everything, IsSleepTimerActive: everything, HasQueue: everything);
        var cut = RenderPlayer(audiobook ? BookSnapshot() : Snapshot(), state);

        Assert.InRange(Tools(cut).Count, 1, 4);
    }

    [Fact]
    public void ThereIsNoVolumeControlOnAPhone()
    {
        foreach (var snapshot in new[] { Snapshot(), BookSnapshot() })
        {
            var cut = RenderPlayer(snapshot);

            Assert.Empty(cut.FindAll("[aria-label='Volume'],[aria-label='Mute'],[aria-label='Unmute'],.playback-volume,input[type='range']"));
            Assert.Empty(cut.FindAll("[data-playback-tool='Volume']"));
        }
    }

    [Fact]
    public async Task Music_BookmarksAreInTheMoreMenuNotInTheToolRow()
    {
        var cut = RenderPlayer(Snapshot());

        Assert.DoesNotContain("Bookmarks", Tools(cut));
        await cut.InvokeAsync(() => cut.FindComponent<PlaybackPanel>().Instance.OpenAsync(true));
        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-playback-item-action='bookmarks']")));
    }

    [Fact]
    public async Task Music_BookmarksFromTheMoreMenuAreAnchoredToTheMoreButtonSoFocusReturnsThere()
    {
        var tools = new List<PlaybackControlActivation>();
        var cut = RenderPlayer(Snapshot(), more: p => p.Add(x => x.OnTool, activation => tools.Add(activation)));

        await cut.InvokeAsync(() => cut.FindComponent<PlaybackPanel>().Instance.OpenAsync(true));
        await cut.WaitForElement("[data-playback-item-action='bookmarks']").ClickAsync();

        var activation = Assert.Single(tools);
        Assert.Equal("open-bookmarks", activation.Control.Command);
        var moreButton = cut.FindComponent<PlaybackItemMenu>().Instance.TriggerElement;
        Assert.NotNull(moreButton);
        Assert.Equal(moreButton.Value.Id, activation.Trigger.Id);
    }

    [Fact]
    public void Audiobook_WithoutChaptersTheRowHasNoChaptersSlotAndHistoryDoesNotStandInForIt()
    {
        var withChapters = RenderPlayer(BookSnapshot());
        var without = RenderPlayer(BookSnapshot(), new PlaybackBarState(CanPrevious: true, CanNext: true, HasChapters: false, CanBookmark: true));

        Assert.Equal(["Chapters", "Speed", "SleepTimer", "Bookmarks"], Tools(withChapters));
        Assert.Equal(["Speed", "SleepTimer", "Bookmarks"], Tools(without));
    }

    [Fact]
    public async Task Audiobook_BookmarksStayInTheToolRowAndNotInTheMoreMenu()
    {
        var cut = RenderPlayer(BookSnapshot());

        Assert.Contains("Bookmarks", Tools(cut));
        await cut.InvokeAsync(() => cut.FindComponent<PlaybackPanel>().Instance.OpenAsync(true));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-playback-item-menu]")));
        Assert.Empty(cut.FindAll("[data-playback-item-action='bookmarks']"));
    }

    [Fact]
    public async Task APlainToolGoesToTheHostWithItsButtonAndShuffleActsAsATransportCommand()
    {
        var transport = new List<string>();
        var tools = new List<PlaybackControlActivation>();
        var cut = RenderPlayer(BookSnapshot(), more: p => p
            .Add(x => x.OnTransport, control => transport.Add(control.Command))
            .Add(x => x.OnTool, activation => tools.Add(activation)));

        await cut.Find("[data-playback-tool='Bookmarks'] button").ClickAsync();
        await cut.Find("[data-playback-full-transport] button[aria-label='Play']").ClickAsync();

        Assert.Equal("open-bookmarks", Assert.Single(tools).Control.Command);
        Assert.Equal(["toggle-play"], transport);

        var music = RenderPlayer(Snapshot(), more: p => p.Add(x => x.OnTransport, control => transport.Add(control.Command)));
        await music.Find("[data-playback-tool='Shuffle'] button").ClickAsync();
        Assert.Equal("shuffle", transport.Last());
    }

    [Fact]
    public void ThePanelsOfTheHostedToolsAreDrawnInsideTheToolRowSoTheyOpenAsSheetsOverThisView()
    {
        var cut = RenderPlayer(Snapshot());

        foreach (var key in new[] { "Lyrics", "Queue" })
        {
            Assert.NotNull(cut.Find($"[data-playback-tool='{key}'] [data-hosted-tool='{key}']"));
        }
    }

    [Fact]
    public async Task CollapsingDismissesOnlyThePresentationAndSendsNoPlaybackCommand()
    {
        var snapshot = Snapshot();
        var sink = new Sink();
        var collapsed = 0;
        var cut = Render<PlaybackFullPlayer>(p => p.Add(x => x.Snapshot, snapshot).Add(x => x.Commands, sink).Add(x => x.OnCollapse, () => collapsed++));

        await cut.Find("button[aria-label='Collapse player']").ClickAsync();
        await cut.Find("section.playback-full").KeyDownAsync("Escape");

        Assert.Equal(2, collapsed);
        Assert.Empty(sink.Commands);
        Assert.Empty(cut.FindAll("audio,video"));
    }

    [Fact]
    public void ThePhoneViewIsOneColumnWithNoSecondSetOfControlsOrVolume()
    {
        var cut = RenderPlayer(Snapshot());

        Assert.Single(cut.FindAll("[data-playback-full-transport]"));
        Assert.Single(cut.FindAll("[data-playback-tool-row]"));
        Assert.Empty(cut.FindAll(".playback-full__modes,.playback-full__volume,.playback-full__middle"));
    }

    [Fact]
    public async Task IdentityLinkReusesItsListenerButActivatesWithTheLatestRenderedAuthority()
    {
        var module = JSInterop.SetupModule("./js/playback-identity-link.js");
        module.Mode = JSRuntimeMode.Loose;
        var snapshot = Snapshot();
        var bookId = snapshot.Queue[0].WorkId;
        snapshot = snapshot with { Queue = [snapshot.Queue[0] with { MediaType = "Audiobooks", AudiobookWorkId = bookId }] };
        var sink = new Sink();
        var cut = Render<PlaybackIdentityLink>(p => p.Add(x => x.Snapshot, snapshot).Add(x => x.Commands, sink)
            .Add(x => x.Kind, "audiobook").Add(x => x.Id, bookId).Add(x => x.Text, "Book title"));
        cut.WaitForAssertion(() => Assert.Single(module.Invocations, call => call.Identifier == "attach"));
        var refreshed = snapshot with { CurrentTimeSeconds = 80, PlaybackRequestVersion = snapshot.PlaybackRequestVersion + 1 };
        cut.Render(p => p.Add(x => x.Snapshot, refreshed));
        await cut.InvokeAsync(() => cut.Instance.ActivateAsync());
        Assert.Single(module.Invocations, call => call.Identifier == "attach");
        Assert.Same(refreshed, Assert.Single(sink.Navigations).Snapshot);
        cut.Render(p => p.Add(x => x.Id, (Guid?)null));
        cut.Render(p => p.Add(x => x.Id, bookId));
        cut.WaitForAssertion(() => Assert.Equal(2, module.Invocations.Count(call => call.Identifier == "attach")));
        await cut.Instance.DisposeAsync();
        Assert.Single(module.Invocations, call => call.Identifier == "detach");
    }

    [Fact]
    public async Task ScopedIdentityLinkKeepsTheCanonicalCommandAndUnavailableIdentityAsPlainText()
    {
        var snapshot = Snapshot();
        var bookId = snapshot.Queue[0].WorkId;
        snapshot = snapshot with { Queue = [snapshot.Queue[0] with { MediaType = "Audiobooks", AudiobookWorkId = bookId }] };
        var sink = new Sink();
        var title = "A long source-authored book title";
        var cut = Render<PlaybackIdentityLink>(p => p.Add(x => x.Snapshot, snapshot).Add(x => x.Commands, sink)
            .Add(x => x.Kind, "audiobook").Add(x => x.Id, bookId).Add(x => x.Text, title));
        var owner = cut.Find("span.playback-identity-owner");
        Assert.Contains(owner.Attributes, attribute => attribute.Name.StartsWith("b-", StringComparison.Ordinal));
        var link = cut.Find(".playback-identity-owner > a[href]");
        Assert.Equal(title, link.TextContent);
        await cut.InvokeAsync(() => cut.Instance.ActivateAsync());
        var navigation = Assert.Single(sink.Navigations);
        Assert.Same(snapshot, navigation.Snapshot);
        Assert.Equal(("audiobook", bookId), (navigation.Kind, navigation.Id));
        Assert.Empty(sink.Commands);
        cut.Render(p => p.Add(x => x.Snapshot, snapshot).Add(x => x.Commands, sink)
            .Add(x => x.Kind, "audiobook").Add(x => x.Id, Guid.NewGuid()).Add(x => x.Text, title));
        Assert.Empty(cut.FindAll("button"));
        Assert.Equal(title, cut.Find(".playback-identity-owner").TextContent.Trim());
    }


    [Fact]
    public async Task FocusBoundaryRebindsOnSubjectChangeAndDetachesWhenTheSubjectDisappears()
    {
        var module = JSInterop.SetupModule("./js/playback-full-focus.js");
        module.SetupVoid("attach"); module.SetupVoid("detach");
        var snapshot = Snapshot(); var sink = new Sink();
        var cut = Render<PlaybackFullPlayer>(p => p.Add(x => x.Snapshot, snapshot).Add(x => x.Commands, sink));
        var initial = module.Invocations.Single(call => call.Identifier == "attach");
        Assert.Equal(false, initial.Arguments[2]); // The host captures its originating Expand first.
        var next = snapshot with { PlaybackRequestVersion = snapshot.PlaybackRequestVersion + 1 };
        cut.Render(p => p.Add(x => x.Snapshot, next).Add(x => x.Commands, sink));
        var rebind = module.Invocations.Last(call => call.Identifier == "attach");
        Assert.NotEqual(initial.Arguments[1], rebind.Arguments[1]);
        Assert.Equal(initial.Arguments[0], rebind.Arguments[0]);
        cut.Render(p => p.Add(x => x.Snapshot, next with { CurrentIndex = -1, Queue = [] }).Add(x => x.Commands, sink));
        Assert.Single(module.Invocations, call => call.Identifier == "detach");
        await cut.Instance.DisposeAsync();
        Assert.Single(module.Invocations, call => call.Identifier == "detach");
        Assert.Empty(sink.Commands);
    }

    private static ListenPlaybackSnapshot Snapshot() => new()
    {
        ProfileId = Guid.NewGuid(),
        PlaybackRequestVersion = 11,
        CurrentIndex = 0,
        CurrentTimeSeconds = 37,
        DurationSeconds = 180,
        PlaybackRate = 1.25,
        IsPlaying = false,
        SleepTimerState = new() { Mode = AudiobookSleepTimerModes.Timer, DeadlineUtc = DateTimeOffset.UtcNow.AddMinutes(30), TimerGeneration = 8 },
        Queue = [new() { WorkId = Guid.NewGuid(), AssetId = Guid.NewGuid(), MediaType = "Music", Title = "Current", Duration = "3:00" }, new() { WorkId = Guid.NewGuid(), AssetId = Guid.NewGuid(), MediaType = "Music", Title = "Upcoming" }]
    };
    private sealed class Sink : IPlaybackCommandSink, IPlaybackIdentityNavigationSink
    {
        public List<(ListenPlaybackSnapshot Snapshot, ListenPlaybackCommandDto Command)> Commands { get; } = [];
        public List<(ListenPlaybackSnapshot Snapshot, string Kind, Guid Id)> Navigations { get; } = [];
        public Task<ListenPlaybackCommandReplyDto?> NavigateIdentityAsync(ListenPlaybackSnapshot snapshot, string kind, Guid id, CancellationToken ct = default)
        { Navigations.Add((snapshot, kind, id)); return Task.FromResult<ListenPlaybackCommandReplyDto?>(new() { BooleanResult = true }); }

        public bool Supports(string action, ListenPlaybackSnapshot snapshot) => PlaybackCommandCapabilities.Supports(action, snapshot);
        public Task<ListenPlaybackCommandReplyDto?> SendAsync(ListenPlaybackSnapshot snapshot, ListenPlaybackCommandDto command, CancellationToken ct = default)
        { Commands.Add((snapshot, command)); return Task.FromResult<ListenPlaybackCommandReplyDto?>(new()); }
    }
}
