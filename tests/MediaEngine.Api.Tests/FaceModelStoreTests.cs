using System.Security.Cryptography;
using MediaEngine.Api.Services.Faces;
using MediaEngine.Api.Services.Tools;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Services;
using MediaEngine.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Api.Tests;

/// <summary>Spec view-people-search WP6: the two face models are fetched once, verified against pinned checksums and never trusted otherwise.</summary>
public sealed class FaceModelStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "face-models-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task ModelsAreFetchedOnceVerifiedAndNotFetchedAgain()
    {
        var content = new Dictionary<string, byte[]>
        {
            [FaceModelFiles.Detection] = [1, 2, 3],
            [FaceModelFiles.Matching] = [4, 5, 6, 7],
        };
        var source = new FakeSource(content);
        var store = Store(source, content);

        Assert.All(store.Statuses(), status => Assert.Equal(FaceModelState.Missing, status.State));
        await store.EnsureAsync(CancellationToken.None);
        Assert.True(store.AreReady);
        Assert.Equal(2, source.Opened);
        Assert.StartsWith(_root, store.Directory, StringComparison.Ordinal);

        await store.EnsureAsync(CancellationToken.None);
        Assert.Equal(2, source.Opened);
    }

    [Fact]
    public async Task AFileThatDoesNotMatchItsChecksumIsRefusedAndNeverRun()
    {
        var pinned = new Dictionary<string, byte[]>
        {
            [FaceModelFiles.Detection] = [1, 2, 3],
            [FaceModelFiles.Matching] = [4, 5, 6, 7],
        };
        var served = new Dictionary<string, byte[]>
        {
            [FaceModelFiles.Detection] = [9, 9, 9],
            [FaceModelFiles.Matching] = [4, 5, 6, 7],
        };
        var store = Store(new FakeSource(served), pinned);

        await store.EnsureAsync(CancellationToken.None);

        Assert.False(store.AreReady);
        var detection = store.Statuses().Single(status => status.FileName == FaceModelFiles.Detection);
        Assert.Equal(FaceModelState.Problem, detection.State);
        Assert.False(File.Exists(store.PathOf(FaceModelFiles.Detection)));
        Assert.Equal(FaceModelState.Ready, store.Statuses().Single(status => status.FileName == FaceModelFiles.Matching).State);
    }

    [Fact]
    public void ThePinnedListNamesBothModelsOnGithubWithChecksums()
    {
        var settings = new ToolDownloadSettings();
        settings.RemoveDisallowedEntries();
        Assert.Equal(2, settings.FaceModels.Count);
        Assert.All(settings.FaceModels, model => Assert.True(ToolDownloadSettings.IsAllowedAddress(model.Url)));
        Assert.Contains("media.githubusercontent.com", ToolDownloadSettings.AllowedRedirectHosts);
    }

    private FaceModelProvider Store(FakeSource source, Dictionary<string, byte[]> pinned)
    {
        var settings = new ToolDownloadSettings
        {
            FaceModels = pinned.Select(pair => new DownloadFile
            {
                Name = pair.Key,
                Url = "https://github.com/opencv/opencv_zoo/raw/main/models/" + pair.Key,
                Sha256 = Convert.ToHexString(SHA256.HashData(pair.Value)),
            }).ToList(),
        };
        var config = Path.Combine(_root, "config");
        Directory.CreateDirectory(config);
        File.WriteAllText(Path.Combine(config, "tool_downloads.json"), System.Text.Json.JsonSerializer.Serialize(settings));
        return new FaceModelProvider(new ConfigurationDirectoryLoader(config), new AssetPathService(_root, null, _root), source, NullLogger<FaceModelProvider>.Instance);
    }

    private sealed class FakeSource(Dictionary<string, byte[]> served) : IToolDownloadSource
    {
        public int Opened { get; private set; }

        public Task<ToolDownload> OpenAsync(Uri address, CancellationToken ct)
        {
            Opened++;
            var bytes = served[Path.GetFileName(address.AbsolutePath)];
            return Task.FromResult(new ToolDownload(new MemoryStream(bytes), bytes.Length));
        }
    }
}
