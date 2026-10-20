using MediaEngine.Web.Services.Playback;

namespace MediaEngine.Web.Tests;

/// <summary>What a player does when a title is deleted from the library while it is playing or queued.</summary>
public sealed class DeleteFromLibraryPlaybackTests
{
    private static ListenQueueItem Song(string title) => new()
    {
        WorkId = Guid.NewGuid(),
        AssetId = Guid.NewGuid(),
        MediaType = "Music",
        Title = title,
        StreamUrl = $"stream://{title}",
    };

    [Fact]
    public async Task WhenTheCurrentItemIsDeleted_ThePlayerStopsAndSaysSo()
    {
        var current = Song("Playing now");
        var next = Song("Up next");
        var player = new PlaybackSessionController(null!, null!);
        await player.ReplaceQueueItemsAsync([current, next], 0, "Album", shuffle: false);
        Assert.Equal(current.WorkId, player.CurrentItem?.WorkId);

        var stopped = await player.HandleLibraryItemsRemovedAsync([current.WorkId], []);

        Assert.True(stopped);
        Assert.True(player.IsDismissed);
        Assert.Null(player.CurrentItem);
        Assert.False(player.IsPlaying);
        Assert.Empty(player.Queue);
    }

    [Fact]
    public async Task WhenTheCurrentFileIsDeletedByItsAssetId_ThePlayerStopsToo()
    {
        var current = Song("Playing now");
        var player = new PlaybackSessionController(null!, null!);
        await player.ReplaceQueueItemsAsync([current, Song("Up next")], 0, "Album", shuffle: false);

        var stopped = await player.HandleLibraryItemsRemovedAsync([], [current.AssetId!.Value]);

        Assert.True(stopped);
        Assert.True(player.IsDismissed);
    }

    [Fact]
    public async Task WhenOnlyUpcomingItemsAreDeleted_PlaybackContinuesAndTheyLeaveTheQueue()
    {
        var current = Song("Playing now");
        var gone = Song("Deleted");
        var kept = Song("Kept");
        var player = new PlaybackSessionController(null!, null!);
        await player.ReplaceQueueItemsAsync([current, gone, kept], 0, "Album", shuffle: false);

        var stopped = await player.HandleLibraryItemsRemovedAsync([gone.WorkId], []);

        Assert.False(stopped);
        Assert.False(player.IsDismissed);
        Assert.Equal(current.WorkId, player.CurrentItem?.WorkId);
        Assert.Equal([current.WorkId, kept.WorkId], player.Queue.Select(item => item.WorkId));
        Assert.Equal([kept.WorkId], player.UpcomingQueue.Select(item => item.WorkId));
    }

    [Fact]
    public async Task WhenSomethingElseIsDeleted_NothingChanges()
    {
        var current = Song("Playing now");
        var player = new PlaybackSessionController(null!, null!);
        await player.ReplaceQueueItemsAsync([current, Song("Up next")], 0, "Album", shuffle: false);

        var stopped = await player.HandleLibraryItemsRemovedAsync([Guid.NewGuid()], [Guid.NewGuid()]);

        Assert.False(stopped);
        Assert.Equal(2, player.Queue.Count);
        Assert.Equal(current.WorkId, player.CurrentItem?.WorkId);
    }

    [Fact]
    public async Task WhenAnAudiobookIsDeleted_ThePlayerStopsEvenThoughItPlaysOneOfItsChapterWorks()
    {
        var bookWorkId = Guid.NewGuid();
        var chapter = new ListenQueueItem
        {
            WorkId = Guid.NewGuid(),
            AudiobookWorkId = bookWorkId,
            AssetId = Guid.NewGuid(),
            MediaType = "Audiobooks",
            Title = "Chapter 1",
            StreamUrl = "stream://chapter-1",
        };
        var player = new PlaybackSessionController(null!, null!);
        await player.PlayAudiobookAsync(chapter);

        var stopped = await player.HandleLibraryItemsRemovedAsync([bookWorkId], []);

        Assert.True(stopped);
        Assert.True(player.IsDismissed);
    }
}
