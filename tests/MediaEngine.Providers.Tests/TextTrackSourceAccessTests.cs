using System.Reflection;
using MediaEngine.Domain;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Services;
using MediaEngine.Providers.Workers;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Providers.Tests;

/// <summary>
/// A lyrics or subtitle refresh asks only the sources the central plugin check allows for the person who asked and the item
/// (Plugin architecture spec PA2): a source outside the person's audience, or kept away from this item, is never contacted.
/// </summary>
public sealed class TextTrackSourceAccessTests
{
    [Fact]
    public async Task OnlyTheSourcesTheCentralCheckAllows_AreAsked()
    {
        var open = new Source("OpenSource");
        var restricted = new Source("AdminsOnlySource");
        var gate = new Gate(allowed: ["OpenSource"]);

        var result = await Run(gate, [restricted, open], PluginAudience.Everyone);

        Assert.Equal("Updated", result.Status);
        Assert.Equal(1, open.Searches);
        Assert.Equal(0, restricted.Searches);
    }

    [Fact]
    public async Task WhenNoSourceIsAllowed_NothingIsContacted_AndTheAnswerIsPlain()
    {
        var restricted = new Source("AdminsOnlySource");

        var result = await Run(new Gate(allowed: []), [restricted], PluginAudience.Everyone);

        Assert.Equal("NotAllowed", result.Status);
        Assert.Equal(0, restricted.Searches);
    }

    [Fact]
    public async Task TheCheckIsToldWhoAsked_AndWhichItem()
    {
        var gate = new Gate(allowed: ["OpenSource"]);

        await Run(gate, [new Source("OpenSource")], PluginAudience.Adults);

        var query = Assert.Single(gate.Queries);
        Assert.Equal("OpenSource", query.ItemId);
        Assert.Equal(PluginAudience.Adults, query.Requester);
        Assert.Equal(Library, query.LibraryId);
    }

    [Fact]
    public async Task ALibraryAskingItself_HasNoAudience_ButTheCheckStillRuns()
    {
        var gate = new Gate(allowed: ["OpenSource"]);

        var result = await Run(gate, [new Source("OpenSource")], requester: null);

        Assert.Equal("Updated", result.Status);
        Assert.Null(Assert.Single(gate.Queries).Requester);
    }

    private const string Library = "aaaaaaaa-0000-4000-8000-0000000000a1";

