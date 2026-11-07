using Dapper;
using MediaEngine.Api.Services.Playback;
using MediaEngine.Contracts.Playback;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Entities;
using MediaEngine.Processors;
using MediaEngine.Storage;
using MediaEngine.Storage.Playback;
using MediaEngine.Storage.Services;
using MediaEngine.TestSupport;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;

namespace MediaEngine.Api.Tests;

/// <summary>
/// The still frame of a video bookmark (Unified player v2 D21a): queued by the save, made by a real FFmpeg in the background worker,
/// kept in the Library's own cache, removed with its bookmark, and never made for audio. Real data store, real cache folder; only
/// FFmpeg is swappable. The access rule for serving a frame is checked beside the bookmark list's own tests.
/// </summary>
public sealed class BookmarkFrameTests : IDisposable
{
    private readonly ScrubPreviewWorld _world = new();
    private readonly AudiobookBookmarkRepository _bookmarks;
    private readonly BookmarkFrameCache _cache;
    private readonly BookmarkFrameService _frames;

    public BookmarkFrameTests()
    {
        _bookmarks = new AudiobookBookmarkRepository(_world.Database);
        _cache = new BookmarkFrameCache(_world.AssetPaths);
        _frames = new BookmarkFrameService(
            _world.Operations, _world.Assets, new MediaTypeExtensionCatalog(), _bookmarks, new VaultLibraryRepository(_world.Database), _cache, NullLogger<BookmarkFrameService>.Instance);
    }

    public void Dispose() => _world.Dispose();

    private BookmarkFrameOperationWorker Worker(ScrubPreviewWorld? world = null, ILogger<BookmarkFrameOperationWorker>? logger = null)
    {
        world ??= _world;
        return new BookmarkFrameOperationWorker(
            world.Operations, new AudiobookBookmarkRepository(world.Database), world.Assets, new VideoFrameExtractor(world.Ffmpeg),
            new BookmarkFrameCache(world.AssetPaths), logger ?? NullLogger<BookmarkFrameOperationWorker>.Instance);
    }

    private static Guid WorkOf(ScrubPreviewWorld world, Guid assetId)
    {
        using var connection = world.Database.CreateConnection();
        return connection.ExecuteScalar<Guid>(
            "SELECT e.work_id FROM editions e JOIN media_assets ma ON ma.edition_id = e.id WHERE ma.id = @assetId;", new { assetId });
    }

    private async Task<AudiobookBookmarkDto> SaveBookmarkAsync(Guid assetId, double seconds)
    {
        var saved = await _bookmarks.CreateAsync(
            Profile.SeedProfileId,
            WorkOf(_world, assetId),
            new CreateAudiobookBookmarkRequestDto { AssetId = assetId, PositionSeconds = seconds, DurationSeconds = 600 });
        await _frames.TryEnqueueAsync(saved, CancellationToken.None);
        return saved;
    }

    [RealFfmpegFact]
    public async Task SavingABookmarkOnAVideo_ProducesA320WideStill_WithinTheWorkersFirstRun()
    {
        var assetId = await _world.AddVideoAsync("film.mp4", seconds: 12);

        var saved = await SaveBookmarkAsync(assetId, 7.5);

        // The save did not wait for the picture; the background worker makes it.
        Assert.False(_cache.Exists(assetId, saved.Id));
        var operation = Assert.Single(await _world.Operations.GetByEntityAsync(saved.Id));
        Assert.Equal(MediaOperationType.PlaybackBookmarkFrame, operation.OperationType);
        Assert.Equal(MediaOperationQueue.BookmarkFrames, operation.QueueName);
        Assert.Equal(BookmarkFrameService.FramePriority, operation.Priority);
        Assert.True(BookmarkFrameService.FramePriority < ScrubPreviewQueue.LowestPriority, "a person waiting for a frame goes before background previews");

        var started = DateTimeOffset.UtcNow;
        await Worker().RunOnceAsync(CancellationToken.None);

        Assert.True(DateTimeOffset.UtcNow - started < TimeSpan.FromSeconds(30));
        var path = _cache.Resolve(assetId, saved.Id);
        Assert.NotNull(path);
        using var codec = SKCodec.Create(path);
        Assert.NotNull(codec);
        Assert.Equal(SKEncodedImageFormat.Jpeg, codec.EncodedFormat);
        Assert.Equal(BookmarkFrameCache.FrameWidth, codec.Info.Width);
        Assert.Equal(MediaOperationStatus.Succeeded, (await _world.Operations.GetByIdAsync(operation.Id))!.Status);
    }

