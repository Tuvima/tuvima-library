using MediaEngine.Api.Services;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Services;
using MediaEngine.Providers.Providers;
using MediaEngine.Providers.Workers;
using MediaEngine.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Api.Tests;

/// <summary>
/// How a finished lyrics lookup is recorded on its durable request (spec music-player-and-listen-fixes D3, D4): a source that could not
/// be reached ends the request as a failure to retry later, which the panel words as "could not be looked up right now", while a source
/// that answered with nothing ends it as "no result". Real data store; the source is a hand-written spy.
/// </summary>
public sealed class TextTrackRefreshOperationWorkerTests : IDisposable
{
    private readonly LibraryMoveTestWorld _world = new(withFiles: true);
    private readonly MediaOperationRepository _operations;
    private readonly SpySource _source = new();
    private readonly TextTrackRefreshOperationWorker _worker;
    private readonly Guid _assetId;

    public TextTrackRefreshOperationWorkerTests()
    {
        var files = _world.Files!;
        _operations = new MediaOperationRepository(files.Database);
        var enrichment = new TextTrackEnrichmentWorker(
            new MediaAssetRepository(files.Database),
            new WorkRepository(files.Database),
            new CanonicalValueRepository(files.Database),
            new BridgeIdRepository(files.Database),
            new TextTrackRepository(files.Database),
            [_source],
            new AssetPathService(Path.Combine(files.Managed, ".library")),
            NullLogger<TextTrackEnrichmentWorker>.Instance);
        _worker = new TextTrackRefreshOperationWorker(_operations, enrichment, NullLogger<TextTrackRefreshOperationWorker>.Instance);
        var album = _world.AddParent("Music", "The Best Of");
        var path = MoveFileWorld.WriteFile(files.Managed, "inbox/001 Song.flac", "audio");
        _assetId = _world.AddChild(album, "Music", "Song", 1, filePath: path).Item3!.Value;
        _world.Put(_assetId, "media_type", "Music");
        _world.Put(_assetId, "title", "Song");
        _world.Put(_assetId, "artist", "Artist");
    }

    public void Dispose() => _world.Dispose();

    private Task<MediaOperation> QueueLyricsAsync() => _operations.EnsureAsync(new MediaOperation
    {
        OperationType = MediaOperationType.TextTrackLyrics,
        OperationKind = MediaOperationKind.TextTrack,
        EntityId = _assetId,
        EntityKind = EntityType.MediaAsset.ToString(),
        Status = MediaOperationStatus.Queued,
        Stage = MediaOperationStage.Queued,
        QueueName = "text_track",
        IdempotencyKey = $"test:{Guid.NewGuid():N}",
    });

    [Fact]
    public async Task ALyricsSourceThatCouldNotBeReached_EndsTheRequestAsAFailureToRetry_NotAsNoResult()
    {
        _source.Failure = new TextTrackLookupException("ProviderUnavailable", "lrclib could not be reached.");
        var queued = await QueueLyricsAsync();

        await _worker.ProcessAsync(queued, CancellationToken.None);

        // A retryable failure is stored as "waiting to retry": the Lyrics panel reads it as "could not be looked up right now".
        var after = await _operations.GetByIdAsync(queued.Id);
        Assert.Equal(MediaOperationStatus.RetryWaiting, after!.Status);
        Assert.NotNull(after.NextRetryAt);
        Assert.Equal("lrclib could not be reached.", after.LastError);
    }

    [Fact]
    public async Task ALyricsSourceThatCannotBeReached_IsTriedAgainAfterFifteenMinutes_ThenAfterTwoHours()
    {
        _source.Failure = new TextTrackLookupException("ProviderUnavailable", "lrclib could not be reached.");
        var queued = await QueueLyricsAsync();

        await _worker.ProcessAsync(queued, CancellationToken.None);
        var first = await _operations.GetByIdAsync(queued.Id);
        await _worker.ProcessAsync(first!, CancellationToken.None);
        var second = await _operations.GetByIdAsync(queued.Id);

        Assert.Equal(MediaOperationStatus.RetryWaiting, first!.Status);
        AssertWaitsAbout(first, TimeSpan.FromMinutes(15));
        Assert.Equal(MediaOperationStatus.RetryWaiting, second!.Status);
        AssertWaitsAbout(second, TimeSpan.FromHours(2));
    }

