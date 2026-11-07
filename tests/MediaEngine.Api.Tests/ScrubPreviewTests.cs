using System.Text.Json;
using Dapper;
using MediaEngine.Api.Services.Playback;
using MediaEngine.Contracts.Playback;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Storage;
using MediaEngine.Storage.Services;
using MediaEngine.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;

namespace MediaEngine.Api.Tests;

/// <summary>Scrub previews made ahead of time (Unified player v2 D9a): the cache, the generator, the switch, the queue, the worker and the back-fill.</summary>
public sealed class ScrubPreviewTests : IDisposable
{
    private readonly ScrubPreviewWorld _world = new();

    public void Dispose() => _world.Dispose();

    private static ScrubPreviewIndexDto Index(params string[] sheets) => new()
    {
        IntervalSeconds = 2,
        TileWidth = 80,
        TileHeight = 46,
        Columns = 3,
        Rows = 2,
        FrameCount = 6 * sheets.Length,
        DurationSeconds = 12 * sheets.Length,
        Sheets = sheets,
    };

    private void WriteSet(Guid assetId, params string[] sheets)
    {
        var staging = _world.Store.NewStagingFolder(assetId);
        Directory.CreateDirectory(staging);
        foreach (var sheet in sheets)
        {
            File.WriteAllBytes(Path.Combine(staging, sheet), new byte[1000]);
        }

        _world.Store.Publish(assetId, staging, Index(sheets));
    }

    // ----- The cache -----

