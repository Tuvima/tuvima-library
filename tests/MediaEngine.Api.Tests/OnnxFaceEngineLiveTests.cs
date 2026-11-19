using MediaEngine.Api.Services.Faces;
using MediaEngine.Api.Services.Tools;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Services;
using MediaEngine.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Api.Tests;

/// <summary>
/// Runs the real YuNet and SFace models on a real photo. Skipped unless <c>TUVIMA_FACE_ASSETS</c> names a folder holding the two model files and
/// this machine's ONNX Runtime library, and <c>TUVIMA_FACE_PHOTO</c> names a photo of one face; the cloud has neither, so this is a local check.
/// </summary>
[Trait("Category", "LiveProvider")]
public sealed class OnnxFaceEngineLiveTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "face-live-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task APhotoWithOneFace_GivesOneFaceWhoseLookSurvivesAResize()
    {
        var assets = Environment.GetEnvironmentVariable("TUVIMA_FACE_ASSETS");
        var photo = Environment.GetEnvironmentVariable("TUVIMA_FACE_PHOTO");
        if (string.IsNullOrWhiteSpace(assets) || string.IsNullOrWhiteSpace(photo) || !File.Exists(photo))
        {
            return;
        }

        var config = Path.Combine(_root, "config");
        Directory.CreateDirectory(config);
        var provider = new FaceModelProvider(new ConfigurationDirectoryLoader(config), new AssetPathService(_root, null, _root), new NoDownloads(), NullLogger<FaceModelProvider>.Instance);
        Directory.CreateDirectory(Path.Combine(provider.Directory, "runtime"));
        foreach (var file in Directory.EnumerateFiles(assets))
        {
            var name = Path.GetFileName(file);
            var target = name.Contains("onnxruntime", StringComparison.OrdinalIgnoreCase) ? Path.Combine(provider.Directory, "runtime", name) : Path.Combine(provider.Directory, name);
            File.Copy(file, target);
        }

        using var engine = new OnnxFaceEngine(provider, NullLogger<OnnxFaceEngine>.Instance);
        Assert.True(engine.IsAvailable);

        var faces = await engine.AnalyseAsync(photo, CancellationToken.None);
        var face = Assert.Single(faces);
        Assert.InRange(face.Width, 0.05, 0.9);
        Assert.Equal(128, face.Embedding.Length);
        Assert.Equal(1.0, Math.Sqrt(face.Embedding.Sum(value => (double)value * value)), 3);

        var smaller = Environment.GetEnvironmentVariable("TUVIMA_FACE_PHOTO_SMALL");
        if (smaller is not null && File.Exists(smaller))
        {
            var other = Assert.Single(await engine.AnalyseAsync(smaller, CancellationToken.None));
            var similarity = face.Embedding.Zip(other.Embedding, (a, b) => (double)a * b).Sum();
            Assert.True(similarity > 0.7, $"The same face at another size should look alike, got {similarity:0.00}.");
        }
    }

    private sealed class NoDownloads : IToolDownloadSource
    {
        public Task<ToolDownload> OpenAsync(Uri address, CancellationToken ct) => throw new InvalidOperationException("No downloads in this check.");
    }
}
