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

public sealed class SubdlPreferenceContinuityTests
{
    [Fact]
    public async Task ExistingOpenSubtitlesPreference_RemainsSelectedAfterNewSubdlDownload()
    {
        var assetId = Guid.NewGuid();
        var workId = Guid.NewGuid();
        var editionId = Guid.NewGuid();
        var asset = new MediaAsset { Id = assetId, EditionId = editionId,
            FilePathRoot = Path.Combine(Path.GetTempPath(), $"tuvima-absent-{Guid.NewGuid():N}", "Movie.mkv") };
        var old = new TextTrack { Id = Guid.NewGuid(), AssetId = assetId, Kind = TextTrackKind.Subtitles,
            Language = "en", Provider = "OpenSubtitles", IsPreferred = true, IsUserOwned = false,
            LocalPath = "old.vtt" };
        var tracks = new List<TextTrack> { old };
        var preferredCalls = 0;
        var assetRepo = Stub<IMediaAssetRepository>(method => method.Name switch
        {
            "FindByIdAsync" => Task.FromResult<MediaAsset?>(asset),
            _ => throw new NotSupportedException(method.Name)
        });
        var workRepo = Stub<IWorkRepository>(method => method.Name switch
        {
            "GetLineageByAssetAsync" => Task.FromResult<WorkLineage?>(
                new WorkLineage(assetId, editionId, workId, null, workId, WorkKind.Standalone, MediaType.Movies)),
            _ => throw new NotSupportedException(method.Name)
        });
        var canonicalRepo = Stub<ICanonicalValueRepository>(method => method.Name switch
        {
            "GetByEntitiesAsync" => Task.FromResult<IReadOnlyDictionary<Guid, IReadOnlyList<CanonicalValue>>>(
                new Dictionary<Guid, IReadOnlyList<CanonicalValue>>()),
            _ => throw new NotSupportedException(method.Name)
        });
        var bridgeRepo = Stub<IBridgeIdRepository>(method => method.Name switch
        {
            "GetByEntitiesAsync" => Task.FromResult<IReadOnlyDictionary<Guid, IReadOnlyList<BridgeIdEntry>>>(
                new Dictionary<Guid, IReadOnlyList<BridgeIdEntry>>
                {
                    [workId] = [new BridgeIdEntry { EntityId = workId, IdType = BridgeIdKeys.TmdbId, IdValue = "123" }]
                }),
            _ => throw new NotSupportedException(method.Name)
        });
        var trackRepo = Stub<ITextTrackRepository>(method => method.Name switch
        {
            "GetByAssetAsync" => Task.FromResult<IReadOnlyList<TextTrack>>(tracks.ToList()),
            "GetPreferredAsync" => Task.FromResult<TextTrack?>(tracks.SingleOrDefault(track => track.IsPreferred)),
            "UpsertAsync" => Task.CompletedTask,
            "SetPreferredAsync" => Task.CompletedTask,
            _ => throw new NotSupportedException(method.Name)
        }, (method, args) =>
        {
            if (method.Name == "UpsertAsync")
            {
                var track = (TextTrack)args![0]!;
                tracks.RemoveAll(existing => existing.Id == track.Id);
                tracks.Add(track);
            }
            if (method.Name == "SetPreferredAsync") preferredCalls++;
        });
        var temp = Path.Combine(Path.GetTempPath(), $"tuvima-subdl-test-{Guid.NewGuid():N}");
        try
        {
            var worker = new TextTrackEnrichmentWorker(assetRepo, workRepo, canonicalRepo, bridgeRepo,
                trackRepo, [new DownloadProvider()], new AssetPathService(temp),
                NullLogger<TextTrackEnrichmentWorker>.Instance);

            var result = await worker.EnrichAsync(assetId, TextTrackKind.Subtitles);

            Assert.Equal("Updated", result.Status);
            Assert.Equal(old.Id, result.TrackId);
            Assert.Equal(0, preferredCalls);
            Assert.Equal(2, tracks.Count);
            Assert.True(old.IsPreferred);
            Assert.Contains(tracks, track => track.Provider == "SubDL" && File.Exists(track.LocalPath));
        }
        finally
        {
            if (Directory.Exists(temp)) Directory.Delete(temp, recursive: true);
        }
    }

    private static T Stub<T>(Func<MethodInfo, object?> result,
        Action<MethodInfo, object?[]?>? before = null) where T : class
    {
        var stub = DispatchProxy.Create<T, InterfaceStub>();
        ((InterfaceStub)(object)stub).Handler = (method, args) =>
        {
            before?.Invoke(method, args);
            return result(method);
        };
        return stub;
    }

    private sealed class DownloadProvider : ITextTrackProvider
    {
        public string Name => "SubDL";
        public TextTrackKind Kind => TextTrackKind.Subtitles;
        public bool IsEnabled => true;
        public bool CanHandle(MediaType mediaType) => mediaType == MediaType.Movies;
        public Task<IReadOnlyList<TextTrackCandidate>> SearchAsync(TextTrackLookup lookup, CancellationToken ct = default)
        {
            Assert.Equal("123", lookup.SubtitleContext?.Movie?.TmdbMovieId);
            return Task.FromResult<IReadOnlyList<TextTrackCandidate>>([
                new TextTrackCandidate("SubDL", TextTrackKind.Subtitles, "n42/f1", null, "en", "srt", 0.93, false, null)
            ]);
        }
        public Task<TextTrackDownload?> DownloadAsync(TextTrackCandidate candidate, CancellationToken ct = default) =>
            Task.FromResult<TextTrackDownload?>(new TextTrackDownload(candidate,
                "1\n00:00:01,000 --> 00:00:02,000\nHello\n", "srt", "vtt"));
    }
}

public class InterfaceStub : DispatchProxy
{
    public Func<MethodInfo, object?[]?, object?> Handler { get; set; } = null!;
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => Handler(targetMethod!, args);
}
