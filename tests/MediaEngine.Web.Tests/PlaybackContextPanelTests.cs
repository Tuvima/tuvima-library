using Bunit;
using MediaEngine.Contracts.Playback;
using MediaEngine.Web.Components.Listen;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Playback;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;

namespace MediaEngine.Web.Tests;

public sealed class PlaybackContextPanelTests : AsyncBunitContext
{
    public PlaybackContextPanelTests() { JSInterop.Mode = JSRuntimeMode.Loose; Services.AddLogging(); Services.AddLocalization(); Services.AddMudServices(); }

    [Fact]
    public async Task ExplicitSnapshotNeedsNoControllerAndCommandsKeepTheRenderedOccurrence()
    {
        var item = new ListenQueueItem { WorkId = Guid.NewGuid(), AssetId = Guid.NewGuid(), Title = "Repeated", MediaType = "Music" };
        var snapshot = new ListenPlaybackSnapshot { ProfileId = Guid.NewGuid(), PlaybackRequestVersion = 9, CurrentIndex = 0,
            Queue = [item with { QueueEntryId = Guid.NewGuid(), Title = "Current" }, item with { QueueEntryId = Guid.NewGuid() }, item with { QueueEntryId = Guid.NewGuid() }] };
        var sink = new RecordingSink();
        var cut = Render<ListenContextSidebar>(parameters => parameters.Add(p => p.Snapshot, snapshot).Add(p => p.Commands, sink).Add(p => p.ActivePanelKey, "queue"));
        Assert.Empty(cut.FindAll("[role='tablist']"));
        Assert.Contains("2 upcoming", cut.Markup);
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
        var item = new ListenQueueItem { WorkId = Guid.NewGuid(), AssetId = asset, Title = "Book", MediaType = "Audiobook", CoverUrl = "/cover",
            Chapters = [new() { Index = 0, AssetId = asset, Title = "Source intro", StartSeconds = 0, EndSeconds = 30 }, new() { Index = 1, AssetId = asset, Title = "Source chapter", StartSeconds = 30, EndSeconds = 90 }] };
        var entry = new AudiobookListenHistoryItemDto { AssetId = asset, WorkId = item.WorkId, Title = "Book", ChapterTitle = "Source chapter", PositionSeconds = 44,
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-2), EndedAt = DateTimeOffset.UtcNow };
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
        var snapshot = new ListenPlaybackSnapshot { Queue = [new() { WorkId = Guid.NewGuid(), AssetId = Guid.NewGuid(), Title = title,
            MediaType = "Audiobook", CoverUrl = "/api/v1/images/test?size=m", AuthorName = "Source author" }], CurrentIndex = 0 };
        var cut = Render<PlaybackDesktopScene>(parameters => parameters.Add(p => p.Snapshot, snapshot).Add(p => p.Commands, new RecordingSink()).Add(p => p.PanelKey, "history"));
        Assert.Equal(title, cut.Find("h1").TextContent);
        Assert.Equal("/api/v1/images/test?size=m", cut.Find("img[data-image-display='desktop-now-playing-artwork']").GetAttribute("src"));
        Assert.Single(cut.FindAll(".playback-desktop__stack > .playback-desktop__art"));
        Assert.Single(cut.FindAll(".playback-desktop__stack > .playback-desktop__identity"));
        Assert.Empty(cut.FindAll("button[aria-label='Close player']"));
        Assert.Single(cut.FindAll("[role='tablist']"));
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MediaEngine.slnx"))) directory = directory.Parent;
        var css = File.ReadAllText(Path.Combine(directory!.FullName, "src/MediaEngine.Web/Components/Listen/PlaybackDesktopScene.razor.css"));
        Assert.DoesNotContain("line-clamp", css);
        Assert.Contains("overflow-wrap:anywhere", css);
    }

    [Fact]
    public void RestoredBookSceneRendersTheBoundedCanonicalCoverForBothSharpArtAndAtmosphere()
    {
        var bookId = Guid.NewGuid();
        var snapshot = new ListenPlaybackSnapshot { Queue = [new() { WorkId = Guid.NewGuid(), AudiobookWorkId = bookId,
            AssetId = Guid.NewGuid(), Title = "Book", MediaType = "Audiobook",
            CoverUrl = "/engine-image/stream/22222222-2222-2222-2222-222222222222/cover" }], CurrentIndex = 0 };
        var cut = Render<PlaybackDesktopScene>(parameters => parameters.Add(p => p.Snapshot, snapshot).Add(p => p.Commands, new RecordingSink()).Add(p => p.PanelKey, "history"));
        var bounded = $"/engine-image/stream/entity/work/{bookId:D}/cover?size=m";
        Assert.Equal(bounded, cut.Find("img[data-image-display='desktop-now-playing-artwork']").GetAttribute("src"));
        Assert.Contains(bounded, cut.FindComponent<AppCssElement>().Instance.Css ?? string.Empty);
        Assert.DoesNotContain(snapshot.Queue[0].CoverUrl!, cut.Markup);
    }

    private sealed class RecordingSink : IPlaybackCommandSink
    {
        public List<(ListenPlaybackSnapshot Snapshot, ListenPlaybackCommandDto Command)> Commands { get; } = [];
        public bool Supports(string action, ListenPlaybackSnapshot snapshot) => true;
        public Task<ListenPlaybackCommandReplyDto?> SendAsync(ListenPlaybackSnapshot snapshot, ListenPlaybackCommandDto command, CancellationToken ct = default)
        { Commands.Add((snapshot, command)); return Task.FromResult<ListenPlaybackCommandReplyDto?>(new()); }
    }
}
