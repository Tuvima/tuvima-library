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

public sealed class VideoSidecarSubtitleTests
{
    [Fact]
    public async Task VideoWithASubtitleFileBesideIt_ImportsItWithNoProviderAndNoNetwork()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"tuvima-video-subs-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            var media = Path.Combine(folder, "Talk.mp4");
            File.WriteAllBytes(media, [0]);
            File.WriteAllText(Path.Combine(folder, "Talk.en.srt"), "1\n00:00:01,000 --> 00:00:02,000\nHello\n");

            var assetId = Guid.NewGuid();
            var workId = Guid.NewGuid();
            var editionId = Guid.NewGuid();
            var asset = new MediaAsset { Id = assetId, EditionId = editionId, FilePathRoot = media };
            var tracks = new List<TextTrack>();
            var assetRepo = Stub<IMediaAssetRepository>(method => method.Name == "FindByIdAsync"
                ? Task.FromResult<MediaAsset?>(asset) : throw new NotSupportedException(method.Name));
            var workRepo = Stub<IWorkRepository>(method => method.Name == "GetLineageByAssetAsync"
                ? Task.FromResult<WorkLineage?>(new WorkLineage(assetId, editionId, workId, null, workId, WorkKind.Standalone, MediaType.Videos))
                : throw new NotSupportedException(method.Name));
            var trackRepo = Stub<ITextTrackRepository>(method => method.Name switch
            {
                "GetByAssetAsync" => Task.FromResult<IReadOnlyList<TextTrack>>(tracks.ToList()),
                "GetPreferredAsync" => Task.FromResult<TextTrack?>(tracks.SingleOrDefault(track => track.IsPreferred)),
                "UpsertAsync" or "SetPreferredAsync" => Task.CompletedTask,
                _ => throw new NotSupportedException(method.Name),
            }, (method, args) =>
            {
                if (method.Name == "UpsertAsync")
                {
                    tracks.Add((TextTrack)args![0]!);
                }
            });
            var temp = Path.Combine(folder, "central");
            var worker = new TextTrackEnrichmentWorker(
                assetRepo,
                workRepo,
                Stub<ICanonicalValueRepository>(method => throw new NotSupportedException(method.Name)),
                Stub<IBridgeIdRepository>(method => throw new NotSupportedException(method.Name)),
                trackRepo,
                [],
                new AssetPathService(temp),
                NullLogger<TextTrackEnrichmentWorker>.Instance);

            var result = await worker.EnrichAsync(assetId, TextTrackKind.Subtitles);

            Assert.Equal("Updated", result.Status);
            var track = Assert.Single(tracks);
            Assert.Equal("local", track.Provider);
            Assert.Equal("en", track.Language);
            Assert.True(File.Exists(track.LocalPath));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
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
}
