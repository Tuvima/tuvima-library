using System.Reflection;
using MediaEngine.Domain;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Services;
using MediaEngine.Providers.Providers;
using MediaEngine.Providers.Workers;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Providers.Tests;

/// <summary>
/// One lyrics source being down must not stop the others from being asked; the lookup only ends as "could not be reached" when
/// at least one source was unreachable and none gave an answer (spec music-player-and-listen-fixes D3).
/// </summary>
public sealed class TextTrackUnreachableSourceTests
{
    [Fact]
    public async Task AnUnreachableLyricsSource_DoesNotStopTheNextSourceFromBeingAsked()
    {
        var down = new FakeSource("Down", unreachable: true);
        var up = new FakeSource("Up", instrumental: true);

        var result = await EnrichAsync(down, up);

        Assert.Equal(1, down.Searches);
        Assert.Equal(1, up.Searches);
        Assert.Equal("Instrumental", result.Status);
    }

    [Fact]
    public async Task ALyricsLookup_WhereOneSourceWasUnreachableAndTheOtherFoundNothing_EndsAsUnreachable()
    {
        var down = new FakeSource("Down", unreachable: true);
        var empty = new FakeSource("Empty");

        var result = await EnrichAsync(down, empty);

        Assert.Equal(1, empty.Searches);
        Assert.Equal("ProviderUnavailable", result.Status);
        Assert.Equal("Down could not be reached.", result.Message);
    }

    [Fact]
    public async Task ALyricsLookup_WhereEverySourceWasUnreachable_EndsAsUnreachable()
    {
        var first = new FakeSource("First", unreachable: true);
        var second = new FakeSource("Second", unreachable: true);

        var result = await EnrichAsync(first, second);

        Assert.Equal(1, first.Searches);
        Assert.Equal(1, second.Searches);
        Assert.Equal("ProviderUnavailable", result.Status);
    }

    [Fact]
    public async Task ALyricsLookup_WhereEverySourceAnsweredWithNothing_EndsAsNoResult()
    {
        var result = await EnrichAsync(new FakeSource("One"), new FakeSource("Two"));

        Assert.Equal("NoResult", result.Status);
    }

    [Fact]
    public async Task ASourceThatGoesDownAtTheDownload_DoesNotStopTheNextSourceFromBeingTried()
    {
        var down = new FakeSource("Down", offersLyrics: true, downloadUnreachable: true);
        var up = new FakeSource("Up", offersLyrics: true);

        var result = await EnrichAsync(down, up);

        Assert.Equal(1, down.Downloads);
        Assert.Equal(1, up.Downloads);
        Assert.Equal("Updated", result.Status);
        Assert.Contains("Up", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ALyricsLookup_WhereEverySourceWentDownAtTheDownload_EndsAsUnreachable()
    {
        var first = new FakeSource("First", offersLyrics: true, downloadUnreachable: true);
        var second = new FakeSource("Second", offersLyrics: true, downloadUnreachable: true);

        var result = await EnrichAsync(first, second);

        Assert.Equal(1, first.Downloads);
        Assert.Equal(1, second.Downloads);
        Assert.Equal("ProviderUnavailable", result.Status);
        Assert.Equal("First could not be reached.", result.Message);
    }

    private static async Task<TextTrackEnrichmentResult> EnrichAsync(params ITextTrackProvider[] sources)
    {
        var assetId = Guid.NewGuid();
        var editionId = Guid.NewGuid();
        var songId = Guid.NewGuid();
        var albumId = Guid.NewGuid();
        var asset = new MediaAsset
        {
            Id = assetId,
            EditionId = editionId,
            LibraryId = "aaaaaaaa-0000-4000-8000-0000000000a1",
            FilePathRoot = Path.Combine(Path.GetTempPath(), $"tuvima-absent-{Guid.NewGuid():N}", "Song.flac"),
        };
        var values = new Dictionary<Guid, IReadOnlyList<CanonicalValue>>
        {
            [songId] =
            [
                new() { EntityId = songId, Key = "title", Value = "Song A", LastScoredAt = DateTimeOffset.UtcNow },
                new() { EntityId = songId, Key = "artist", Value = "Artist C", LastScoredAt = DateTimeOffset.UtcNow },
            ],
        };

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
            "GetByEntitiesAsync" => Task.FromResult<IReadOnlyDictionary<Guid, IReadOnlyList<CanonicalValue>>>(values),
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
            "UpsertAsync" => Task.CompletedTask,
            "SetPreferredAsync" => Task.CompletedTask,
            _ => throw new NotSupportedException(method.Name),
        });
        var temp = Path.Combine(Path.GetTempPath(), $"tuvima-lyrics-unreachable-{Guid.NewGuid():N}");
        try
        {
            var worker = new TextTrackEnrichmentWorker(
                assetRepo, workRepo, canonicalRepo, bridgeRepo, trackRepo, sources, new AssetPathService(temp),
                NullLogger<TextTrackEnrichmentWorker>.Instance);
            return await worker.EnrichAsync(assetId, TextTrackKind.Lyrics);
        }
        finally
        {
            if (Directory.Exists(temp))
            {
                Directory.Delete(temp, recursive: true);
            }
        }
    }

    private static T Stub<T>(Func<MethodInfo, object?> result) where T : class
    {
        var stub = DispatchProxy.Create<T, InterfaceStub>();
        ((InterfaceStub)(object)stub).Handler = (method, _) => result(method);
        return stub;
    }

    private sealed class FakeSource(
        string name, bool unreachable = false, bool instrumental = false, bool offersLyrics = false, bool downloadUnreachable = false) : ITextTrackProvider
    {
        public int Searches { get; private set; }

        public int Downloads { get; private set; }

        public string Name => name;

        public TextTrackKind Kind => TextTrackKind.Lyrics;

        public bool IsEnabled => true;

        public bool CanHandle(MediaType mediaType) => mediaType == MediaType.Music;

        public Task<IReadOnlyList<TextTrackCandidate>> SearchAsync(TextTrackLookup lookup, CancellationToken ct = default)
        {
            Searches++;
            if (unreachable)
            {
                throw new TextTrackLookupException("ProviderUnavailable", $"{name} could not be reached.");
            }

            IReadOnlyList<TextTrackCandidate> found = instrumental
                ? [new TextTrackCandidate(name, TextTrackKind.Lyrics, "1", null, "und", "lrc", 1, false, null, IsInstrumental: true)]
                : offersLyrics
                    ? [new TextTrackCandidate(name, TextTrackKind.Lyrics, "1", null, "und", "txt", 1, false, null)]
                    : [];
            return Task.FromResult(found);
        }

        public Task<TextTrackDownload?> DownloadAsync(TextTrackCandidate candidate, CancellationToken ct = default)
        {
            Downloads++;
            if (downloadUnreachable)
            {
                throw new TextTrackLookupException("ProviderUnavailable", $"{name} could not be reached.");
            }

            return Task.FromResult<TextTrackDownload?>(
                offersLyrics ? new TextTrackDownload(candidate, "Plain words", "txt", "txt") : null);
        }
    }
}