    [RealFfmpegFact]
    public async Task TheFrameAppearsOnTheBookmarkListOnlyOnceItExists()
    {
        var assetId = await _world.AddVideoAsync("film.mp4", seconds: 12);
        var saved = await SaveBookmarkAsync(assetId, 4);
        Assert.Null(_frames.Decorate(saved).FrameUrl);

        await Worker().RunOnceAsync(CancellationToken.None);

        Assert.Equal($"/api/v1/player/bookmarks/{saved.Id:D}/frame", _frames.Decorate(saved).FrameUrl);
    }

    [Fact]
    public void TheFrameRoute_NeedsTheProgressReadScope_AndMatchesTheAddressTheListHandsOut()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "src", "MediaEngine.Api", "Endpoints", "PlayerEndpoints.cs"));
        var start = source.IndexOf("group.MapGet(\"/bookmarks/{bookmarkId:guid}/frame\"", StringComparison.Ordinal);
        Assert.True(start >= 0, "the frame route is mapped");
        var route = source[start..source.IndexOf("group.MapPost(", start, StringComparison.Ordinal)];

        Assert.Contains(".RequireClientScope(ClientApiScopes.ProgressRead)", route, StringComparison.Ordinal);
        Assert.Contains("player.GetBookmarkFramePathAsync(", route, StringComparison.Ordinal);
        Assert.Equal("/api/v1/player/bookmarks/" + Guid.Empty.ToString("D") + "/frame", BookmarkFrameService.AddressFor(Guid.Empty));
    }

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MediaEngine.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("The repository root was not found.");
    }

    [Fact]
    public async Task AnAudioBookmark_IsNeverQueued_AndGetsNoFrame()
    {
        var assetId = await _world.AddVideoAsync("book.m4a", seconds: 5);

        var saved = await SaveBookmarkAsync(assetId, 30);

        Assert.Empty(await _world.Operations.GetByEntityAsync(saved.Id));
        Assert.False(await _frames.EnqueueAsync(saved.Id, assetId, 30, CancellationToken.None));
        Assert.Null(_frames.Decorate(saved).FrameUrl);
        Assert.False(Directory.Exists(_cache.Root));
    }

    [Fact]
    public async Task ABookmarkIsQueuedOnce_AndANewTimeQueuesAgainAndDropsTheOldPicture()
    {
        var assetId = await _world.AddVideoAsync("film.mp4", seconds: 5, makeFile: false);
        var saved = await SaveBookmarkAsync(assetId, 60);
        _cache.Write(assetId, saved.Id, [0xFF, 0xD8, 0xFF, 0xD9]);

        Assert.False(await _frames.EnqueueAsync(saved.Id, assetId, 60, CancellationToken.None));
        Assert.Single(await _world.Operations.GetByEntityAsync(saved.Id));

        // The time changed: the picture of the old moment must not be shown for the new one.
        Assert.True(await _frames.EnqueueAsync(saved.Id, assetId, 75, CancellationToken.None, timeChanged: true));
        Assert.Equal(2, (await _world.Operations.GetByEntityAsync(saved.Id)).Count);
        Assert.False(_cache.Exists(assetId, saved.Id));
    }

    [Fact]
    public async Task AFinishedJobWhosePictureWasRemoved_GoesBackInTheQueue()
    {
        var assetId = await _world.AddVideoAsync("film.mp4", seconds: 5, makeFile: false);
        var saved = await SaveBookmarkAsync(assetId, 60);
        var operation = (await _world.Operations.GetByEntityAsync(saved.Id)).Single();
        await _world.Operations.MarkSucceededAsync(operation.Id);

        Assert.True(await _frames.EnqueueAsync(saved.Id, assetId, 60, CancellationToken.None));
        Assert.Equal(MediaOperationStatus.Queued, (await _world.Operations.GetByIdAsync(operation.Id))!.Status);
    }

    [Fact]
    public async Task DeletingTheBookmarkBeforeItsFrameIsMade_LeavesNoPictureBehind()
    {
        var assetId = await _world.AddVideoAsync("film.mp4", seconds: 5, makeFile: false);
        var saved = await SaveBookmarkAsync(assetId, 3);
        await _bookmarks.DeleteAsync(Profile.SeedProfileId, saved.Id);

        await Worker().RunOnceAsync(CancellationToken.None);

        var operation = (await _world.Operations.GetByEntityAsync(saved.Id)).Single();
        Assert.Equal(MediaOperationStatus.NoResult, operation.Status);
        Assert.False(_cache.Exists(assetId, saved.Id));
    }

    [Fact]
    public async Task WithoutFfmpeg_NoFrameIsMade_TheJobWaits_AndOneLogLineIsWritten()
    {
        using var world = new ScrubPreviewWorld(new ScrubPreviewWorld.MissingFfmpeg());
        var bookmarks = new AudiobookBookmarkRepository(world.Database);
        var cache = new BookmarkFrameCache(world.AssetPaths);
        var frames = new BookmarkFrameService(
            world.Operations, world.Assets, new MediaTypeExtensionCatalog(), bookmarks, new VaultLibraryRepository(world.Database), cache, NullLogger<BookmarkFrameService>.Instance);
        var assetId = await world.AddVideoAsync("film.mp4", seconds: 12);
        var saved = new List<AudiobookBookmarkDto>();
        foreach (var seconds in new[] { 2.0, 9.0 })
        {
            var bookmark = await bookmarks.CreateAsync(
                Profile.SeedProfileId, WorkOf(world, assetId), new CreateAudiobookBookmarkRequestDto { AssetId = assetId, PositionSeconds = seconds });
            await frames.TryEnqueueAsync(bookmark, CancellationToken.None);
            saved.Add(bookmark);
        }

        var log = new CapturingLogger<BookmarkFrameOperationWorker>();
        var worker = Worker(world, log);

        var wait = await worker.RunOnceAsync(CancellationToken.None);
        await worker.RunOnceAsync(CancellationToken.None);

        Assert.True(wait >= TimeSpan.FromMinutes(1));
        foreach (var bookmark in saved)
        {
            var operation = (await world.Operations.GetByEntityAsync(bookmark.Id)).Single();
            Assert.Equal(MediaOperationStatus.RetryWaiting, operation.Status);
            Assert.Contains("FFmpeg is not available", operation.LastError);
            Assert.False(cache.Exists(assetId, bookmark.Id));
        }

        var line = Assert.Single(log.Messages, message => message.Contains("FFmpeg is not available", StringComparison.Ordinal));
        Assert.Contains("bookmark frames", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheOrphanSweep_RemovesFramesOfBookmarksThatAreGone_AndKeepsTheRest()
    {
        var assetId = await _world.AddVideoAsync("film.mp4", seconds: 5, makeFile: false);
        var kept = await SaveBookmarkAsync(assetId, 10);
        var removed = await SaveBookmarkAsync(assetId, 20);
        _cache.Write(assetId, kept.Id, [1, 2, 3]);
        _cache.Write(assetId, removed.Id, [1, 2, 3]);
        await _bookmarks.DeleteAsync(Profile.SeedProfileId, removed.Id);

        Assert.Equal(1, await _frames.SweepOrphansAsync(CancellationToken.None));

        Assert.True(_cache.Exists(assetId, kept.Id));
        Assert.False(_cache.Exists(assetId, removed.Id));
    }

    [Fact]
    public async Task TheGradualPass_QueuesVideoBookmarksWithoutAFrame_AFewAtATime_AndSkipsAudioAndFinishedOnes()
    {
        var video = await _world.AddVideoAsync("film.mkv", seconds: 5, makeFile: false);
        var audio = await _world.AddVideoAsync("book.m4b", seconds: 5, makeFile: false);
        // Saved before frames existed: nothing was queued for them.
        var old = new List<AudiobookBookmarkDto>();
        foreach (var (asset, seconds) in new[] { (video, 10.0), (video, 20.0), (video, 30.0), (audio, 40.0) })
        {
            old.Add(await _bookmarks.CreateAsync(
                Profile.SeedProfileId, WorkOf(_world, asset), new CreateAudiobookBookmarkRequestDto { AssetId = asset, PositionSeconds = seconds }));
        }

        _cache.Write(video, old[2].Id, [1, 2, 3]);

        var queued = await _frames.BackfillAsync(perPass: 10, CancellationToken.None);

        Assert.Equal(2, queued);
        Assert.Single(await _world.Operations.GetByEntityAsync(old[0].Id));
        Assert.Single(await _world.Operations.GetByEntityAsync(old[1].Id));
        Assert.Empty(await _world.Operations.GetByEntityAsync(old[2].Id));
        Assert.Empty(await _world.Operations.GetByEntityAsync(old[3].Id));
        // A second pass finds nothing new.
        Assert.Equal(0, await _frames.BackfillAsync(perPass: 10, CancellationToken.None));
    }

    [Fact]
    public void DeletingAnAssetsFrames_RemovesTheWholeFolder_AndLeavesOtherFilesAlone()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var bookmark = Guid.NewGuid();
        _cache.Write(first, bookmark, [1]);
        _cache.Write(second, bookmark, [1]);

        _cache.DeleteAsset(first);

        Assert.False(Directory.Exists(_cache.FolderFor(first)));
        Assert.True(_cache.Exists(second, bookmark));
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }
}
