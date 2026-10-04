using Bunit;
using MediaEngine.Contracts.Playback;
using MediaEngine.Web.Components.Listen;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Playback;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;

namespace MediaEngine.Web.Tests;

public sealed class PlaybackFullPlayerTests : AsyncBunitContext
{
    public PlaybackFullPlayerTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging(); Services.AddLocalization(); Services.AddMudServices();
        var api = EngineApiClientStub.CreateDefault();
        Services.AddSingleton<IEngineApiClient>(api); Services.AddSingleton(new PlaybackLyricsPresenter(api));
        Render<MudBlazor.MudPopoverProvider>();
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
        var link = cut.Find(".playback-identity-owner > button[role='link']");
        Assert.Equal(title, link.TextContent);
        await link.ClickAsync();
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
        Assert.Single(phone.FindAll("[role='dialog']"));
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
        snapshot = snapshot with { Experience = PlayerExperienceModes.Audiobook, Queue = [snapshot.Queue[0] with
            { MediaType = "Audiobooks", AudiobookWorkId = Guid.NewGuid(), Chapters = [new() { Index = 0, AssetId = asset, Title = "Source intro", StartSeconds = 0, EndSeconds = 30 }, new() { Index = 1, AssetId = asset, Title = "Source chapter", StartSeconds = 30, EndSeconds = 90 }] }] };
        var bookmarks = 0;
        var cut = Render<PlaybackFullPlayer>(p => p.Add(x => x.Snapshot, snapshot).Add(x => x.Commands, new Sink()).Add(x => x.BookmarkRequested, _ => bookmarks++));
        Assert.Single(cut.FindAll("input[aria-label='Playback position']"));
        Assert.Single(cut.FindAll(".playback-full__book-progress"));
        await Activate(cut, PlaybackControlKey.Chapters);
        Assert.Equal(2, cut.FindAll("[role='tab']").Count);
        Assert.Contains("Current item paused", cut.Markup);
        await Activate(cut, PlaybackControlKey.Bookmarks);
        Assert.Equal(1, bookmarks);
        Assert.Empty(cut.FindAll("[role='dialog']"));
    }

    private static Task Activate(IRenderedComponent<PlaybackFullPlayer> cut, PlaybackControlKey key) =>
        cut.FindComponents<PlaybackIconButton>().Single(control => control.Instance.Control.Key == key).Find("button").ClickAsync();
    private static ListenPlaybackSnapshot Snapshot() => new() { ProfileId = Guid.NewGuid(), PlaybackRequestVersion = 11, CurrentIndex = 0,
        CurrentTimeSeconds = 37, DurationSeconds = 180, PlaybackRate = 1.25, IsPlaying = false,
        SleepTimerState = new() { Mode = AudiobookSleepTimerModes.Timer, DeadlineUtc = DateTimeOffset.UtcNow.AddMinutes(30), TimerGeneration = 8 },
        Queue = [new() { WorkId = Guid.NewGuid(), AssetId = Guid.NewGuid(), MediaType = "Music", Title = "Current", Duration = "3:00" }, new() { WorkId = Guid.NewGuid(), AssetId = Guid.NewGuid(), MediaType = "Music", Title = "Upcoming" }] };
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
