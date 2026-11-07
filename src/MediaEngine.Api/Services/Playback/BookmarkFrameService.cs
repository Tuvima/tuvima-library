using MediaEngine.Contracts.Playback;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Storage.Playback;

namespace MediaEngine.Api.Services.Playback;

/// <summary>A bookmark frame on disk, and whether the file it came from is in the Vault.</summary>
public sealed record BookmarkFrameFile(string Path, bool InVault);

/// <summary>
/// The still frame of every video bookmark (Unified player v2 D21a): queues the small background job that takes the picture right
/// after a bookmark is saved (so saving stays instant), tells a bookmark list which bookmarks have a picture yet, removes a picture
/// with its bookmark, and keeps the cache honest (an orphan sweep, and a gradual pass that gives older video bookmarks their frame).
/// Only video files get frames; audiobooks and music use the cover.
/// </summary>
public sealed class BookmarkFrameService(
    IMediaOperationRepository operations,
    IMediaAssetRepository assets,
    IMediaTypeExtensionCatalog extensions,
    AudiobookBookmarkRepository bookmarks,
    IVaultLibraryRepository vault,
    BookmarkFrameCache cache,
    ILogger<BookmarkFrameService> logger)
{
    /// <summary>Behind the default work (100), ahead of the scrub previews (10,000): one frame is a moment's work and a person is waiting to see it.</summary>
    public const int FramePriority = 1_000;

    private const int BackfillPageSize = 200;

    // Where the gradual pass over older bookmarks stopped; it starts a new round from the top when it reaches the end.
    private int _backfillOffset;

    public static string KeyFor(Guid bookmarkId, double positionSeconds) =>
        $"bookmark-frame:{bookmarkId:N}:{(long)Math.Round(positionSeconds * 1000)}";

    /// <summary>
    /// Where the Engine serves a bookmark's frame (the frame route in the player endpoints). The route ignores <c>v</c>; it changes
    /// whenever the frame is made again, so a browser never shows a picture of the old moment.
    /// </summary>
    public static string AddressFor(Guid bookmarkId, long version) =>
        $"/api/v1/player/bookmarks/{bookmarkId:D}/frame?v={version.ToString(System.Globalization.CultureInfo.InvariantCulture)}";

    /// <summary>
    /// Queues the frame of a bookmark at its time. Does nothing (and returns false) for a file that is not a video, or when the same job
    /// is already queued or done. With <paramref name="timeChanged"/> the old picture goes at once, so the old moment is never shown for
    /// the new time.
    /// </summary>
    public async Task<bool> EnqueueAsync(
        Guid bookmarkId, Guid assetId, double positionSeconds, CancellationToken ct, bool timeChanged = false)
    {
        var asset = await assets.FindByIdAsync(assetId, ct).ConfigureAwait(false);
        if (asset is null || !IsVideo(asset.FilePathRoot))
        {
            return false;
        }

        if (timeChanged)
        {
            cache.Delete(assetId, bookmarkId);
        }

        var key = KeyFor(bookmarkId, positionSeconds);
        var existing = await operations.GetByIdempotencyKeyAsync(key, ct).ConfigureAwait(false);
        if (existing is not null)
        {
            if (existing.Status == MediaOperationStatus.Succeeded && !cache.Exists(assetId, bookmarkId))
            {
                await operations.RequeueAsync(existing.Id, ct).ConfigureAwait(false);
                return true;
            }

            return false;
        }

        await operations.EnsureAsync(new MediaOperation
        {
            OperationType = MediaOperationType.PlaybackBookmarkFrame,
            OperationKind = MediaOperationKind.Maintenance,
            EntityId = bookmarkId,
            EntityKind = "Bookmark",
            SourcePath = asset.FilePathRoot,
            Status = MediaOperationStatus.Queued,
            Stage = MediaOperationStage.Queued,
            Priority = FramePriority,
            QueueName = MediaOperationQueue.BookmarkFrames,
            IdempotencyKey = key,
        }, ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Queues the frame of a bookmark that was just saved or changed. The bookmark is already saved, so a problem here never fails the
    /// save: it is logged, and the gradual pass queues the frame later.
    /// </summary>
    public async Task TryEnqueueAsync(AudiobookBookmarkDto bookmark, CancellationToken ct, bool timeChanged = false)
    {
        try
        {
            await EnqueueAsync(bookmark.Id, bookmark.AssetId, bookmark.PositionSeconds, ct, timeChanged).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "The frame for bookmark {BookmarkId} could not be queued; it will be queued again later.", bookmark.Id);
        }
    }

    /// <summary>Fills in <see cref="AudiobookBookmarkDto.FrameUrl"/> for the bookmarks whose picture exists.</summary>
    public IReadOnlyList<AudiobookBookmarkDto> Decorate(IReadOnlyList<AudiobookBookmarkDto> list) =>
        list.Count == 0 ? list : list.Select(Decorate).ToList();

    public AudiobookBookmarkDto Decorate(AudiobookBookmarkDto bookmark) =>
        cache.Version(bookmark.AssetId, bookmark.Id) is { } version ? bookmark with { FrameUrl = AddressFor(bookmark.Id, version) } : bookmark;

    /// <summary>
    /// The file of a bookmark's finished frame, or null when it has not been made. <see cref="BookmarkFrameFile.InVault"/> tells the
    /// route to send it uncacheable, as every other response about a Vault file is.
    /// </summary>
    public async Task<BookmarkFrameFile?> ResolveFrameAsync(AudiobookBookmarkDto bookmark, CancellationToken ct)
    {
        var path = cache.Resolve(bookmark.AssetId, bookmark.Id);
        return path is null ? null : new BookmarkFrameFile(path, await vault.IsInVaultAsync(bookmark.AssetId, ct).ConfigureAwait(false));
    }

    /// <summary>Removes a bookmark's frame (it goes with the bookmark).</summary>
    public void RemoveFrame(Guid assetId, Guid bookmarkId) => cache.Delete(assetId, bookmarkId);

    /// <summary>Removes the frames of bookmarks that an import or a promote just replaced, all on one file.</summary>
    public void RemoveFrames(Guid assetId, IEnumerable<Guid> bookmarkIds)
    {
        foreach (var id in bookmarkIds)
        {
            cache.Delete(assetId, id);
        }
    }

    /// <summary>
    /// Removes the frames whose bookmark no longer exists, however it went (removed with its profile, or any path that does not remove
    /// its own frames), and temporary files a crash left behind. Runs in the background pass, never inside a request. Returns how many
    /// frames were removed.
    /// </summary>
    public async Task<int> SweepOrphansAsync(CancellationToken ct)
    {
        var frames = cache.ListFrames();
        var removed = 0;
        if (frames.Count > 0)
        {
            var alive = await bookmarks.ExistingIdsAsync(frames.Select(frame => frame.BookmarkId).Distinct().ToList(), ct).ConfigureAwait(false);
            foreach (var frame in frames.Where(frame => !alive.Contains(frame.BookmarkId)))
            {
                if (cache.Delete(frame.AssetId, frame.BookmarkId))
                {
                    removed++;
                }
            }
        }

        cache.RemoveLeftovers(TimeSpan.FromDays(1));
        if (removed > 0)
        {
            logger.LogInformation("Removed {Count} bookmark frame(s) whose bookmark no longer exists.", removed);
        }

        return removed;
    }

    /// <summary>
    /// One gradual pass over the bookmarks already saved (those from before frames existed, restored from the Recycle bin, or whose
    /// first attempt was lost): queues up to <paramref name="perPass"/> video bookmarks that have no frame. Returns how many were queued.
    /// </summary>
    public async Task<int> BackfillAsync(int perPass, CancellationToken ct)
    {
        var videoExtensions = extensions.GetAllMediaExtensions().Where(extensions.IsVideoExtension).ToList();
        var page = await bookmarks.ListVideoBookmarksAsync(videoExtensions, _backfillOffset, BackfillPageSize, ct).ConfigureAwait(false);
        if (page.Count == 0)
        {
            _backfillOffset = 0;
            return 0;
        }

        var queued = 0;
        var consumed = 0;
        foreach (var bookmark in page)
        {
            if (queued >= perPass)
            {
                break;
            }

            consumed++;
            if (!cache.Exists(bookmark.AssetId, bookmark.Id)
                && await EnqueueAsync(bookmark.Id, bookmark.AssetId, bookmark.PositionSeconds, ct).ConfigureAwait(false))
            {
                queued++;
            }
        }

        _backfillOffset += consumed;
        return queued;
    }

    private bool IsVideo(string filePath) => extensions.IsVideoExtension(Path.GetExtension(filePath));
}
