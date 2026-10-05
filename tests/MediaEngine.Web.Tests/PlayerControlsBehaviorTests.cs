using MediaEngine.Contracts.Playback;
using MediaEngine.Web.Services.Playback;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Tests.Support;

namespace MediaEngine.Web.Tests;

public sealed class PlayerControlsBehaviorTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SavedDuplicateMoveUsesServerOccurrencesAndReadsLostReplyOnce(bool loseReply)
    {
        var items = SavedDuplicates();
        var state = SavedState(items);
        var reads = 0; var mutations = 0;
        var api = EngineApiClientStub.Create(stub => {
            stub.SetHandler(nameof(IEngineApiClient.GetPlayerStateAsync), _ => { reads++; return Task.FromResult<PlayerStateDto?>(state); });
            stub.SetHandler(nameof(IEngineApiClient.ReorderPlayerQueueAsync), args => {
                var request = (PlayerQueueMutationDto)args![0]!;
                mutations++;
                Assert.False(request.Force);
                Assert.Equal(state.StateVersion, request.ExpectedStateVersion);
                state = state with { Queue = request.QueueItemIds.Select(id => state.Queue.Single(row => row.QueueItemId == id)).ToArray() };
                return Task.FromResult<PlayerStateDto?>(loseReply ? null : state);
            });
        });
        var player = SavedPlayer(api, items);
        await player.MoveUpcomingAsync(items[2].QueueEntryId, 0, player.QueueRevision);
        Assert.Equal(new[] { items[0].PersistedQueueItemId, items[2].PersistedQueueItemId, items[1].PersistedQueueItemId }, player.CreateSnapshot().Queue.Select(row => row.PersistedQueueItemId));
        Assert.Equal(items[0].QueueEntryId, player.CurrentItem!.QueueEntryId);
        Assert.Equal(1, mutations);
        Assert.Equal(loseReply ? 2 : 1, reads);
    }

    [Fact]
    public async Task ChangedSavedOrderRejectsMoveWithoutOverwritingOtherClient()
    {
        var items = SavedDuplicates();
        var state = SavedState([items[0], items[2], items[1]]);
        var mutations = 0;
        var api = EngineApiClientStub.Create(stub => {
            stub.SetHandler(nameof(IEngineApiClient.GetPlayerStateAsync), _ => Task.FromResult<PlayerStateDto?>(state));
            stub.SetHandler(nameof(IEngineApiClient.ReorderPlayerQueueAsync), _ => { mutations++; return Task.FromResult<PlayerStateDto?>(state); });
        });
        var player = SavedPlayer(api, items);
        await Assert.ThrowsAsync<InvalidOperationException>(() => player.MoveUpcomingAsync(items[2].QueueEntryId, 0, player.QueueRevision));
        Assert.Equal(items.Select(row => row.QueueEntryId), player.Queue.Select(row => row.QueueEntryId));
        Assert.Equal(0, mutations);
    }

    [Fact]
    public async Task LateSavedMoveReplyCannotCommitToNewPlaybackRequest()
    {
        var items = SavedDuplicates(); var state = SavedState(items);
        PlaybackSessionController? player = null;
        var api = EngineApiClientStub.Create(stub => {
            stub.SetHandler(nameof(IEngineApiClient.GetPlayerStateAsync), _ => Task.FromResult<PlayerStateDto?>(state));
            stub.SetHandler(nameof(IEngineApiClient.ReorderPlayerQueueAsync), args => {
                var request = (PlayerQueueMutationDto)args![0]!;
                player!.ReservePlaybackRequest();
                return Task.FromResult<PlayerStateDto?>(state with { Queue = request.QueueItemIds.Select(id => state.Queue.Single(row => row.QueueItemId == id)).ToArray() });
            });
        });
        player = SavedPlayer(api, items);
        await Assert.ThrowsAsync<InvalidOperationException>(() => player.MoveUpcomingAsync(items[2].QueueEntryId, 0, player.QueueRevision));
        Assert.Equal(items.Select(row => row.QueueEntryId), player.Queue.Select(row => row.QueueEntryId));
    }

    [Fact]
    public async Task UnconfirmedSavedRemovalKeepsLocalOccurrence()
    {
        var items = SavedDuplicates(); var state = SavedState(items);
        var api = EngineApiClientStub.Create(stub => {
            stub.SetHandler(nameof(IEngineApiClient.GetPlayerStateAsync), _ => Task.FromResult<PlayerStateDto?>(state));
            stub.SetHandler(nameof(IEngineApiClient.RemovePlayerQueueItemAsync), _ => Task.FromResult(false));
        });
        var player = SavedPlayer(api, items);
        await Assert.ThrowsAsync<InvalidOperationException>(() => player.DispatchAsync(new(PlaybackCommandKind.RemoveUpcoming, Index: 1)));
        Assert.Equal(items.Select(row => row.QueueEntryId), player.Queue.Select(row => row.QueueEntryId));
    }

    private static ListenQueueItem[] SavedDuplicates()
    {
        var row = new ListenQueueItem { WorkId = Guid.NewGuid(), AssetId = Guid.NewGuid(), MediaType = "Music", Title = "Repeated", StreamUrl = "stream://fixture" };
        return Enumerable.Range(0, 3).Select(_ => row with { QueueEntryId = Guid.NewGuid(), PersistedQueueItemId = Guid.NewGuid() }).ToArray();
    }
    private static PlayerStateDto SavedState(ListenQueueItem[] items) => new() {
        StateVersion = 17, CurrentQueueItemId = items[0].PersistedQueueItemId,
        Queue = items.Select(row => new PlayerQueueItemDto { QueueItemId = row.PersistedQueueItemId!.Value, WorkId = row.WorkId, AssetId = row.AssetId }).ToArray()
    };
    private static PlaybackSessionController SavedPlayer(IEngineApiClient api, ListenQueueItem[] items)
    {
        var player = new PlaybackSessionController(null!, api);
        player.RestoreState(new() { CurrentIndex = 0, Queue = items.ToList(), Experience = PlayerExperienceModes.Music });
        return player;
    }
    [Fact]
    public async Task UpcomingMovePreservesCurrentOccurrenceAndRejectsStaleAndCurrentMoves()
    {
        var repeated = new ListenQueueItem { WorkId = Guid.NewGuid(), AssetId = Guid.NewGuid(), MediaType = "Music", Title = "Repeated", StreamUrl = "stream://fixture" };
        var current = repeated with { QueueEntryId = Guid.NewGuid() };
        var first = repeated with { QueueEntryId = Guid.NewGuid() };
        var second = repeated with { QueueEntryId = Guid.NewGuid() };
        var player = new PlaybackSessionController(null!, null!);
        player.RestoreState(new() { CurrentIndex = 0, Queue = [current, first, second], ShuffleEnabled = true, Experience = PlayerExperienceModes.Music });
        var before = player.CreateSnapshot();
        await player.MoveUpcomingAsync(second.QueueEntryId, 0, before.QueueRevision);
        Assert.Equal(new[] { current.QueueEntryId, second.QueueEntryId, first.QueueEntryId }, player.Queue.Select(item => item.QueueEntryId));
        Assert.Equal(current.QueueEntryId, player.CurrentItem!.QueueEntryId);
        Assert.Equal(before.PlaybackRequestVersion, player.PlaybackRequestVersion);
        await Assert.ThrowsAsync<InvalidOperationException>(() => player.MoveUpcomingAsync(first.QueueEntryId, 0, before.QueueRevision));
        await Assert.ThrowsAsync<InvalidOperationException>(() => player.MoveUpcomingAsync(current.QueueEntryId, 0, player.QueueRevision));
        var restored = new PlaybackSessionController(null!, null!);
        var serialized = System.Text.Json.JsonSerializer.Serialize(player.CreateSnapshot());
        restored.RestoreState(System.Text.Json.JsonSerializer.Deserialize<ListenPlaybackSnapshot>(serialized)!);
        await restored.SkipNextAsync();
        Assert.Equal(second.QueueEntryId, restored.CurrentItem!.QueueEntryId);
    }

    [Theory]
    [InlineData("flac", 44100, 16, "Lossless")]
    [InlineData("ALAC", 48000, 16, "Lossless")]
    [InlineData("pcm_s24le", 44100, 24, "Hi-Res Lossless")]
    [InlineData("flac", 48000, 20, "Hi-Res Lossless")]
    [InlineData("flac", 96000, 16, "Hi-Res Lossless")]
    [InlineData("flac", 192000, 32, "Hi-Res Lossless")]
    [InlineData("aac", 96000, 24, null)]
    [InlineData("flac", 0, 24, null)]
    [InlineData("flac", 44100, 0, null)]
    public void QualityRequiresLosslessCodecAndKnownResolution(string codec, int rate, int bits, string? expected)
    {
        var id = Guid.NewGuid();
        var manifest = Manifest(id) with { Technical = new() { AudioCodec = codec, SampleRateHz = rate, BitDepth = bits } };
        Assert.Equal(expected, PlaybackAudioQuality.Classify(manifest, $"/engine-stream/{id:D}"));
        Assert.Null(PlaybackAudioQuality.Classify(manifest, $"/engine-stream/{Guid.NewGuid():D}"));
        Assert.Null(PlaybackAudioQuality.Classify(manifest with { ConversionReason = "converted" }, $"/engine-stream/{id:D}"));
        Assert.Null(PlaybackAudioQuality.Classify(manifest with { RecommendedDelivery = PlaybackDeliveryModes.Hls }, $"/engine-hls/{id:D}/index.m3u8"));
    }

    [Fact]
    public void MusicHistoryClearKeepsQueueAndRejectsAudiobookHistory()
    {
        var music = new ListenQueueItem { WorkId = Guid.NewGuid(), MediaType = "Music", Title = "Music" };
        var book = music with { MediaType = "Audiobooks", Title = "Book" };
        var player = new PlaybackSessionController(null!, null!);
        player.RestoreState(new() { Queue = [music], CurrentIndex = 0, History = [music, book], Experience = PlayerExperienceModes.Music });
        player.ClearMusicHistory();
        Assert.Equal(book.WorkId, Assert.Single(player.History).WorkId);
        Assert.Single(player.Queue);
        player.RestoreState(new() { Queue = [book], CurrentIndex = 0, Experience = PlayerExperienceModes.Audiobook });
        Assert.Throws<InvalidOperationException>(player.ClearMusicHistory);
    }

    private static PlaybackManifestDto Manifest(Guid id) => new() { AssetId = id, DirectPlaySupported = true, DirectStreamUrl = $"/media/assets/{id:D}/stream" };
}