    [Fact]
    public async Task ALyricsSourceThatCannotBeReachedOnTheThirdAttempt_EndsTheRequestAsDeadLettered_AndNeverRetriesAgain()
    {
        _source.Failure = new TextTrackLookupException("ProviderUnavailable", "lrclib could not be reached.");
        var operation = await QueueLyricsAsync();

        for (var attempt = 1; attempt <= TextTrackRefreshOperationWorker.MaxUnreachableAttempts; attempt++)
        {
            await _worker.ProcessAsync(operation, CancellationToken.None);
            operation = (await _operations.GetByIdAsync(operation.Id))!;
        }

        Assert.Equal(MediaOperationStatus.DeadLettered, operation.Status);
        Assert.NotNull(operation.CompletedAt);
        Assert.Equal("lrclib could not be reached.", operation.LastError);
        var leased = await _operations.LeaseNextAsync("test", [MediaOperationType.TextTrackLyrics], 4, TimeSpan.FromMinutes(1));
        Assert.Empty(leased);
        Assert.Null(await _operations.GetNewestUnfinishedByEntityAsync(_assetId, MediaOperationType.TextTrackLyrics));
    }

    [Fact]
    public async Task ALyricsLookupThatGaveUp_LetsALaterRequestQueueAFreshLookup()
    {
        _source.Failure = new TextTrackLookupException("ProviderUnavailable", "lrclib could not be reached.");
        var operation = await QueueLyricsAsync();
        for (var attempt = 1; attempt <= TextTrackRefreshOperationWorker.MaxUnreachableAttempts; attempt++)
        {
            await _worker.ProcessAsync(operation, CancellationToken.None);
            operation = (await _operations.GetByIdAsync(operation.Id))!;
        }

        var fresh = await QueueLyricsAsync();
        _source.Failure = null;
        await _worker.ProcessAsync(fresh, CancellationToken.None);

        Assert.Equal(MediaOperationStatus.DeadLettered, operation.Status);
        Assert.Equal(MediaOperationStatus.NoResult, (await _operations.GetByIdAsync(fresh.Id))!.Status);
    }

    private static void AssertWaitsAbout(MediaOperation operation, TimeSpan expected)
    {
        var wait = operation.NextRetryAt!.Value - DateTimeOffset.UtcNow;
        Assert.InRange(wait, expected - TimeSpan.FromMinutes(1), expected + TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task ALyricsSourceThatAnsweredWithNothing_EndsTheRequestAsNoResult()
    {
        var queued = await QueueLyricsAsync();

        await _worker.ProcessAsync(queued, CancellationToken.None);

        var after = await _operations.GetByIdAsync(queued.Id);
        Assert.Equal(MediaOperationStatus.NoResult, after!.Status);
    }

    private sealed class SpySource : ITextTrackProvider
    {
        public string Name => "spy";

        public TextTrackKind Kind => TextTrackKind.Lyrics;

        public bool IsEnabled => true;

        public TextTrackLookupException? Failure { get; set; }

        public bool CanHandle(MediaType mediaType) => mediaType == MediaType.Music;

        public Task<IReadOnlyList<TextTrackCandidate>> SearchAsync(TextTrackLookup lookup, CancellationToken ct = default) =>
            Failure is { } failure ? throw failure : Task.FromResult<IReadOnlyList<TextTrackCandidate>>([]);

        public Task<TextTrackDownload?> DownloadAsync(TextTrackCandidate candidate, CancellationToken ct = default) =>
            Task.FromResult<TextTrackDownload?>(null);
    }
}
