using Bunit;
using MediaEngine.Contracts.Playback;
using MediaEngine.Web.Components.Listen;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Playback;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

public sealed class PlaybackContextPanelTests : AsyncBunitContext
{
    public PlaybackContextPanelTests() { JSInterop.Mode = JSRuntimeMode.Loose; Services.AddLogging(); Services.AddLocalization(); Services.AddNativeUiServices(); }

    [Fact]
    public async Task ExplicitSnapshotNeedsNoControllerAndCommandsKeepTheRenderedOccurrence()
    {
        var item = new ListenQueueItem { WorkId = Guid.NewGuid(), AssetId = Guid.NewGuid(), Title = "Repeated", MediaType = "Music" };
        var snapshot = new ListenPlaybackSnapshot
        {
            ProfileId = Guid.NewGuid(),
            PlaybackRequestVersion = 9,
            CurrentIndex = 0,
            Queue = [item with { QueueEntryId = Guid.NewGuid(), Title = "Current" }, item with { QueueEntryId = Guid.NewGuid() }, item with { QueueEntryId = Guid.NewGuid() }]
        };
        var sink = new RecordingSink();
        var cut = Render<ListenContextSidebar>(parameters => parameters.Add(p => p.Snapshot, snapshot).Add(p => p.Commands, sink).Add(p => p.ActivePanelKey, "queue"));
        Assert.Empty(cut.FindAll("[role='tablist']"));
        Assert.Equal("NOW PLAYING", cut.Find(".listen-context-sidebar__now-playing h3").TextContent);
        Assert.Equal("UP NEXT · 2", cut.Find(".listen-context-sidebar__section-action h3").TextContent);
        Assert.Single(cut.FindAll(".listen-context-sidebar__now-playing .playback-context-row.is-current"));
        await cut.InvokeAsync(() => cut.FindComponents<PlaybackPopover>().Last(p => p.Instance.Title == "Queue actions for Repeated").Instance.OpenAsync(true));
        await cut.FindAll("button[aria-label='Remove Repeated from queue']").Last().ClickAsync();
        var remove = Assert.Single(sink.Commands);
        Assert.Same(snapshot, remove.Snapshot);
        Assert.Equal(snapshot.Queue[2].QueueEntryId, remove.Command.QueueEntryId);
        Assert.Equal(ListenPlaybackCommandActions.RemoveUpcoming, remove.Command.Action);
        await cut.Find("button[aria-label='Clear upcoming queue']").ClickAsync();
        Assert.Same(snapshot, sink.Commands[1].Snapshot);
        Assert.Equal(ListenPlaybackCommandActions.ClearUpcoming, sink.Commands[1].Command.Action);
    }

