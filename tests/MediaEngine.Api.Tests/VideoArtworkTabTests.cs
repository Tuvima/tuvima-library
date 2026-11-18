using System.Globalization;
using MediaEngine.Api.Endpoints;
using MediaEngine.Api.Services;
using MediaEngine.Api.Services.Metadata;
using MediaEngine.Contracts.Artwork;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Models;
using MediaEngine.Domain.Services;
using MediaEngine.Processors;
using MediaEngine.Storage;
using SkiaSharp;

namespace MediaEngine.Api.Tests;

/// <summary>The Artwork tab on a folder of Other videos, a video and an episode (review mock-ups 2026-11-18, D2).</summary>
public sealed class VideoArtworkTabTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"tuvima_video_art_{Guid.NewGuid():N}");
    private readonly DatabaseConnection _database;
    private readonly ArtworkAssetService _assets;
    private readonly string _video;

    public VideoArtworkTabTests()
    {
        Directory.CreateDirectory(_root);
        _database = new DatabaseConnection(Path.Combine(_root, "library.db"));
        _database.InitializeSchema();
        _database.RunStartupChecks();
        _assets = new ArtworkAssetService(_database, new AssetPathService(_root), new NoHttpFactory());
        _video = Path.Combine(_root, "media", "lesson.mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(_video)!);
        File.WriteAllBytes(_video, [1, 2, 3, 4]);
    }

    public void Dispose()
    {
        _database.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    [Theory]
    [InlineData("Videos", "series")]
    [InlineData("Videos", "section")]
    [InlineData("Videos", "video")]
    [InlineData("Video", "item")]
    public void AFolderOfVideos_AndAVideo_OfferCoverBackgroundAndLogo(string mediaType, string scopeId) =>
        Assert.Equal(["CoverArt", "Background", "Logo"], ArtworkScopeService.GetScopedArtworkSlots(mediaType, scopeId));

    [Fact]
    public void AnEpisode_KeepsItsStill_ThatTheFrameStripFills() =>
        Assert.Equal(["EpisodeStill"], ArtworkScopeService.GetScopedArtworkSlots("TV", "episode"));

    [Theory]
    [InlineData("Videos", "series")]
    [InlineData("Videos", "section")]
    [InlineData("Videos", "video")]
    [InlineData("TV", "episode")]
    public void TheEditorOffersAnArtworkTab_OnEachOfTheseKinds(string mediaType, string scopeId)
    {
        var tabs = MetadataEndpoints.BuildEditorAvailableTabs("edit", mediaType, scopeId, canEditArtwork: true, _video);

        Assert.Contains("artwork", tabs);
        Assert.Contains("details", tabs);
    }

    [Fact]
    public void AVideoFolder_IsNotAnAutomaticGroup_SoItsCoverCanBePicked() =>
        Assert.False(ArtworkScopeService.IsAutomaticGroupScope("series", "Videos"));

    [Theory]
    [InlineData("CoverArt", true)]
    [InlineData("Background", true)]
    [InlineData("EpisodeStill", true)]
    [InlineData("Logo", false)]
    [InlineData("Headshot", false)]
    public void AFrameCanBeChosenForTheCoverTheBackgroundAndAStill_ButNeverALogo(string slot, bool accepted) =>
        Assert.Equal(accepted, VideoFrameArtworkService.AcceptsFrames(slot));

    [Fact]
    public async Task ReadingTheFrames_OfAVideoWithNoCover_SavesTheBestFrameAsItsDefaultCover_FromVideoFrame()
    {
        var service = Service();
        var owner = Guid.NewGuid();

        var frames = await service.ListAsync(owner, "video", _video, "Work", owner, "Lesson", "Videos", CancellationToken.None);

        Assert.Equal(VideoFrameCandidateService.SampleCount, frames.Items.Count);
        var best = Assert.Single(frames.Items, frame => frame.IsBest);
        var workspace = await _assets.GetEntityAsync("Work", owner, CancellationToken.None);
        var cover = Assert.Single(workspace.Variants);
        Assert.True(cover.IsPreferred);
        Assert.Equal("Primary", cover.Role);
        Assert.Equal("CoverArt", cover.SourceAssetType);
        Assert.Equal(VideoFrameSources.GeneratedProvider, cover.SourceProvider);
        Assert.Equal(best.SourceKey, cover.SourceUrl);
    }

    [Fact]
    public async Task ReadingTheFramesTwice_DoesNotSaveASecondDefaultCover()
    {
        var service = Service();
        var owner = Guid.NewGuid();

        await service.ListAsync(owner, "video", _video, "Work", owner, "Lesson", "Videos", CancellationToken.None);
        await service.ListAsync(owner, "video", _video, "Work", owner, "Lesson", "Videos", CancellationToken.None);

        Assert.Single((await _assets.GetEntityAsync("Work", owner, CancellationToken.None)).Variants);
    }

    [Fact]
    public async Task ADefaultCover_IsNotMade_WhenTheVideoAlreadyHasACoverPicture()
    {
        var service = Service();
        var owner = Guid.NewGuid();
        await service.ImportAsync(_video, 2, "CoverArt", "Work", owner, "Lesson", "Videos", CancellationToken.None);

        await service.ListAsync(owner, "video", _video, "Work", owner, "Lesson", "Videos", CancellationToken.None);

        var cover = Assert.Single((await _assets.GetEntityAsync("Work", owner, CancellationToken.None)).Variants);
        Assert.False(cover.IsPreferred);
    }

    [Fact]
    public async Task ADefaultCover_IsNotMade_WhenAPictureSitsBesideTheVideo()
    {
        await File.WriteAllBytesAsync(Path.Combine(Path.GetDirectoryName(_video)!, "cover.jpg"), Picture(1));
        var owner = Guid.NewGuid();

        await Service().ListAsync(owner, "video", _video, "Work", owner, "Lesson", "Videos", CancellationToken.None);

        Assert.Empty((await _assets.GetEntityAsync("Work", owner, CancellationToken.None)).Variants);
    }

    [Fact]
    public async Task AnEpisode_GetsFrames_ButNoDefaultCover()
    {
        var owner = Guid.NewGuid();

        var frames = await Service().ListAsync(owner, "episode", _video, "Work", owner, "Pilot", "TV", CancellationToken.None);

        Assert.NotEmpty(frames.Items);
        Assert.Empty((await _assets.GetEntityAsync("Work", owner, CancellationToken.None)).Variants);
    }

    [Fact]
    public async Task ChoosingAFrame_SavesItAsAnUnchosenPicture_RecordingWhichMomentItCameFrom()
    {
        var service = Service();
        var owner = Guid.NewGuid();
        var frame = (await service.ListAsync(owner, "video", _video, "Work", owner, "Lesson", "Videos", CancellationToken.None)).Items[4];

        var saved = await service.ImportAsync(_video, 4, "Background", "Work", owner, "Lesson", "Videos", CancellationToken.None);

        Assert.NotNull(saved);
        var background = Assert.Single(
            (await _assets.GetEntityAsync("Work", owner, CancellationToken.None)).Variants, variant => variant.Role == "Background");
        Assert.False(background.IsPreferred);
        Assert.Equal(saved!.Id, background.ArtworkAssetId);
        Assert.Equal(frame.SourceKey, background.SourceUrl);
        Assert.Equal(VideoFrameSources.GeneratedProvider, background.SourceProvider);
    }

    [Fact]
    public async Task AFrame_IsNeverSavedAsALogo_OrFromAVideoThatIsGone()
    {
        var service = Service();
        var owner = Guid.NewGuid();

        Assert.Null(await service.ImportAsync(_video, 1, "Logo", "Work", owner, "Lesson", "Videos", CancellationToken.None));
        Assert.Null(await service.ImportAsync(Path.Combine(_root, "gone.mp4"), 1, "CoverArt", "Work", owner, "Lesson", "Videos", CancellationToken.None));
        Assert.Null(await service.ImportAsync(_video, 99, "CoverArt", "Work", owner, "Lesson", "Videos", CancellationToken.None));
        Assert.Empty((await _assets.GetEntityAsync("Work", owner, CancellationToken.None)).Variants);
    }

    [Fact]
    public async Task AFrameStrip_ForAMissingVideo_SaysWhy()
    {
        var owner = Guid.NewGuid();

        var frames = await Service().ListAsync(owner, "video", Path.Combine(_root, "gone.mp4"), "Work", owner, "Lesson", "Videos", CancellationToken.None);

        Assert.Empty(frames.Items);
        Assert.False(string.IsNullOrWhiteSpace(frames.Message));
    }

    [Fact]
    public async Task EachFramesPicture_IsServedFromTheEnginesFrameAddress_TheDashboardMayProxy()
    {
        var owner = Guid.NewGuid();

        var frames = await Service().ListAsync(owner, "video", _video, "Work", owner, "Lesson", "Videos", CancellationToken.None);

        Assert.All(frames.Items, frame => Assert.StartsWith($"/metadata/{owner:D}/video-frames/video/", frame.ThumbnailUrl, StringComparison.Ordinal));
    }

    private VideoFrameArtworkService Service()
    {
        var ffmpeg = new FakeFfmpeg();
        var candidates = new VideoFrameCandidateService(new VideoFrameExtractor(ffmpeg), ffmpeg, Path.Combine(_root, "strip"));
        return new VideoFrameArtworkService(candidates, _assets);
    }

    private static byte[] Picture(int seed)
    {
        using var bitmap = new SKBitmap(320, 180);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.DarkSlateBlue);
        using var paint = new SKPaint { IsAntialias = true };
        var random = new Random(seed);
        for (var i = 0; i < 40 + seed; i++)
        {
            paint.Color = new SKColor((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256));
            canvas.DrawRect(random.Next(300), random.Next(160), 20, 20, paint);
        }

        using var data = SKImage.FromBitmap(bitmap).Encode(SKEncodedImageFormat.Jpeg, 90);
        return data.ToArray();
    }

    private sealed class NoHttpFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new NotSupportedException();
    }

    // Writes a picture where the real FFmpeg would: the last argument is the frame's file and "-ss" says which moment it is.
    private sealed class FakeFfmpeg : IFFmpegService
    {
        public string? FfmpegPath => "fake";

        public string? FfprobePath => null;

        public bool IsAvailable => true;

        public HardwareCapabilities HardwareCapabilities => throw new NotSupportedException();

        public Task<MediaProbeResult?> ProbeAsync(string filePath, CancellationToken ct = default) =>
            Task.FromResult<MediaProbeResult?>(new MediaProbeResult { Duration = TimeSpan.FromSeconds(90) });

        public Task<(int ExitCode, string Output, string Error)> RunAsync(string arguments, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public async Task<(int ExitCode, string Output, string Error)> RunAsync(IReadOnlyList<string> arguments, CancellationToken ct = default)
        {
            var at = double.Parse(arguments[arguments.ToList().IndexOf("-ss") + 1], CultureInfo.InvariantCulture);
            await File.WriteAllBytesAsync(arguments[^1], Picture((int)Math.Round(at)), ct);
            return (0, string.Empty, string.Empty);
        }
    }
}
