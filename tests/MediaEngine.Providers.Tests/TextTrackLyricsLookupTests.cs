using System.Reflection;
using MediaEngine.Domain;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Services;
using MediaEngine.Providers.Workers;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Providers.Tests;

/// <summary>
/// A lyrics lookup names the song by its own title and artist, the album by the album's name, and measures the song by the
/// length of the file first (Music player and Listen fixes spec D1).
/// </summary>
public sealed class TextTrackLyricsLookupTests
{
    private static readonly Guid LowId = Guid.Parse("00000000-0000-4000-8000-000000000001");
    private static readonly Guid HighId = Guid.Parse("ffffffff-0000-4000-8000-000000000002");

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ASongsTitleAndAlbum_AreNotSwapped_WhateverOrderTheStoreReturnsThem(bool songSortsFirst)
    {
        var songId = songSortsFirst ? LowId : HighId;
        var albumId = songSortsFirst ? HighId : LowId;

        var lookup = await Capture(songId, albumId,
        [
            Value(songId, "title", "Song A"),
            Value(songId, "artist", "Artist C"),
            Value(albumId, "title", "Album B"),
            Value(albumId, "artist", "Album Artist D"),
        ]);

        Assert.Equal("Song A", lookup.Title);
        Assert.Equal("Album B", lookup.Album);
        Assert.Equal("Artist C", lookup.Artist);
    }

    [Fact]
    public async Task ASongsOwnAlbumValue_IsUsedBeforeTheParentsTitle()
    {
        var lookup = await Capture(LowId, HighId,
        [
            Value(LowId, "title", "Song A"),
            Value(LowId, "album", "Album From Tags"),
            Value(HighId, "title", "Album B"),
        ]);

        Assert.Equal("Song A", lookup.Title);
        Assert.Equal("Album From Tags", lookup.Album);
    }

    [Fact]
    public async Task TheFileMeasuredLength_IsPreferredOverTheProvidersDuration()
    {
        var lookup = await Capture(LowId, HighId,
        [
            Value(LowId, "title", "Song A"),
            Value(LowId, "duration", "3.5"),
            Value(LowId, "duration_sec", "243.120"),
        ]);

        Assert.NotNull(lookup.DurationSeconds);
        Assert.Equal(243.12, lookup.DurationSeconds.Value, 2);
    }

    [Fact]
    public async Task ALengthWrittenAsMinutesAndSeconds_IsReadAsMinutesAndSeconds()
    {
        var lookup = await Capture(LowId, HighId,
        [
            Value(LowId, "title", "Song A"),
            Value(LowId, "duration", "4:03"),
        ]);

        Assert.Equal(243d, lookup.DurationSeconds);
    }

    [Fact]
    public async Task ALengthWrittenAsHoursMinutesAndSeconds_StillReadsHours()
    {
        var lookup = await Capture(LowId, HighId,
        [
            Value(LowId, "title", "Song A"),
            Value(LowId, "duration", "1:02:03"),
        ]);

        Assert.Equal(3723d, lookup.DurationSeconds);
    }

    private static CanonicalValue Value(Guid entityId, string key, string value) =>
        new() { EntityId = entityId, Key = key, Value = value, LastScoredAt = DateTimeOffset.UtcNow };

