using MediaEngine.Web.Services.Playback;

namespace MediaEngine.Web.Tests;

public sealed class PlaybackArtworkUrlTests
{
    [Fact]
    public void BookQueuePrefersManagedParentCoverAndRequestsABoundedRendition()
    {
        var bookId = Guid.NewGuid();
        const string cover = "/engine-image/stream/artwork/11111111-1111-1111-1111-111111111111";
        var queued = PlaybackArtworkUrl.AudiobookCover(bookId, cover);
        Assert.Equal(cover + "?size=m", queued);
        Assert.Equal(cover + "?size=s", PlaybackArtworkUrl.ForItem(new ListenQueueItem
        { WorkId = Guid.NewGuid(), AudiobookWorkId = bookId, MediaType = "Audiobooks", CoverUrl = queued }, "s"));
    }

    [Theory]
    [InlineData("/engine-image/stream/22222222-2222-2222-2222-222222222222/cover")]
    [InlineData("/stream/22222222-2222-2222-2222-222222222222/cover")]
    [InlineData("/engine-image/stream/22222222-2222-2222-2222-222222222222/cover?size=m")]
    public void RestoredRecordingCoverUsesCanonicalBookIdentityWithoutRequestingTheOriginal(string legacy)
    {
        var bookId = Guid.NewGuid();
        var item = new ListenQueueItem
        {
            WorkId = Guid.NewGuid(),
            AudiobookWorkId = bookId,
            AssetId = Guid.Parse("22222222-2222-2222-2222-222222222222"),
            MediaType = "Audiobooks",
            CoverUrl = legacy
        };
        Assert.Equal($"/engine-image/stream/entity/work/{bookId:D}/cover?size=m", PlaybackArtworkUrl.ForItem(item));
        Assert.Equal($"/engine-image/stream/entity/work/{item.WorkId:D}/cover?size=m", PlaybackArtworkUrl.ForItem(item with { MediaType = "Music" }));
        Assert.Null(PlaybackArtworkUrl.ForItem(item with { WorkId = Guid.Empty, AudiobookWorkId = null }));
        Assert.Null(PlaybackArtworkUrl.ForItem(item, "original"));
    }

    [Fact]
    public void ExistingDeliveredBoundedSourceIsRetainedWithoutInventingOtherRenditions()
    {
        const string delivered = "/api/v1/images/owned-cover?size=m#preview";
        var item = new ListenQueueItem { WorkId = Guid.NewGuid(), MediaType = "Audiobook", CoverUrl = delivered };
        Assert.Equal(delivered, PlaybackArtworkUrl.ForItem(item));
        Assert.Equal(delivered, PlaybackArtworkUrl.ForItem(item, "s"));
        Assert.Equal($"/engine-image/stream/entity/work/{item.WorkId:D}/cover?size=m", PlaybackArtworkUrl.ForItem(item with { MediaType = "Music", CoverUrl = "/images/original.jpg" }));
    }
}