    private static async Task<TextTrackEnrichmentResult> Run(Gate gate, IReadOnlyList<Source> sources, PluginAudience? requester)
    {
        var assetId = Guid.NewGuid();
        var workId = Guid.NewGuid();
        var editionId = Guid.NewGuid();
        var asset = new MediaAsset
        {
            Id = assetId,
            EditionId = editionId,
            LibraryId = Library,
            FilePathRoot = Path.Combine(Path.GetTempPath(), $"tuvima-absent-{Guid.NewGuid():N}", "Movie.mkv"),
        };
        var tracks = new List<TextTrack>();
        var assetRepo = Stub<IMediaAssetRepository>(method => method.Name switch
        {
            "FindByIdAsync" => Task.FromResult<MediaAsset?>(asset),
            _ => throw new NotSupportedException(method.Name),
        });
        var workRepo = Stub<IWorkRepository>(method => method.Name switch
        {
            "GetLineageByAssetAsync" => Task.FromResult<WorkLineage?>(
                new WorkLineage(assetId, editionId, workId, null, workId, WorkKind.Standalone, MediaType.Movies)),
            _ => throw new NotSupportedException(method.Name),
        });
        var canonicalRepo = Stub<ICanonicalValueRepository>(method => method.Name switch
        {
            "GetByEntitiesAsync" => Task.FromResult<IReadOnlyDictionary<Guid, IReadOnlyList<CanonicalValue>>>(new Dictionary<Guid, IReadOnlyList<CanonicalValue>>()),
            _ => throw new NotSupportedException(method.Name),
        });
        var bridgeRepo = Stub<IBridgeIdRepository>(method => method.Name switch
        {
            "GetByEntitiesAsync" => Task.FromResult<IReadOnlyDictionary<Guid, IReadOnlyList<BridgeIdEntry>>>(
                new Dictionary<Guid, IReadOnlyList<BridgeIdEntry>>
                {
                    [workId] = [new BridgeIdEntry { EntityId = workId, IdType = BridgeIdKeys.TmdbId, IdValue = "123" }],
                }),
            _ => throw new NotSupportedException(method.Name),
        });
        var trackRepo = Stub<ITextTrackRepository>(method => method.Name switch
        {
            "GetByAssetAsync" => Task.FromResult<IReadOnlyList<TextTrack>>(tracks.ToList()),
            "GetPreferredAsync" => Task.FromResult<TextTrack?>(tracks.SingleOrDefault(track => track.IsPreferred)),
            "UpsertAsync" => Task.CompletedTask,
            "SetPreferredAsync" => Task.CompletedTask,
            _ => throw new NotSupportedException(method.Name),
        }, (method, args) =>
        {
            if (method.Name == "UpsertAsync")
            {
                var track = (TextTrack)args![0]!;
                tracks.RemoveAll(existing => existing.Id == track.Id);
                tracks.Add(track);
            }
        });
        var temp = Path.Combine(Path.GetTempPath(), $"tuvima-text-track-access-{Guid.NewGuid():N}");
        try
        {
            var worker = new TextTrackEnrichmentWorker(
                assetRepo, workRepo, canonicalRepo, bridgeRepo, trackRepo, sources, new AssetPathService(temp),
                NullLogger<TextTrackEnrichmentWorker>.Instance, accessGate: gate);
            return await worker.EnrichAsync(assetId, TextTrackKind.Subtitles, requester: requester);
        }
        finally
        {
            if (Directory.Exists(temp))
            {
                Directory.Delete(temp, recursive: true);
            }
        }
    }

    private static T Stub<T>(Func<MethodInfo, object?> result, Action<MethodInfo, object?[]?>? before = null) where T : class
    {
        var stub = DispatchProxy.Create<T, InterfaceStub>();
        ((InterfaceStub)(object)stub).Handler = (method, args) =>
        {
            before?.Invoke(method, args);
            return result(method);
        };
        return stub;
    }

    private sealed class Gate(IReadOnlyList<string> allowed) : IPluginAccessGate
    {
        public List<PluginAccessQuery> Queries { get; } = [];

        public Task<PluginAccessOutcome> CheckAsync(PluginAccessQuery query, CancellationToken ct = default)
        {
            Queries.Add(query);
            return Task.FromResult(allowed.Contains(query.ItemId) ? PluginAccessOutcome.Allowed : PluginAccessOutcome.NotInAudience);
        }
    }

    private sealed class Source(string name) : ITextTrackProvider
    {
        public int Searches { get; private set; }

        public string Name => name;

        public TextTrackKind Kind => TextTrackKind.Subtitles;

        public bool IsEnabled => true;

        public bool CanHandle(MediaType mediaType) => mediaType == MediaType.Movies;

        public Task<IReadOnlyList<TextTrackCandidate>> SearchAsync(TextTrackLookup lookup, CancellationToken ct = default)
        {
            Searches++;
            return Task.FromResult<IReadOnlyList<TextTrackCandidate>>([
                new TextTrackCandidate(name, TextTrackKind.Subtitles, "n42/f1", null, "en", "srt", 0.93, false, null),
            ]);
        }

        public Task<TextTrackDownload?> DownloadAsync(TextTrackCandidate candidate, CancellationToken ct = default) =>
            Task.FromResult<TextTrackDownload?>(new TextTrackDownload(candidate, "1\n00:00:01,000 --> 00:00:02,000\nHello\n", "srt", "vtt"));
    }
}