    [Fact]
    public void ASetWithoutItsIndex_OrWithASheetMissing_IsNeverOffered()
    {
        var assetId = Guid.NewGuid();
        var folder = _world.Store.FolderFor(assetId);
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, "sheet-000.jpg"), new byte[10]);
        Assert.Null(_world.Store.TryReadComplete(assetId));

        File.WriteAllText(Path.Combine(folder, ScrubPreviewCache.IndexFileName), JsonSerializer.Serialize(Index("sheet-000.jpg", "sheet-001.jpg"), new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        Assert.Null(_world.Store.TryReadComplete(assetId));

        File.WriteAllBytes(Path.Combine(folder, "sheet-001.jpg"), new byte[10]);
        Assert.NotNull(_world.Store.TryReadComplete(assetId));
    }

    [Theory]
    [InlineData("../index.json")]
    [InlineData("..%2Findex.json")]
    [InlineData("sheet-009.jpg")]
    [InlineData("sheet-000.jpg.txt")]
    [InlineData("index.json")]
    public void OnlyTheSheetsTheIndexNamesCanBeReached(string name)
    {
        var assetId = Guid.NewGuid();
        WriteSet(assetId, "sheet-000.jpg");

        Assert.Null(_world.Store.ResolveSheetFile(assetId, name));
        Assert.NotNull(_world.Store.ResolveSheetFile(assetId, "sheet-000.jpg"));
    }

    [Fact]
    public void TheCacheReportsItsSpace_AndRemovingOneAssetLeavesTheOthers()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        WriteSet(first, "sheet-000.jpg");
        WriteSet(second, "sheet-000.jpg", "sheet-001.jpg");
        Assert.True(_world.Store.UsedBytes() > 3000);

        _world.Store.Delete(first);

        Assert.Null(_world.Store.TryReadComplete(first));
        Assert.NotNull(_world.Store.TryReadComplete(second));
    }

    // ----- Making them -----

    [RealFfmpegFact]
    public async Task ASampleVideo_GetsAnIndexAndSpriteSheets()
    {
        var assetId = await _world.AddVideoAsync("film.mp4", seconds: 25);

        var result = await _world.Generator.GenerateAsync(assetId, CancellationToken.None);

        Assert.Equal(ScrubPreviewOutcome.Generated, result.Outcome);
        var index = _world.Store.TryReadComplete(assetId);
        Assert.NotNull(index);
        Assert.Equal(2, index!.IntervalSeconds);
        Assert.Equal(80, index.TileWidth);
        Assert.Equal(3, index.Columns);
        Assert.Equal(2, index.Rows);
        Assert.Equal(12, index.FrameCount);
        Assert.Equal(2, index.Sheets.Count);
        foreach (var sheet in index.Sheets)
        {
            using var codec = SKCodec.Create(_world.Store.ResolveSheetFile(assetId, sheet));
            Assert.Equal(index.Columns * index.TileWidth, codec.Info.Width);
            Assert.Equal(index.Rows * index.TileHeight, codec.Info.Height);
        }

        // Nothing half-made is left beside the finished set.
        Assert.Single(Directory.EnumerateDirectories(_world.Store.Root));
    }

    [RealFfmpegFact]
    public async Task AFileThatAlreadyHasAnIndex_IsNotRegenerated()
    {
        var assetId = await _world.AddVideoAsync("film.mp4", seconds: 12);
        await _world.Generator.GenerateAsync(assetId, CancellationToken.None);
        var sheet = _world.Store.ResolveSheetFile(assetId, "sheet-000.jpg")!;
        var before = File.GetLastWriteTimeUtc(sheet);
        await Task.Delay(50);

        var again = await _world.Generator.GenerateAsync(assetId, CancellationToken.None);

        Assert.Equal(ScrubPreviewOutcome.AlreadyComplete, again.Outcome);
        Assert.Equal(before, File.GetLastWriteTimeUtc(sheet));
    }

    [Fact]
    public async Task WithTheSwitchOff_NothingIsMade()
    {
        var assetId = await _world.AddVideoAsync("film.mp4", seconds: 12);
        _world.Save(settings => settings.ScrubPreviews.Enabled = false);

        var result = await _world.Generator.GenerateAsync(assetId, CancellationToken.None);

        Assert.Equal(ScrubPreviewOutcome.Disabled, result.Outcome);
        Assert.False(Directory.Exists(_world.Store.Root));
    }

    [Fact]
    public async Task WithoutFfmpeg_TheJobSaysSoAndMakesNothing()
    {
        using var world = new ScrubPreviewWorld(new ScrubPreviewWorld.MissingFfmpeg());
        var assetId = await world.AddVideoAsync("film.mp4", seconds: 12);

        var result = await world.Generator.GenerateAsync(assetId, CancellationToken.None);

        Assert.Equal(ScrubPreviewOutcome.FfmpegUnavailable, result.Outcome);
        Assert.Null(world.Store.TryReadComplete(assetId));
    }

    [RealFfmpegFact]
    public async Task AnAudioFile_GetsNoPreviews()
    {
        var path = Path.Combine(_world.Root, "media", "book.m4a");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var (exit, _, error) = await ((RealFfmpeg)_world.Ffmpeg).RunAsync($"-v error -y -f lavfi -i sine=d=6 -c:a aac \"{path}\"");
        Assert.True(exit == 0, error);
        var assetId = _world.AddAssetRow(path);

        var result = await _world.Generator.GenerateAsync(assetId, CancellationToken.None);

        Assert.Equal(ScrubPreviewOutcome.NotVideo, result.Outcome);
        Assert.Null(_world.Store.TryReadComplete(assetId));
    }

    [Fact]
    public async Task AMissingFile_IsReportedNotThrown()
    {
        var assetId = await _world.AddVideoAsync("gone.mp4", seconds: 5, makeFile: false);

        Assert.Equal(ScrubPreviewOutcome.SourceMissing, (await _world.Generator.GenerateAsync(assetId, CancellationToken.None)).Outcome);
    }

    // ----- The switch -----

    [Fact]
    public void TurningTheSwitchOff_RemovesEveryPreview_AndOnKeepsTheSettingSaved()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        WriteSet(first, "sheet-000.jpg");
        WriteSet(second, "sheet-000.jpg");
        var service = new ScrubPreviewSettingsService(_world.Configuration, _world.Store, _world.Ffmpeg, NullLogger<ScrubPreviewSettingsService>.Instance);
        Assert.True(service.GetStatus().Enabled);
        Assert.True(service.GetStatus().BytesUsed > 0);

        var off = service.SetEnabled(false);

        Assert.False(off.Enabled);
        Assert.Equal(0, off.BytesUsed);
        Assert.Null(_world.Store.TryReadComplete(first));
        Assert.Null(_world.Store.TryReadComplete(second));
        Assert.False(_world.Configuration.LoadTranscoding().ScrubPreviews.Enabled);
        Assert.True(service.SetEnabled(true).Enabled);
        Assert.True(_world.Configuration.LoadTranscoding().ScrubPreviews.Enabled);
    }

    // ----- The queue and the worker -----

    [Fact]
    public async Task AFileIsQueuedOnce_AtTheLowestPriority()
    {
        var assetId = await _world.AddVideoAsync("film.mp4", seconds: 5);
        var path = Path.Combine(_world.Root, "media", "film.mp4");

        Assert.True(await _world.Queue.EnqueueAsync(assetId, path, "hash-1", CancellationToken.None));
        Assert.False(await _world.Queue.EnqueueAsync(assetId, path, "hash-1", CancellationToken.None));

        var operation = Assert.Single(await _world.Operations.GetByEntityAsync(assetId));
        Assert.Equal(MediaOperationType.PlaybackScrubPreviews, operation.OperationType);
        Assert.Equal(ScrubPreviewQueue.LowestPriority, operation.Priority);
        Assert.Equal(MediaOperationQueue.ScrubPreviews, operation.QueueName);
        // A changed file (new content hash) is a new job.
        Assert.True(await _world.Queue.EnqueueAsync(assetId, path, "hash-2", CancellationToken.None));
    }

    [Fact]
    public async Task AFinishedJobWhosePreviewsWereRemoved_GoesBackInTheQueue()
    {
        var assetId = await _world.AddVideoAsync("film.mp4", seconds: 5);
        var path = Path.Combine(_world.Root, "media", "film.mp4");
        await _world.Queue.EnqueueAsync(assetId, path, "h", CancellationToken.None);
        var operation = (await _world.Operations.GetByEntityAsync(assetId)).Single();
        await _world.Operations.MarkSucceededAsync(operation.Id);

        Assert.True(await _world.Queue.EnqueueAsync(assetId, path, "h", CancellationToken.None));
        Assert.Equal(MediaOperationStatus.Queued, (await _world.Operations.GetByIdAsync(operation.Id))!.Status);

        // With the previews there again, nothing is queued a second time.
        await _world.Operations.MarkSucceededAsync(operation.Id);
        WriteSet(assetId, "sheet-000.jpg");
        Assert.False(await _world.Queue.EnqueueAsync(assetId, path, "h", CancellationToken.None));
    }

    private ScrubPreviewOperationWorker Worker(ScrubPreviewWorld world, bool transcoding) => new(
        world.Operations, world.Generator, new Transcodes { IsBusy = transcoding }, world.Configuration,
        NullLogger<ScrubPreviewOperationWorker>.Instance);

    [RealFfmpegFact]
    public async Task TheWorkerMakesOneFileAtATime_AndRecordsTheResult()
    {
        var first = await _world.AddVideoAsync("one.mp4", seconds: 12);
        var second = await _world.AddVideoAsync("two.mp4", seconds: 12);
        await _world.Queue.EnqueueAsync(first, "one", "h1", CancellationToken.None);
        await _world.Queue.EnqueueAsync(second, "two", "h2", CancellationToken.None);
        var worker = Worker(_world, transcoding: false);

        await worker.RunOnceAsync(CancellationToken.None);

        var done = new[] { first, second }.Count(id => _world.Store.TryReadComplete(id) is not null);
        Assert.Equal(1, done);
        await worker.RunOnceAsync(CancellationToken.None);
        Assert.NotNull(_world.Store.TryReadComplete(first));
        Assert.NotNull(_world.Store.TryReadComplete(second));
        Assert.All(new[] { first, second }, id => Assert.Equal(MediaOperationStatus.Succeeded, _world.Operations.GetByEntityAsync(id).Result.Single().Status));
    }

    [Fact]
    public async Task WhileATranscodeIsServed_TheWorkerLeavesTheQueueAlone()
    {
        var assetId = await _world.AddVideoAsync("film.mp4", seconds: 12);
        await _world.Queue.EnqueueAsync(assetId, "film", "h", CancellationToken.None);

        var wait = await Worker(_world, transcoding: true).RunOnceAsync(CancellationToken.None);

        Assert.True(wait > TimeSpan.Zero);
        Assert.Equal(MediaOperationStatus.Queued, (await _world.Operations.GetByEntityAsync(assetId)).Single().Status);
        Assert.Null(_world.Store.TryReadComplete(assetId));
    }

    [Fact]
    public async Task WithTheSwitchOff_TheWorkerLeavesTheQueueAlone()
    {
        var assetId = await _world.AddVideoAsync("film.mp4", seconds: 12);
        await _world.Queue.EnqueueAsync(assetId, "film", "h", CancellationToken.None);
        _world.Save(settings => settings.ScrubPreviews.Enabled = false);

        await Worker(_world, transcoding: false).RunOnceAsync(CancellationToken.None);

        Assert.Equal(MediaOperationStatus.Queued, (await _world.Operations.GetByEntityAsync(assetId)).Single().Status);
    }

    [Fact]
    public async Task WithoutFfmpeg_TheJobRecordsNotAvailableAndTheWorkerBacksOffQuietly()
    {
        using var world = new ScrubPreviewWorld(new ScrubPreviewWorld.MissingFfmpeg());
        var assetId = await world.AddVideoAsync("film.mp4", seconds: 12);
        await world.Queue.EnqueueAsync(assetId, "film", "h", CancellationToken.None);

        var wait = await Worker(world, transcoding: false).RunOnceAsync(CancellationToken.None);

        var operation = (await world.Operations.GetByEntityAsync(assetId)).Single();
        Assert.Equal(MediaOperationStatus.RetryWaiting, operation.Status);
        Assert.Contains("FFmpeg is not available", operation.LastError);
        Assert.True(wait >= TimeSpan.FromMinutes(1));
        Assert.False(Directory.Exists(world.Store.Root));
    }

    private sealed class Transcodes : ITranscodeActivitySignal
    {
        public bool IsBusy { get; init; }
    }

    // ----- The gradual pass -----

    private ScrubPreviewBackfillService Backfill(ScrubPreviewWorld world) => new(
        new ScrubPreviewCandidateRepository(world.Database), world.Queue, world.Store, new MediaTypeExtensionCatalog(),
        world.Operations, world.Assets, world.Configuration, NullLogger<ScrubPreviewBackfillService>.Instance);

    private async Task<List<Guid>> QueuedAssetsAsync(ScrubPreviewWorld world) =>
        (await world.Operations.GetQueueAsync(MediaOperationQueue.ScrubPreviews, 200))
            .Where(operation => operation.EntityId is not null).Select(operation => operation.EntityId!.Value).ToList();

    [Fact]
    public async Task ThePassQueuesTheNewestVideosFirst_AFewAtATime_AndSkipsFilesThatAlreadyHavePreviews()
    {
        var now = DateTimeOffset.UtcNow;
        var oldest = await _world.AddVideoAsync("a.mkv", 5, now.AddDays(-3), makeFile: false);
        var middle = await _world.AddVideoAsync("b.mkv", 5, now.AddDays(-2), makeFile: false);
        var newest = await _world.AddVideoAsync("c.mp4", 5, now.AddDays(-1), makeFile: false);
        var done = await _world.AddVideoAsync("d.mp4", 5, now, makeFile: false);
        WriteSet(done, "sheet-000.jpg");
        var notVideo = _world.AddAssetRow(Path.Combine(_world.Root, "media", "song.mp3"), now);
        var notShown = _world.AddAssetRow(Path.Combine(_world.Root, "media", "hidden.mkv"));
        using (var connection = _world.Database.CreateConnection())
        {
            connection.Execute("UPDATE media_assets SET presented_at = NULL WHERE id = @notShown;", new { notShown });
        }

        var service = Backfill(_world);
        var queued = await service.RunPassAsync(perPass: 2, CancellationToken.None);

        Assert.Equal(2, queued);
        var first = await QueuedAssetsAsync(_world);
        Assert.Equal([newest, middle], first.OrderByDescending(id => id == newest).ToList());
        Assert.DoesNotContain(done, first);
        Assert.DoesNotContain(notVideo, first);
        Assert.DoesNotContain(notShown, first);

        // The next pass reaches the one that is left, and nothing is queued twice.
        await service.RunPassAsync(perPass: 2, CancellationToken.None);
        var all = await QueuedAssetsAsync(_world);
        Assert.Equal(3, all.Count);
        Assert.Contains(oldest, all);
    }

    [Fact]
    public async Task WithTheSwitchOffAndBackOn_PreviewsThatWereRemovedAreQueuedAgain()
    {
        var assetId = await _world.AddVideoAsync("film.mkv", 5, makeFile: false);
        var service = Backfill(_world);
        await service.RunPassAsync(perPass: 5, CancellationToken.None);
        var operation = (await _world.Operations.GetByEntityAsync(assetId)).Single();
        await _world.Operations.MarkSucceededAsync(operation.Id); // made, then removed by the switch

        var queued = await service.RunPassAsync(perPass: 5, CancellationToken.None);

        Assert.Equal(1, queued);
        Assert.Equal(MediaOperationStatus.Queued, (await _world.Operations.GetByIdAsync(operation.Id))!.Status);
    }

    [Fact]
    public async Task PreviewsOfFilesThatAreGone_AreSweptAway_AndTheRestKept()
    {
        var kept = await _world.AddVideoAsync("kept.mkv", 5, makeFile: false);
        var gone = Guid.NewGuid();
        WriteSet(kept, "sheet-000.jpg");
        WriteSet(gone, "sheet-000.jpg");
        var stale = Path.Combine(_world.Store.Root, $"{gone:D}.tmp-old");
        Directory.CreateDirectory(stale);
        Directory.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddDays(-3));

        var removed = await Backfill(_world).PruneOrphansAsync(CancellationToken.None);

        Assert.Equal(2, removed);
        Assert.NotNull(_world.Store.TryReadComplete(kept));
        Assert.Null(_world.Store.TryReadComplete(gone));
        Assert.False(Directory.Exists(stale));
    }
}