    [Fact]
    public async Task ChapterActivityFollowsSnapshotAndHistoryKeepsTheOriginalListeningSegment()
    {
        var asset = Guid.NewGuid();
        var item = new ListenQueueItem
        {
            WorkId = Guid.NewGuid(),
            AssetId = asset,
            Title = "Book",
            MediaType = "Audiobook",
            CoverUrl = "/cover",
            Chapters = [new() { Index = 0, AssetId = asset, Title = "Source intro", StartSeconds = 0, EndSeconds = 30 }, new() { Index = 1, AssetId = asset, Title = "Source chapter", StartSeconds = 30, EndSeconds = 90 }]
        };
        var entry = new AudiobookListenHistoryItemDto
        {
            AssetId = asset,
            WorkId = item.WorkId,
            Title = "Book",
            ChapterTitle = "Source chapter",
            PositionSeconds = 44,
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-2),
            EndedAt = DateTimeOffset.UtcNow
        };
        var snapshot = new ListenPlaybackSnapshot { Queue = [item], CurrentIndex = 0, CurrentTimeSeconds = 35, IsPlaying = false, AudiobookHistory = [entry] };
        var sink = new RecordingSink();
        var cut = Render<PlaybackContextPanel>(parameters => parameters.Add(p => p.Snapshot, snapshot).Add(p => p.Commands, sink).Add(p => p.ActivePanelKey, "chapters"));
        Assert.Empty(cut.FindAll("img"));
        Assert.Single(cut.FindAll(".playback-context-row--chapter[aria-current='true']"));
        Assert.Contains("Current item paused", cut.Markup);
        await cut.Find("button[aria-label='Play Source intro']").ClickAsync();
        Assert.Equal(0, Assert.Single(sink.Commands).Command.ChapterIndex);
        cut.Render(parameters => parameters.Add(p => p.Snapshot, snapshot with { IsPlaying = true }).Add(p => p.Commands, sink).Add(p => p.ActivePanelKey, "chapters"));
        Assert.Contains("Now playing", cut.Markup);
        cut.Render(parameters => parameters.Add(p => p.Snapshot, snapshot).Add(p => p.Commands, sink).Add(p => p.ActivePanelKey, "history"));
        Assert.Empty(cut.FindAll("img"));
        await cut.Find("button[aria-label='Resume Source chapter']").ClickAsync();
        Assert.Same(entry, sink.Commands.Last().Command.AudiobookHistoryItem);
        Assert.Equal(ListenPlaybackCommandActions.PlayAudiobookHistory, sink.Commands.Last().Command.Action);
    }

    [Fact]
    public void DesktopIdentityKeepsTheCompleteLongTitleWhileArtworkCanYieldAtShortHeights()
    {
        const string title = "The Collected Voyages of the Northern Cartographer: Across Uncharted Seas, Through Forgotten Cities, and Beyond the Last Recorded Horizon";
        var snapshot = new ListenPlaybackSnapshot
        {
            Queue = [new() { WorkId = Guid.NewGuid(), AssetId = Guid.NewGuid(), Title = title,
            MediaType = "Audiobook", CoverUrl = "/api/v1/images/test?size=m", AuthorName = "Source author" }],
            CurrentIndex = 0
        };
        var cut = Render<PlaybackDesktopScene>(parameters => parameters.Add(p => p.Snapshot, snapshot).Add(p => p.Commands, new RecordingSink()).Add(p => p.PanelKey, "history"));
        Assert.Equal(title, cut.Find("h1").TextContent);
        Assert.Equal("/api/v1/images/test?size=m", cut.Find("img[data-image-display='desktop-now-playing-artwork']").GetAttribute("src"));
        Assert.Single(cut.FindAll(".playback-desktop__stack > .playback-desktop__art"));
        Assert.Single(cut.FindAll(".playback-desktop__stack > .playback-desktop__identity"));
        Assert.Empty(cut.FindAll("button[aria-label='Close player']"));
        Assert.Single(cut.FindAll("[role='tablist']"));
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MediaEngine.slnx")))
        {
            directory = directory.Parent;
        }
        var css = File.ReadAllText(Path.Combine(directory!.FullName, "src/MediaEngine.Web/Components/Listen/PlaybackDesktopScene.razor.css"));
        Assert.DoesNotContain("line-clamp", css);
        Assert.Contains("overflow-wrap:anywhere", css);
    }

    [Fact]
    public void RestoredBookSceneRendersTheBoundedCanonicalCoverForBothSharpArtAndAtmosphere()
    {
        var bookId = Guid.NewGuid();
        var snapshot = new ListenPlaybackSnapshot
        {
            Queue = [new() { WorkId = Guid.NewGuid(), AudiobookWorkId = bookId,
            AssetId = Guid.NewGuid(), Title = "Book", MediaType = "Audiobook",
            CoverUrl = "/engine-image/stream/22222222-2222-2222-2222-222222222222/cover" }],
            CurrentIndex = 0
        };
        var cut = Render<PlaybackDesktopScene>(parameters => parameters.Add(p => p.Snapshot, snapshot).Add(p => p.Commands, new RecordingSink()).Add(p => p.PanelKey, "history"));
        var bounded = $"/engine-image/stream/entity/work/{bookId:D}/cover?size=m";
        Assert.Equal(bounded, cut.Find("img[data-image-display='desktop-now-playing-artwork']").GetAttribute("src"));
        Assert.Contains(bounded, cut.FindComponent<AppCssElement>().Instance.Css ?? string.Empty);
        Assert.DoesNotContain(snapshot.Queue[0].CoverUrl!, cut.Markup);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MusicSceneLinksArtistAndAlbumWhileTitleRemainsPlain(bool hasArtistIdentity)
    {
        var album = Guid.NewGuid(); var artist = Guid.NewGuid();
        var item = new ListenQueueItem { WorkId = Guid.NewGuid(), AssetId = Guid.NewGuid(), MediaType = "Music", Title = "Track title", Subtitle = "Artist & Ensemble", Album = "Album title", AlbumWorkId = album, ArtistPersonId = hasArtistIdentity ? artist : null };
        var snapshot = new ListenPlaybackSnapshot { CurrentIndex = 0, Queue = [item] };
        var cut = Render<PlaybackDesktopScene>(p => p.Add(c => c.Snapshot, snapshot).Add(c => c.Commands, new RecordingSink()).Add(c => c.PanelKey, "queue"));
        Assert.Empty(cut.FindAll("h1 a"));
        if (hasArtistIdentity)
        {
            Assert.Equal($"/details/person/{artist:D}", cut.Find(".playback-desktop__byline a").GetAttribute("href"));
        }
        else
        {
            Assert.Contains("Artist &amp; Ensemble", cut.Markup);
        }
        Assert.Equal($"/details/musicalbum/{album:D}?context=listen", cut.Find(".playback-desktop__secondary a").GetAttribute("href"));
        var tabs = cut.FindAll("[role='tab']");
        Assert.Equal("Lyrics", tabs[0].GetAttribute("aria-label"));
        Assert.Equal(string.Empty, tabs[0].TextContent.Trim());
        Assert.Equal(new[] { "Up Next", "History" }, tabs.Skip(1).Select(e => e.TextContent.Trim()).ToArray());
    }

    private static ListenPlaybackSnapshot QueueOfThree(out Guid second)
    {
        var item = new ListenQueueItem { WorkId = Guid.NewGuid(), AssetId = Guid.NewGuid(), Title = "Song", MediaType = "Music", Duration = "4:03" };
        var queue = new List<ListenQueueItem> { item with { QueueEntryId = Guid.NewGuid(), Title = "Current" }, item with { QueueEntryId = Guid.NewGuid(), Title = "Two" }, item with { QueueEntryId = Guid.NewGuid(), Title = "Three" } };
        second = queue[2].QueueEntryId;
        return new ListenPlaybackSnapshot { ProfileId = Guid.NewGuid(), CurrentIndex = 0, Queue = queue };
    }

    [Fact]
    public void UpNextRows_PutTheDragHandleInItsOwnColumnApartFromTheTime()
    {
        var cut = Render<ListenContextSidebar>(parameters => parameters.Add(p => p.Snapshot, QueueOfThree(out _)).Add(p => p.Commands, new RecordingSink()).Add(p => p.ActivePanelKey, "queue"));

        var row = cut.FindAll(".playback-queue-drop .playback-context-row").First();
        Assert.Contains("playback-context-row--handled", row.ClassName);
        Assert.NotNull(row.QuerySelector(".playback-context-row__handle button[data-playback-queue-drag]"));
        Assert.Null(row.QuerySelector(".playback-context-row__actions button[data-playback-queue-drag]"));
        Assert.Equal("4:03", row.QuerySelector(".playback-context-row__duration")!.TextContent);
    }

    [Fact]
    public async Task ASuccessfulMove_AddsNoVisibleTextButStillReachesScreenReaders()
    {
        var snapshot = QueueOfThree(out _);
        var cut = Render<ListenContextSidebar>(parameters => parameters.Add(p => p.Snapshot, snapshot).Add(p => p.Commands, new RecordingSink()).Add(p => p.ActivePanelKey, "queue"));

        await cut.FindAll("button[data-playback-queue-drag]").First().TriggerEventAsync("onkeydown", new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "ArrowDown", AltKey = true });

        var announcement = cut.Find("p.listen-context-sidebar__announce[role='status']");
        Assert.Equal("Moved to position 2", announcement.TextContent);
        Assert.Empty(cut.FindAll(".listen-context-sidebar__feedback"));
    }

    [Fact]
    public async Task AFailedMove_ShowsAVisibleWarningNotice()
    {
        var sink = new RecordingSink { Reply = new ListenPlaybackCommandReplyDto { Outcome = "failed", Message = "The move did not go through. Try again." } };
        var cut = Render<ListenContextSidebar>(parameters => parameters.Add(p => p.Snapshot, QueueOfThree(out _)).Add(p => p.Commands, sink).Add(p => p.ActivePanelKey, "queue"));

        await cut.FindAll("button[data-playback-queue-drag]").First().TriggerEventAsync("onkeydown", new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "ArrowDown", AltKey = true });

        Assert.Contains("The move did not go through", cut.Find(".listen-context-sidebar__feedback").TextContent);
        Assert.Equal(string.Empty, cut.Find("p.listen-context-sidebar__announce[role='status']").TextContent);
    }

    [Fact]
    public void Clear_UsesTheSharedButtonStyle()
    {
        var cut = Render<ListenContextSidebar>(parameters => parameters.Add(p => p.Snapshot, QueueOfThree(out _)).Add(p => p.Commands, new RecordingSink()).Add(p => p.ActivePanelKey, "queue"));

        Assert.NotNull(cut.FindComponent<AppButton>());
        Assert.Contains("listen-context-sidebar__clear", cut.Find("button[aria-label='Clear upcoming queue']").ClassName);
    }

    [Fact]
    public void MusicHistoryRows_ShowThePlayedTimeUnderTheTitle()
    {
        var played = new ListenQueueItem { WorkId = Guid.NewGuid(), AssetId = Guid.NewGuid(), Title = "Old", MediaType = "Music", QueueEntryId = Guid.NewGuid(), PlayedAt = DateTimeOffset.UtcNow.AddMinutes(-3) };
        var snapshot = new ListenPlaybackSnapshot { ProfileId = Guid.NewGuid(), CurrentIndex = -1, History = [played] };
        var cut = Render<ListenContextSidebar>(parameters => parameters.Add(p => p.Snapshot, snapshot).Add(p => p.Commands, new RecordingSink()).Add(p => p.ActivePanelKey, "history"));

        var row = cut.Find(".playback-context-row--history");
        Assert.Equal("Played 3 min ago", row.QuerySelector(".playback-context-row__identity span")!.TextContent);
        Assert.Null(row.QuerySelector(".playback-context-row__duration"));
    }

    private sealed class RecordingSink : IPlaybackCommandSink, IPlaybackIdentityNavigationSink
    {
        public ListenPlaybackCommandReplyDto? Reply { get; set; } = new();
        public Task<ListenPlaybackCommandReplyDto?> NavigateIdentityAsync(ListenPlaybackSnapshot snapshot, string kind, Guid id, CancellationToken ct = default) => Task.FromResult<ListenPlaybackCommandReplyDto?>(new());
        public List<(ListenPlaybackSnapshot Snapshot, ListenPlaybackCommandDto Command)> Commands { get; } = [];
        public bool Supports(string action, ListenPlaybackSnapshot snapshot) => true;
        public Task<ListenPlaybackCommandReplyDto?> SendAsync(ListenPlaybackSnapshot snapshot, ListenPlaybackCommandDto command, CancellationToken ct = default)
        { Commands.Add((snapshot, command)); return Task.FromResult(Reply); }
    }
}
