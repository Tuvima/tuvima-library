using System.Reflection;
using MediaEngine.Api.Services.Plugins;
using MediaEngine.Domain;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;

namespace MediaEngine.Api.Tests;

/// <summary>
/// What a sharing plugin may learn about a finished play (Plugin architecture spec PA6): only music, only where the central gate allows
/// the plugin (never a Vault item), only a track with a title and an artist, the track's own facts before its album's, and a
/// MusicBrainz id only when the Library is sure of it.
/// </summary>
public sealed class PluginPlayDetailsTests
{
    private const string PluginId = "tuvima.listenbrainz";

    private readonly Guid _asset = Guid.NewGuid();
    private readonly Guid _work = Guid.NewGuid();
    private readonly Guid _album = Guid.NewGuid();
    private readonly List<CanonicalValue> _values = [];
    private readonly List<BridgeIdEntry> _bridges = [];
    private readonly GateSpy _gate = new();
    private MediaType _mediaType = MediaType.Music;
    private bool _assetExists = true;

    public PluginPlayDetailsTests()
    {
        _values.Add(Value(_work, "title", "Song"));
        _values.Add(Value(_work, "artist", "Artist"));
    }

    [Fact]
    public async Task ATrackWithATitleAndArtist_IsNamed()
    {
        _values.Add(Value(_work, "album", "Album"));
        _values.Add(Value(_work, "track_number", "4"));
        _values.Add(Value(_work, "duration", "215.5"));
        _bridges.Add(Bridge(_work, BridgeIdKeys.MusicBrainzRecordingId, "rec-1"));
        _bridges.Add(Bridge(_album, BridgeIdKeys.MusicBrainzArtistId, "art-1"));

        var track = await Service().GetTrackAsync(PluginId, _asset, CancellationToken.None);

        Assert.NotNull(track);
        Assert.Equal("Song", track.Title);
        Assert.Equal("Artist", track.Artist);
        Assert.Equal("Album", track.Album);
        Assert.Equal(4, track.TrackNumber);
        Assert.Equal(215.5, track.DurationSeconds);
        Assert.Equal("rec-1", track.RecordingMbid);
        Assert.Equal(["art-1"], track.ArtistMbids);
        Assert.Equal(new PluginAccessQuery(PluginId, null, "lib-1", _asset), _gate.Seen);
    }

    [Fact]
    public async Task AVaultOrLimitedItem_GetsNoAnswer()
    {
        _gate.Outcome = PluginAccessOutcome.VaultBlocked;

        Assert.Null(await Service().GetTrackAsync(PluginId, _asset, CancellationToken.None));
    }

    [Theory]
    [InlineData(MediaType.Movies)]
    [InlineData(MediaType.Books)]
    public async Task AnythingThatIsNotMusic_GetsNoAnswer(MediaType type)
    {
        _mediaType = type;

        Assert.Null(await Service().GetTrackAsync(PluginId, _asset, CancellationToken.None));
    }

    [Fact]
    public async Task AnUnknownAsset_GetsNoAnswer()
    {
        _assetExists = false;

        Assert.Null(await Service().GetTrackAsync(PluginId, _asset, CancellationToken.None));
        Assert.Null(await Service().GetTrackAsync(PluginId, Guid.Empty, CancellationToken.None));
    }

    [Fact]
    public async Task ATrackWithoutAnArtist_GetsNoAnswer()
    {
        _values.RemoveAll(value => value.Key == "artist");

        Assert.Null(await Service().GetTrackAsync(PluginId, _asset, CancellationToken.None));
    }

    [Fact]
    public async Task TheTracksOwnTitle_WinsOverTheAlbums()
    {
        _values.Add(Value(_album, "title", "The Album"));

        var track = await Service().GetTrackAsync(PluginId, _asset, CancellationToken.None);

        Assert.Equal("Song", track!.Title);
    }

    [Fact]
    public async Task TwoDifferentRecordingIds_MeansNeitherIsShared()
    {
        _bridges.Add(Bridge(_work, BridgeIdKeys.MusicBrainzRecordingId, "rec-1"));
        _bridges.Add(Bridge(_album, BridgeIdKeys.MusicBrainzRecordingId, "rec-2"));

        var track = await Service().GetTrackAsync(PluginId, _asset, CancellationToken.None);

        Assert.Null(track!.RecordingMbid);
    }

    private PluginPlayDetailsService Service() => new(
        Stub.Create<IMediaAssetRepository>(("FindByIdAsync", _ => Task.FromResult<MediaAsset?>(_assetExists ? new MediaAsset { Id = _asset, LibraryId = "lib-1" } : null))),
        Stub.Create<IWorkRepository>(("GetLineageByAssetAsync", _ => Task.FromResult<WorkLineage?>(
            new WorkLineage(_asset, Guid.NewGuid(), _work, _album, _album, WorkKind.Standalone, _mediaType)))),
        Stub.Create<ICanonicalValueRepository>(("GetByEntitiesAsync", _ => Task.FromResult<IReadOnlyDictionary<Guid, IReadOnlyList<CanonicalValue>>>(
            _values.GroupBy(value => value.EntityId).ToDictionary(group => group.Key, group => (IReadOnlyList<CanonicalValue>)group.ToList())))),
        Stub.Create<IBridgeIdRepository>(("GetByEntitiesAsync", _ => Task.FromResult<IReadOnlyDictionary<Guid, IReadOnlyList<BridgeIdEntry>>>(
            _bridges.GroupBy(entry => entry.EntityId).ToDictionary(group => group.Key, group => (IReadOnlyList<BridgeIdEntry>)group.ToList())))),
        _gate);

    private static CanonicalValue Value(Guid entity, string key, string value) => new() { EntityId = entity, Key = key, Value = value };

    private static BridgeIdEntry Bridge(Guid entity, string type, string value) => new() { EntityId = entity, IdType = type, IdValue = value };

    private sealed class GateSpy : IPluginAccessGate
    {
        public PluginAccessOutcome Outcome { get; set; } = PluginAccessOutcome.Allowed;

        public PluginAccessQuery? Seen { get; private set; }

        public Task<PluginAccessOutcome> CheckAsync(PluginAccessQuery query, CancellationToken ct = default)
        {
            Seen = query;
            return Task.FromResult(Outcome);
        }
    }

    /// <summary>Answers only the methods a test names; any other call fails loudly so a test never silently relies on an unplanned read.</summary>
    private class Stub : DispatchProxy
    {
        private Dictionary<string, Func<object?[], object?>> _answers = [];

        public static T Create<T>(params (string Method, Func<object?[], object?> Answer)[] answers)
            where T : class
        {
            var proxy = Create<T, Stub>();
            ((Stub)(object)proxy)._answers = answers.ToDictionary(entry => entry.Method, entry => entry.Answer);
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod is not null && _answers.TryGetValue(targetMethod.Name, out var answer)
                ? answer(args ?? [])
                : throw new NotSupportedException($"{targetMethod?.Name} was not expected in this test.");
    }
}
