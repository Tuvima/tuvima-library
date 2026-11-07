using Bunit;
using MediaEngine.Contracts.Playback;
using MediaEngine.Web.Components.Listen;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Components.Watch;
using MediaEngine.Web.Services.Playback;
using MediaEngine.Web.Tests.Support;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>The Queue, Chapters and Episodes panels (unified player v2 D18 to D20; mockup screens 8, 9 and 10).</summary>
public sealed class PlaybackListPanelTests : AsyncBunitContext
{
    private static readonly Guid Profile = Guid.NewGuid();

    public PlaybackListPanelTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        Services.AddLocalization();
        Services.AddNativeUiServices();
    }

    // ---- Queue ----

    private static ListenPlaybackSnapshot Queue(int upcoming, string? source = "Northern Lights", string? firstTitle = null)
    {
        var queue = new List<ListenQueueItem> { Song("Stay a Little Longer", "The Midnight Coast", "4:12") };
        for (var index = 0; index < upcoming; index++)
        {
            queue.Add(Song(index == 0 && firstTitle is not null ? firstTitle : $"Song {index + 1}", "The Midnight Coast", "3:46"));
        }

        return new ListenPlaybackSnapshot { ProfileId = Profile, CurrentIndex = 0, Queue = queue, IsPlaying = true, SourceLabel = source, QueueRevision = 7 };
    }

    private static ListenQueueItem Song(string title, string artist, string length) => new()
    {
        QueueEntryId = Guid.NewGuid(),
        WorkId = Guid.NewGuid(),
        AssetId = Guid.NewGuid(),
        MediaType = "Music",
        Title = title,
        Subtitle = artist,
        Duration = length,
        AlbumWorkId = Guid.NewGuid(),
        ArtistPersonId = Guid.NewGuid(),
    };

    private IRenderedComponent<QueuePanel> RenderQueue(ListenPlaybackSnapshot snapshot, RecordingPlaybackSink sink, bool showTabs = true, string? tab = null) =>
        Render<QueuePanel>(p => p.Add(x => x.Snapshot, snapshot).Add(x => x.Commands, sink).Add(x => x.ShowTabs, showTabs).Add(x => x.ActiveTab, tab));

    private static async Task ChooseRowAction(IRenderedComponent<QueuePanel> cut, int row, string action)
    {
        await cut.InvokeAsync(() => cut.FindComponents<PlaybackRowMenu>()[row].Instance.OpenAsync());
        await cut.Find($"button[data-playback-row-action='{action}']").ClickAsync();
    }

    [Fact]
    public void Queue_DrawsTheTabsTheNowPlayingRowTheHeadingAndWhereTheQueueCameFrom()
    {
        var cut = RenderQueue(Queue(2), new RecordingPlaybackSink());

        Assert.Equal(new[] { "Up next · 2", "History" }, cut.FindAll("[role='tab']").Select(tab => tab.TextContent.Trim()));
        Assert.Equal("Up next · 2", cut.Find("[data-queue-heading]").TextContent);
        var now = cut.Find("[data-playback-list-row='now']");
        Assert.Equal("true", now.GetAttribute("aria-current"));
        Assert.Equal("Stay a Little Longer", now.QuerySelector(".plr__title")!.TextContent);
        Assert.Equal("The Midnight Coast", now.QuerySelector(".plr__secondary")!.TextContent.Trim());
        Assert.Equal("4:12", now.QuerySelector(".plr__length")!.TextContent);
        Assert.NotNull(now.QuerySelector(".playback-activity-mark"));
        Assert.Equal("Playing from Northern Lights", cut.Find("[data-queue-source]").TextContent.Trim());
        Assert.DoesNotContain("Autoplay", cut.Markup);
    }

    [Fact]
    public void Queue_UpcomingRowsHaveTheHandleInTheirOwnColumnAndTheMenuLast()
    {
        var cut = RenderQueue(Queue(2), new RecordingPlaybackSink());

        var rows = cut.FindAll("[data-playback-list-row='queue']");
        Assert.Equal(2, rows.Count);
        Assert.Equal(new[] { "plr__lead", "plr__art plr__art--square", "plr__body", "plr__length", "plr__trail" }, rows[0].Children.Select(child => child.ClassName));
        Assert.NotNull(rows[0].QuerySelector(".plr__lead button[data-playback-queue-drag]"));
        Assert.Equal("3:46", rows[0].QuerySelector(".plr__length")!.TextContent);
        Assert.NotNull(rows[0].QuerySelector(".plr__trail .prm-host"));
    }

    [Fact]
    public async Task Queue_ClearSendsOnlyTheClearUpcomingCommandSoTheCurrentSongKeepsPlaying()
    {
        var snapshot = Queue(3);
        var sink = new RecordingPlaybackSink();
        var cut = RenderQueue(snapshot, sink);

        await cut.Find("[data-queue-clear]").ClickAsync();

        var sent = Assert.Single(sink.Commands);
        Assert.Same(snapshot, sent.Snapshot);
        Assert.Equal(ListenPlaybackCommandActions.ClearUpcoming, sent.Command.Action);
        Assert.Null(sent.Command.QueueEntryId);
        Assert.Equal("true", cut.Find("[data-playback-list-row='now']").GetAttribute("aria-current"));
    }

    [Fact]
    public void Queue_ClearIsOffWhenNothingIsUpcoming()
    {
        var cut = RenderQueue(Queue(0), new RecordingPlaybackSink());

        Assert.True(cut.Find("[data-queue-clear]").HasAttribute("disabled"));
        Assert.Contains("Nothing up next", cut.Markup);
    }

    [Theory]
    [InlineData(1, "move-up", 0)]
    [InlineData(1, "move-down", 2)]
    [InlineData(2, "play-next", 0)]
    public async Task Queue_TheRowMenuMovesASongThroughTheSameReorderCommand(int row, string action, int expectedIndex)
    {
        var snapshot = Queue(3);
        var sink = new RecordingPlaybackSink();
        var cut = RenderQueue(snapshot, sink);

        await ChooseRowAction(cut, row, action);

        var sent = Assert.Single(sink.Commands);
        Assert.Equal(ListenPlaybackCommandActions.ReorderUpcoming, sent.Command.Action);
        Assert.Equal(snapshot.Queue[row + 1].QueueEntryId, sent.Command.QueueEntryId);
        Assert.Equal(expectedIndex, sent.Command.Index);
        Assert.Equal(7, sent.Command.ExpectedQueueRevision);
    }

    [Fact]
    public async Task Queue_TheFirstRowCannotMoveUpAndTheLastCannotMoveDown()
    {
        var cut = RenderQueue(Queue(3), new RecordingPlaybackSink());

        await cut.InvokeAsync(() => cut.FindComponents<PlaybackRowMenu>()[0].Instance.OpenAsync());
        Assert.True(cut.Find("button[data-playback-row-action='move-up']").HasAttribute("disabled"));
        Assert.True(cut.Find("button[data-playback-row-action='play-next']").HasAttribute("disabled"));
        Assert.False(cut.Find("button[data-playback-row-action='move-down']").HasAttribute("disabled"));
    }

    [Fact]
    public async Task Queue_TheRowMenuListsItsActionsAndRemovesTheRow()
    {
        var snapshot = Queue(2);
        var sink = new RecordingPlaybackSink();
        var cut = RenderQueue(snapshot, sink);

        await cut.InvokeAsync(() => cut.FindComponents<PlaybackRowMenu>()[0].Instance.OpenAsync());
        Assert.Equal(new[] { "Play next", "Move up", "Move down", "Remove", "Go to album", "Go to artist" }, cut.FindAll(".prm__row").Select(row => row.TextContent.Trim()));
        await cut.Find("button[data-playback-row-action='remove']").ClickAsync();

        var sent = Assert.Single(sink.Commands);
        Assert.Equal(ListenPlaybackCommandActions.RemoveUpcoming, sent.Command.Action);
        Assert.Equal(snapshot.Queue[1].QueueEntryId, sent.Command.QueueEntryId);
    }

    [Fact]
    public async Task Queue_TheRowMenuGetsItsArrowKeysFromTheSharedRovingFocusScript()
    {
        var roving = JSInterop.SetupModule("./js/roving-focus.js");
        roving.Mode = JSRuntimeMode.Loose;
        var cut = RenderQueue(Queue(2), new RecordingPlaybackSink());

        await cut.InvokeAsync(() => cut.FindComponents<PlaybackRowMenu>()[0].Instance.OpenAsync());

        cut.WaitForAssertion(() => Assert.Single(roving.Invocations["attachMenu"]));
        Assert.NotNull(cut.Find("[data-playback-row-menu] [data-playback-menu-roving]"));
        await cut.InvokeAsync(() => cut.FindComponent<PlaybackPanel>().Instance.CloseAsync());
        cut.WaitForAssertion(() => Assert.Single(roving.Invocations["detachMenu"]));
    }

    [Fact]
    public async Task Queue_GoToArtistAsksTheMainPlayerToOpenThatArtist()
    {
        var snapshot = Queue(2);
        var sink = new RecordingPlaybackSink();
        var cut = RenderQueue(snapshot, sink);

        await cut.InvokeAsync(() => cut.FindComponents<PlaybackRowMenu>()[0].Instance.OpenAsync());
        await cut.Find("button[data-playback-row-action='go-artist']").ClickAsync();

        Assert.Equal(("artist", snapshot.Queue[1].ArtistPersonId!.Value), Assert.Single(sink.Navigations));
        Assert.Empty(sink.Commands);
    }

    [Fact]
    public async Task Queue_DraggingARowOntoAnotherCallsTheSameReorderCommand()
    {
        var snapshot = Queue(3);
        var sink = new RecordingPlaybackSink();
        var cut = RenderQueue(snapshot, sink);
        var third = snapshot.Queue[3].QueueEntryId;

        await cut.Find($"button[data-playback-queue-drag='{third}']").TriggerEventAsync("ondragstart", new DragEventArgs());
        await cut.FindAll(".qp__drop")[0].TriggerEventAsync("ondrop", new DragEventArgs());

        var sent = Assert.Single(sink.Commands);
        Assert.Equal(ListenPlaybackCommandActions.ReorderUpcoming, sent.Command.Action);
        Assert.Equal(third, sent.Command.QueueEntryId);
        Assert.Equal(0, sent.Command.Index);
        Assert.Equal(7, sent.Command.ExpectedQueueRevision);
    }

    [Fact]
    public async Task Queue_AltArrowOnTheHandleMovesTheRowAndAnnouncesItToScreenReadersOnly()
    {
        var cut = RenderQueue(Queue(3), new RecordingPlaybackSink());

        await cut.FindAll("button[data-playback-queue-drag]")[0].TriggerEventAsync("onkeydown", new KeyboardEventArgs { Key = "ArrowDown", AltKey = true });

        Assert.Equal("Moved to position 2", cut.Find("p.sr-only[role='status']").TextContent);
        Assert.Empty(cut.FindAll("[data-queue-failure]"));
    }

    [Fact]
    public async Task Queue_AFailedMoveShowsAWarningNotice()
    {
        var sink = new RecordingPlaybackSink { Reply = new ListenPlaybackCommandReplyDto { Outcome = "failed", Message = "The move did not go through. Try again." } };
        var cut = RenderQueue(Queue(3), sink);

        await cut.FindAll("button[data-playback-queue-drag]")[0].TriggerEventAsync("onkeydown", new KeyboardEventArgs { Key = "ArrowDown", AltKey = true });

        Assert.Contains("The move did not go through", cut.Find("[data-queue-failure]").TextContent);
        Assert.Equal(string.Empty, cut.Find("p.sr-only[role='status']").TextContent);
    }

    [Fact]
    public async Task Queue_HistoryListsThisSessionWithRelativeTimeAndPlaysFromTheRowMenu()
    {
        var played = Song("Old song", "Some artist", "3:00") with { PlayedAt = DateTimeOffset.UtcNow.AddMinutes(-3.5) };
        var snapshot = Queue(1) with { History = [played] };
        var sink = new RecordingPlaybackSink();
        var cut = RenderQueue(snapshot, sink);

        await cut.Find("[role='tab'][data-playback-list-tab='history']").ClickAsync();

        var row = cut.Find("[data-playback-list-row='history']");
        Assert.Equal("Old song", row.QuerySelector(".plr__title")!.TextContent);
        Assert.Equal("3 min ago", row.QuerySelector(".plr__length")!.TextContent);
        await cut.InvokeAsync(() => cut.FindComponents<PlaybackRowMenu>()[0].Instance.OpenAsync());
        await cut.Find("button[data-playback-row-action='play']").ClickAsync();
        var sent = Assert.Single(sink.Commands);
        Assert.Equal(ListenPlaybackCommandActions.PlayHistory, sent.Command.Action);
        Assert.Equal(played.QueueEntryId, sent.Command.QueueEntryId);
    }

    [Fact]
    public async Task Queue_HistoryWithNothingPlayedSaysSo()
    {
        var cut = RenderQueue(Queue(1), new RecordingPlaybackSink(), tab: "history", showTabs: false);

        Assert.Contains("Played songs appear here for this session.", cut.Markup);
        Assert.Empty(cut.FindAll("[role='tablist']"));
        await Task.CompletedTask;
    }

    [Fact]
    public void Queue_TheTabsAreOneTabStopWithPanelsTheyControl()
    {
        var cut = RenderQueue(Queue(2), new RecordingPlaybackSink());

        var tabs = cut.FindAll("[role='tab']");
        Assert.Equal(new[] { "0", "-1" }, tabs.Select(tab => tab.GetAttribute("tabindex")));
        var panel = cut.Find("[role='tabpanel']");
        Assert.All(tabs, tab => Assert.Equal(panel.Id, tab.GetAttribute("aria-controls")));
        Assert.Equal(tabs[0].Id, panel.GetAttribute("aria-labelledby"));
    }

    [Fact]
    public void Queue_ALongQueueDrawsOnlyTheRowsOnScreen()
    {
        var cut = RenderQueue(Queue(200), new RecordingPlaybackSink());

        Assert.Equal("Up next · 200", cut.Find("[data-queue-heading]").TextContent);
        Assert.True(cut.FindAll("[data-playback-list-row='queue']").Count < 200, "A 200 song queue must not draw 200 rows.");
    }

    [Fact]
    public void Queue_WithTabsOpensOnTheHistoryTabWhenTheHostAsksForIt()
    {
        var cut = RenderQueue(Queue(2), new RecordingPlaybackSink(), showTabs: true, tab: "history");

        var tabs = cut.FindAll("[role='tab']");
        Assert.Equal(new[] { "false", "true" }, tabs.Select(tab => tab.GetAttribute("aria-selected")));
        Assert.Contains("Played songs appear here for this session.", cut.Markup);
        Assert.Empty(cut.FindAll("[data-queue-clear]"));
    }

    [Fact]
    public void Queue_AChangedRequestMovesAnOpenPanelButTheListenersOwnClickWinsUntilThen()
    {
        var snapshot = Queue(2);
        var cut = RenderQueue(snapshot, new RecordingPlaybackSink(), showTabs: true, tab: "upnext");
        Assert.Equal("true", cut.FindAll("[role='tab']")[0].GetAttribute("aria-selected"));

        cut.FindAll("[role='tab']")[1].Click();
        Assert.Equal("true", cut.FindAll("[role='tab']")[1].GetAttribute("aria-selected"));
        cut.Render();
        Assert.Equal("true", cut.FindAll("[role='tab']")[1].GetAttribute("aria-selected"));

        cut.Render(p => p.Add(x => x.ActiveTab, "history"));
        cut.Render(p => p.Add(x => x.ActiveTab, "upnext"));
        Assert.Equal("true", cut.FindAll("[role='tab']")[0].GetAttribute("aria-selected"));
    }

    [Fact]
    public async Task Queue_RemovingARowMovesFocusToTheNextRowsHandleThenAgainOnceTheNewQueueHasDrawn()
    {
        var module = JSInterop.SetupModule("./js/playback-queue-drag.js");
        module.Mode = JSRuntimeMode.Loose;
        var snapshot = Queue(3);
        var cut = RenderQueue(snapshot, new RecordingPlaybackSink());

        await ChooseRowAction(cut, 0, "remove");

        var first = Assert.Single(module.Invocations["focusHandle"]);
        Assert.Equal(snapshot.Queue[2].QueueEntryId.ToString(), first.Arguments[1]);
        var next = snapshot with { Queue = snapshot.Queue.Where((_, index) => index != 1).ToList(), QueueRevision = snapshot.QueueRevision + 1 };
        cut.Render(p => p.Add(x => x.Snapshot, next));
        Assert.Equal(2, module.Invocations["focusHandle"].Count);
        Assert.Equal(snapshot.Queue[2].QueueEntryId.ToString(), module.Invocations["focusHandle"][1].Arguments[1]);
    }

    [Fact]
    public async Task Queue_RemovingTheLastRowFallsBackToThePreviousOneAndTheOnlyRowToTheList()
    {
        var module = JSInterop.SetupModule("./js/playback-queue-drag.js");
        module.Mode = JSRuntimeMode.Loose;
        var snapshot = Queue(2);
        var cut = RenderQueue(snapshot, new RecordingPlaybackSink());

        await ChooseRowAction(cut, 1, "remove");
        Assert.Equal(snapshot.Queue[1].QueueEntryId.ToString(), Assert.Single(module.Invocations["focusHandle"]).Arguments[1]);

        var alone = RenderQueue(Queue(1), new RecordingPlaybackSink());
        await ChooseRowAction(alone, 0, "remove");
        Assert.Null(module.Invocations["focusHandle"][1].Arguments[1]);
    }

    [Fact]
    public void Queue_DrawsNoFooterStripWithoutASourceOrWhenTheHostLeavesItOut()
    {
        Assert.NotEmpty(RenderQueue(Queue(2), new RecordingPlaybackSink()).FindAll(".playback-list-panel__footer"));
        Assert.Empty(RenderQueue(Queue(2, source: null), new RecordingPlaybackSink()).FindAll(".playback-list-panel__footer"));
        var left = Render<QueuePanel>(p => p.Add(x => x.Snapshot, Queue(2)).Add(x => x.Commands, new RecordingPlaybackSink()).Add(x => x.ShowFooter, false));
        Assert.Empty(left.FindAll(".playback-list-panel__footer"));
        Assert.Empty(left.FindAll("[data-queue-source]"));
    }

    [Theory]
    [InlineData("lyrics", true, "lyrics")]
    [InlineData("queue", true, "queue")]
    [InlineData("chapters", false, "chapters")]
    [InlineData("history", true, "queue")]
    [InlineData("history", false, "ordinary")]
    [InlineData("anything-else", true, "ordinary")]
    public void TheBarsCompactPanelsUseTheKindThatMatchesTheirContent(string key, bool isMusic, string expected)
    {
        Assert.Equal(expected, ListenNowPlayingBar.CompactPanelKind(key, isMusic));
    }
}
