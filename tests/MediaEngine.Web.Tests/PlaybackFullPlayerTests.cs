using Bunit;
using MediaEngine.Contracts.Playback;
using MediaEngine.Web.Components.Listen;
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

    [Fact]
    public void PopupHasAnInlineHorizontalVolumeAndMusicModeControls()
    {
        var cut = Render<PlaybackFullPlayer>(p => p.Add(c => c.Snapshot, Snapshot()).Add(c => c.Commands, new Sink()).Add(c => c.IsPopup, true));
        Assert.Single(cut.FindAll(".playback-full__volume input[aria-orientation='horizontal']"));
        Assert.Equal("0:37", cut.Find(".playback-full__timeline .playback-seek-rail__start").TextContent);
        Assert.Equal("3:00", cut.Find(".playback-full__timeline .playback-seek-rail__end").TextContent);
        Assert.Empty(cut.FindAll("input[aria-orientation='vertical']"));
        Assert.Single(cut.FindAll("button[aria-label='Turn shuffle on']"));
        foreach (var label in new[] { "Lyrics", "Queue" })
        {
            var button = cut.Find($".playback-full__modes button[aria-label='{label}']");
            Assert.Equal(string.Empty, button.TextContent.Trim());
            Assert.Equal(label, button.ParentElement!.GetAttribute("data-playback-tooltip"));
        }
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
    public async Task PhoneFocusBoundaryRebindsOnSubjectChangeAndDetachesWhenTheSubjectDisappears()
    {
        var module = JSInterop.SetupModule("./js/playback-full-focus.js");
        module.SetupVoid("attach"); module.SetupVoid("detach");
        var snapshot = Snapshot(); var sink = new Sink();
        var cut = Render<PlaybackFullPlayer>(p => p.Add(x => x.Snapshot, snapshot).Add(x => x.Commands, sink));
        var initial = module.Invocations.Single(call => call.Identifier == "attach");
        Assert.Equal(false, initial.Arguments[2]); // Phone host captures its originating Expand first.
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

    [Fact]
    public async Task PopupFocusBoundaryEstablishesFocusAndDisposesWithoutAnExitOrTransportCommand()
    {
        var module = JSInterop.SetupModule("./js/playback-full-focus.js");
        module.SetupVoid("attach"); module.SetupVoid("detach");
        var sink = new Sink();
        var cut = Render<PlaybackFullPlayer>(p => p.Add(x => x.Snapshot, Snapshot()).Add(x => x.Commands, sink).Add(x => x.IsPopup, true));
        var attach = Assert.Single(module.Invocations, call => call.Identifier == "attach");
        Assert.Equal(true, attach.Arguments[2]);
        await cut.Instance.DisposeAsync();
        var detach = Assert.Single(module.Invocations, call => call.Identifier == "detach");
        Assert.Equal(attach.Arguments[0], detach.Arguments[0]);
        Assert.Empty(sink.Commands);
    }

    [Fact]
    public void PopupKeepsTheFullCompositionWhenTheMainWindowReturnsToDockedPresentation()
    {
        var playback = new PlaybackSessionController(null!, null!);
        playback.RestoreState(Snapshot());
        playback.SetPresentationSurface(PlaybackPresentationSurface.NowPlaying);
        var sink = new Sink();
        var cut = Render<PlaybackFullPlayer>(p => p.Add(x => x.Snapshot, playback.CreateSnapshot()).Add(x => x.Commands, sink).Add(x => x.IsPopup, true));
        playback.SetPresentationSurface(PlaybackPresentationSurface.Docked);
        cut.Render(p => p.Add(x => x.Snapshot, playback.CreateSnapshot()).Add(x => x.Commands, sink).Add(x => x.IsPopup, true));
        Assert.Single(cut.FindAll(".playback-full--popup"));
        Assert.Empty(cut.FindAll(".playback-full__header"));
        Assert.Empty(sink.Commands);
    }

    [Fact]
    public async Task PhoneCollapseDismissesOnlyPresentationAndPopupHasNoExitSlotOrShortcut()
    {
        var snapshot = Snapshot();
        var sink = new Sink();
        var collapsed = 0;
        var phone = Render<PlaybackFullPlayer>(p => p.Add(x => x.Snapshot, snapshot).Add(x => x.Commands, sink).Add(x => x.OnCollapse, () => collapsed++));
        await Activate(phone, PlaybackControlKey.Queue);
        Assert.Single(phone.FindAll(".playback-full__middle"));
        await phone.Find("button[aria-label='Collapse player']").ClickAsync();
        Assert.Equal(1, collapsed);
        Assert.Empty(phone.FindAll("[role='dialog']"));
        Assert.Empty(sink.Commands);
        Assert.Same(snapshot, phone.Instance.Snapshot);
        Assert.Empty(phone.FindAll("audio,video"));
        Assert.Equal(snapshot.SleepTimerState.DeadlineUtc, phone.Instance.Snapshot.SleepTimerState.DeadlineUtc);

        var popup = Render<PlaybackFullPlayer>(p => p.Add(x => x.Snapshot, snapshot).Add(x => x.Commands, sink).Add(x => x.IsPopup, true));
        Assert.Empty(popup.FindAll(".playback-full__header"));
        Assert.Empty(popup.FindAll("button[aria-label='Collapse player'],button[aria-label='Close player']"));
        await popup.Find("section.playback-full").KeyDownAsync("Escape");
        Assert.Empty(sink.Commands);
        Assert.Equal(phone.Find(".playback-full__timeline").TextContent, popup.Find(".playback-full__timeline").TextContent);
        Assert.Equal(phone.FindComponents<ListenTransportControls>().Single().Instance.Variant, popup.FindComponents<ListenTransportControls>().Single().Instance.Variant);
    }

    [Fact]
    public async Task QueueHistoryAndStableRemovalUseSharedBodyWithoutChangingTheCurrentSong()
    {
        var snapshot = Snapshot();
        var sink = new Sink();
        var cut = Render<PlaybackFullPlayer>(p => p.Add(x => x.Snapshot, snapshot).Add(x => x.Commands, sink).Add(x => x.IsPopup, true));
        await Activate(cut, PlaybackControlKey.Queue);
        await cut.InvokeAsync(() => cut.FindComponents<PlaybackPopover>().Single(p => p.Instance.Title == "Queue actions for Upcoming").Instance.OpenAsync(true));
        await cut.Find("button[aria-label='Remove Upcoming from queue']").ClickAsync();
        var remove = Assert.Single(sink.Commands);
        Assert.Same(snapshot, remove.Snapshot);
        Assert.Equal(snapshot.Queue[1].QueueEntryId, remove.Command.QueueEntryId);
        await cut.Find("[role='tab'][aria-selected='false']").ClickAsync();
        Assert.Single(cut.FindAll("[role='tablist']"));
        await cut.Find("section.playback-full").KeyDownAsync("Escape");
        Assert.Empty(cut.FindAll("[role='dialog']"));
        Assert.Single(sink.Commands);
    }

    [Fact]
    public async Task ImmediateNativeToggleIsConsumedOnceAndNewSubjectDismissesAnObsoleteTool()
    {
        var snapshot = Snapshot();
        var sink = new Sink();
        JSInterop.Setup<bool>("listenPlayback.consumeImmediateToggleHandled").SetResult(true);
        var cut = Render<PlaybackFullPlayer>(p => p.Add(x => x.Snapshot, snapshot).Add(x => x.Commands, sink));
        await cut.Find("button.playback-primary-button").ClickAsync();
        Assert.Empty(sink.Commands);
        await Activate(cut, PlaybackControlKey.Queue);
        cut.Render(p => p.Add(x => x.Snapshot, snapshot with { ProfileId = Guid.NewGuid(), PlaybackRequestVersion = 12 }).Add(x => x.Commands, sink));
        Assert.Empty(cut.FindAll("[role='dialog']"));
    }

    [Fact]
    public async Task BookKeepsOneNativeSeekRailCombinedContextAndExistingBookmarkCallback()
    {
        var snapshot = Snapshot();
        var asset = snapshot.Queue[0].AssetId;
        snapshot = snapshot with
        {
            Experience = PlayerExperienceModes.Audiobook,
            Queue = [snapshot.Queue[0] with
            { MediaType = "Audiobooks", AudiobookWorkId = Guid.NewGuid(), Chapters = [new() { Index = 0, AssetId = asset, Title = "Source intro", StartSeconds = 0, EndSeconds = 30 }, new() { Index = 1, AssetId = asset, Title = "Source chapter", StartSeconds = 30, EndSeconds = 90 }] }]
        };
        var bookmarks = 0;
        var cut = Render<PlaybackFullPlayer>(p => p.Add(x => x.Snapshot, snapshot).Add(x => x.Commands, new Sink()).Add(x => x.BookmarkRequested, _ => bookmarks++));
        Assert.Single(cut.FindAll("input[aria-label='Playback position']"));
        Assert.Single(cut.FindAll(".playback-full__book-progress"));
        await Activate(cut, PlaybackControlKey.Chapters);
        Assert.Empty(cut.FindAll("[role='tab']"));
        Assert.Contains("Source chapter", cut.Markup);
        await Activate(cut, PlaybackControlKey.Bookmarks);
        Assert.Equal(1, bookmarks);
        Assert.Empty(cut.FindAll("[role='dialog']"));
    }

    [Fact]
    public void MusicHasOneCompleteSongMenuOnPhoneAndPopup()
    {
        foreach (var popup in new[] { false, true })
        {
            var cut = Render<PlaybackFullPlayer>(p => p.Add(c => c.Snapshot, Snapshot()).Add(c => c.Commands, new Sink()).Add(c => c.IsPopup, popup));
            Assert.Single(cut.FindAll("button[aria-label='More song actions']"));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SongActionRowSharesTheGlyphFamilyAndTargetsOnPhoneAndPopup(bool popup)
    {
        var favorites = 0;
        var sink = new Sink();
        var cut = Render<PlaybackFullPlayer>(p => p.Add(x => x.Snapshot, Snapshot()).Add(x => x.Commands, sink)
            .Add(x => x.IsPopup, popup).Add(x => x.FavoriteChanged, () => favorites++));
        var row = cut.Find(".playback-song-actions");
        var targets = row.QuerySelectorAll("button.playback-song-action");
        Assert.Equal(3, targets.Length);
        Assert.Equal(new[] { "Favorite", "Like", "More" }, targets.Select(button =>
        {
            var svg = Assert.Single(button.QuerySelectorAll("svg"));
            Assert.Equal("0 0 24 24", svg.GetAttribute("viewBox"));
            Assert.Contains("playback-utility-glyph", svg.ClassList);
            Assert.Equal("true", svg.GetAttribute("aria-hidden"));
            Assert.False(string.IsNullOrWhiteSpace(button.GetAttribute("aria-label")));
            Assert.Equal(string.Empty, button.TextContent.Trim());
            return svg.GetAttribute("data-playback-glyph");
        }));
        Assert.Empty(row.QuerySelectorAll(".tl-icon"));
        Assert.Equal(popup ? "popup" : "phone", row.QuerySelector(".playback-popover-owner")!.GetAttribute("data-playback-popover-surface"));
        await cut.Find("button[aria-label='Add song to Favorites']").ClickAsync();
        Assert.Equal(1, favorites);
        Assert.Empty(sink.Commands);
        await cut.Find(".playback-song-actions button[aria-label='Rate']").ClickAsync();
        Assert.Equal("true", cut.Find(".playback-song-actions button[aria-label='Rate']").GetAttribute("aria-expanded"));
        Assert.Equal(new[] { "Dislike", "Like" }, cut.FindAll(".media-rate-control__choices svg").Select(x => x.GetAttribute("data-playback-glyph")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LyricsKeepsAccessibleContextWithoutRepeatingAVisibleHeading(bool popup)
    {
        var cut = Render<PlaybackFullPlayer>(p => p.Add(x => x.Snapshot, Snapshot()).Add(x => x.Commands, new Sink()).Add(x => x.IsPopup, popup));
        await Activate(cut, PlaybackControlKey.Lyrics);
        Assert.Equal("Lyrics", cut.Find(".playback-panel-card").GetAttribute("aria-label"));
        Assert.Empty(cut.FindAll(".playback-panel-card h2,.playback-lyrics__sync"));
        Assert.Single(cut.FindAll(".playback-full__volume input[aria-label='Volume']"));
        Assert.Equal("0:37", cut.Find(".playback-full__timeline .playback-seek-rail__start").TextContent);
        await Activate(cut, PlaybackControlKey.Queue);
        Assert.Empty(cut.FindAll(".playback-panel-card h2"));
        var tabs = cut.FindAll(".playback-panel-card [role='tab']");
        Assert.Equal(2, tabs.Count);
        Assert.StartsWith("Up Next", tabs[0].TextContent.Trim());
        Assert.StartsWith("History", tabs[1].TextContent.Trim());
    }

    [Fact]
    public void DesktopUsesTheSameSongActionsAndAnIconOnlyLyricsTab()
    {
        var cut = Render<PlaybackDesktopScene>(p => p.Add(x => x.Snapshot, Snapshot()).Add(x => x.Commands, new Sink())
            .Add(x => x.FavoriteChanged, () => { }).Add(x => x.PanelKey, "queue"));
        Assert.Equal(new[] { "Favorite", "Like", "More" }, cut.FindAll("button.playback-song-action svg").Select(x => x.GetAttribute("data-playback-glyph")));
        Assert.Equal("expanded", cut.Find(".playback-song-actions .playback-popover-owner").GetAttribute("data-playback-popover-surface"));
        Assert.Empty(cut.FindAll(".playback-song-actions .tl-icon,.playback-panel-card h2"));
        Assert.Equal(string.Empty, cut.Find("[role='tab'][aria-label='Lyrics']").TextContent.Trim());
        Assert.Contains("Up Next", cut.Find(".playback-desktop__tabs").TextContent);
        Assert.Contains("History", cut.Find(".playback-desktop__tabs").TextContent);
    }

    private static Task Activate(IRenderedComponent<PlaybackFullPlayer> cut, PlaybackControlKey key) =>
        cut.Find($".playback-full__modes button[aria-label='{key}']").ClickAsync();
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