    private static async Task<TextTrackLookup> Capture(Guid songId, Guid albumId, IReadOnlyList<CanonicalValue> values)
    {
        var assetId = Guid.NewGuid();
        var editionId = Guid.NewGuid();
        var asset = new MediaAsset
        {
            Id = assetId,
            EditionId = editionId,
            LibraryId = "aaaaaaaa-0000-4000-8000-0000000000a1",
            FilePathRoot = Path.Combine(Path.GetTempPath(), $"tuvima-absent-{Guid.NewGuid():N}", "Song.flac"),
        };
        // The store hands the groups back in entity-id order, as the real repository does.
        var groups = values
            .GroupBy(value => value.EntityId)
            .OrderBy(group => group.Key.ToByteArray(bigEndian: true), Comparer<byte[]>.Create(CompareBytes))
            .ToDictionary(group => group.Key, group => (IReadOnlyList<CanonicalValue>)group.ToList());

        var assetRepo = Stub<IMediaAssetRepository>(method => method.Name switch
        {
            "FindByIdAsync" => Task.FromResult<MediaAsset?>(asset),
            _ => throw new NotSupportedException(method.Name),
        });
        var workRepo = Stub<IWorkRepository>(method => method.Name switch
        {
            "GetLineageByAssetAsync" => Task.FromResult<WorkLineage?>(
                new WorkLineage(assetId, editionId, songId, albumId, albumId, WorkKind.Child, MediaType.Music)),
            _ => throw new NotSupportedException(method.Name),
        });
        var canonicalRepo = Stub<ICanonicalValueRepository>(method => method.Name switch
        {
            "GetByEntitiesAsync" => Task.FromResult<IReadOnlyDictionary<Guid, IReadOnlyList<CanonicalValue>>>(groups),
            _ => throw new NotSupportedException(method.Name),
        });
        var bridgeRepo = Stub<IBridgeIdRepository>(method => method.Name switch
        {
            "GetByEntitiesAsync" => Task.FromResult<IReadOnlyDictionary<Guid, IReadOnlyList<BridgeIdEntry>>>(
                new Dictionary<Guid, IReadOnlyList<BridgeIdEntry>>()),
            _ => throw new NotSupportedException(method.Name),
        });
        var trackRepo = Stub<ITextTrackRepository>(method => method.Name switch
        {
            "GetByAssetAsync" => Task.FromResult<IReadOnlyList<TextTrack>>([]),
            "GetPreferredAsync" => Task.FromResult<TextTrack?>(null),
            "GetRejectionsAsync" => Task.FromResult<IReadOnlyList<TextTrackRejection>>([]),
            _ => throw new NotSupportedException(method.Name),
        });
        var source = new LyricsSource();
        var temp = Path.Combine(Path.GetTempPath(), $"tuvima-lyrics-lookup-{Guid.NewGuid():N}");
        try
        {
            var worker = new TextTrackEnrichmentWorker(
                assetRepo, workRepo, canonicalRepo, bridgeRepo, trackRepo, [source], new AssetPathService(temp),
                NullLogger<TextTrackEnrichmentWorker>.Instance);
            await worker.EnrichAsync(assetId, TextTrackKind.Lyrics);
        }
        finally
        {
            if (Directory.Exists(temp))
            {
                Directory.Delete(temp, recursive: true);
            }
        }

        return Assert.IsType<TextTrackLookup>(source.Captured);
    }

    private static int CompareBytes(byte[] left, byte[] right) => left.AsSpan().SequenceCompareTo(right);

    private static T Stub<T>(Func<MethodInfo, object?> result) where T : class
    {
        var stub = DispatchProxy.Create<T, InterfaceStub>();
        ((InterfaceStub)(object)stub).Handler = (method, _) => result(method);
        return stub;
    }

    private sealed class LyricsSource : ITextTrackProvider
    {
        public TextTrackLookup? Captured { get; private set; }

        public string Name => "LyricsSpy";

        public TextTrackKind Kind => TextTrackKind.Lyrics;

        public bool IsEnabled => true;

        public bool CanHandle(MediaType mediaType) => mediaType == MediaType.Music;

        public Task<IReadOnlyList<TextTrackCandidate>> SearchAsync(TextTrackLookup lookup, CancellationToken ct = default)
        {
            Captured = lookup;
            return Task.FromResult<IReadOnlyList<TextTrackCandidate>>([]);
        }

        public Task<TextTrackDownload?> DownloadAsync(TextTrackCandidate candidate, CancellationToken ct = default) =>
            Task.FromResult<TextTrackDownload?>(null);
    }
}
