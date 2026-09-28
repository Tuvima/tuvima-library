using MediaEngine.Api.Services.View;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Models;
using MediaEngine.Domain.Services;
using MediaEngine.Storage.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;

namespace MediaEngine.Api.Tests;

public sealed class ViewThumbnailServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"tuvima-view-thumbnail-{Guid.NewGuid():N}");

    [Fact]
    public async Task GetOrCreateAsync_WritesManagedDerivative_WithoutChangingOriginal()
    {
        Directory.CreateDirectory(_root);
        var sourcePath = Path.Combine(_root, "source.jpg");
        WriteJpeg(sourcePath, 1200, 800);
        var originalBytes = await File.ReadAllBytesAsync(sourcePath);
        var originalWriteTime = File.GetLastWriteTimeUtc(sourcePath);
        var itemId = Guid.NewGuid();
        var paths = new AssetPathService(_root);
        var service = new ViewThumbnailService(paths, NullLogger<ViewThumbnailService>.Instance);

        var first = await service.GetOrCreateAsync(itemId, Location(itemId, sourcePath));

        Assert.NotNull(first);
        Assert.True(File.Exists(first));
        Assert.StartsWith(paths.DerivedRoot, first, StringComparison.OrdinalIgnoreCase);
        using (var thumbnail = SKBitmap.Decode(first))
        {
            Assert.NotNull(thumbnail);
            Assert.Equal(640, Math.Max(thumbnail.Width, thumbnail.Height));
        }
        Assert.Equal(originalBytes, await File.ReadAllBytesAsync(sourcePath));
        Assert.Equal(originalWriteTime, File.GetLastWriteTimeUtc(sourcePath));

        var derivativeWriteTime = File.GetLastWriteTimeUtc(first);
        var second = await service.GetOrCreateAsync(itemId, Location(itemId, sourcePath));
        Assert.Equal(first, second);
        Assert.Equal(derivativeWriteTime, File.GetLastWriteTimeUtc(first));
    }

    [Fact]
    public async Task GetOrCreateAsync_ReturnsNull_ForNonImageContent()
    {
        Directory.CreateDirectory(_root);
        var sourcePath = Path.Combine(_root, "notes.txt");
        await File.WriteAllTextAsync(sourcePath, "personal media notes");
        var itemId = Guid.NewGuid();
        var service = new ViewThumbnailService(
            new AssetPathService(_root), NullLogger<ViewThumbnailService>.Instance);

        var result = await service.GetOrCreateAsync(
            itemId, Location(itemId, sourcePath) with { MimeType = "text/plain" });

        Assert.Null(result);
    }

    [Fact]
    public async Task GetOrCreateAsync_UsesFfmpegForVideoFrameThumbnail()
    {
        Directory.CreateDirectory(_root);
        var sourcePath = Path.Combine(_root, "clip.mp4");
        await File.WriteAllBytesAsync(sourcePath, [0, 0, 0, 24, 0x66, 0x74, 0x79, 0x70]);
        var itemId = Guid.NewGuid();
        var ffmpeg = new ThumbnailFfmpegService();
        var service = new ViewThumbnailService(
            new AssetPathService(_root),
            NullLogger<ViewThumbnailService>.Instance,
            ffmpeg);

        var result = await service.GetOrCreateAsync(
            itemId,
            Location(itemId, sourcePath) with { MimeType = "video/mp4" });

        Assert.NotNull(result);
        Assert.True(File.Exists(result));
        Assert.Contains("-frames:v 1", ffmpeg.LastArguments, StringComparison.Ordinal);
        using var thumbnail = SKBitmap.Decode(result);
        Assert.NotNull(thumbnail);
    }

    [Fact]
    public async Task GetOrCreateAsync_UsesFfmpegWhenSkiaCannotDecodeHeic()
    {
        Directory.CreateDirectory(_root);
        var sourcePath = Path.Combine(_root, "photo.heic");
        await File.WriteAllBytesAsync(sourcePath, [0, 0, 0, 24, 0x66, 0x74, 0x79, 0x70]);
        var itemId = Guid.NewGuid();
        var ffmpeg = new ThumbnailFfmpegService();
        var service = new ViewThumbnailService(
            new AssetPathService(_root),
            NullLogger<ViewThumbnailService>.Instance,
            ffmpeg);

        var result = await service.GetOrCreateAsync(
            itemId,
            Location(itemId, sourcePath) with { MimeType = "image/heic" });

        Assert.NotNull(result);
        Assert.True(File.Exists(result));
        Assert.Contains("-frames:v 1", ffmpeg.LastArguments, StringComparison.Ordinal);
        Assert.DoesNotContain("-ss 0.5", ffmpeg.LastArguments, StringComparison.Ordinal);
    }

    private static LocalAssetContentLocation Location(Guid itemId, string filePath) => new(
        itemId, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, null,
        filePath, "image/jpeg", new FileInfo(filePath).Length, "hash", "primary", null);

    private static void WriteJpeg(string path, int width, int height)
    {
        using var bitmap = new SKBitmap(width, height);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.DarkSlateBlue);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 90);
        using var output = File.Create(path);
        data.SaveTo(output);
    }

    private sealed class ThumbnailFfmpegService : IFFmpegService
    {
        public string? LastArguments { get; private set; }
        public string? FfmpegPath => "/usr/bin/ffmpeg";
        public string? FfprobePath => "/usr/bin/ffprobe";
        public bool IsAvailable => true;
        public HardwareCapabilities HardwareCapabilities { get; } = new();

        public Task<(int ExitCode, string Output, string Error)> RunAsync(
            IReadOnlyList<string> arguments, CancellationToken ct = default)
        {
            LastArguments = string.Join(" ", arguments);
            WriteJpeg(arguments[^1], 4032, 3024);
            return Task.FromResult((0, string.Empty, string.Empty));
        }

        public Task<MediaProbeResult?> ProbeAsync(string filePath, CancellationToken ct = default) =>
            Task.FromResult<MediaProbeResult?>(null);

        public Task<(int ExitCode, string Output, string Error)> RunAsync(
            string arguments,
            CancellationToken ct = default)
        {
            LastArguments = arguments;
            var outputPath = arguments.Split('"', StringSplitOptions.RemoveEmptyEntries)[^1];
            WriteJpeg(outputPath, 320, 180);
            return Task.FromResult((0, string.Empty, string.Empty));
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
