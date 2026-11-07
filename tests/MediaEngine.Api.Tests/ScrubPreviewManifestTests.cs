using System.Text.Json;
using Dapper;
using MediaEngine.Api.Services.Playback;
using MediaEngine.Contracts.Playback;
using MediaEngine.Domain.Models;
using MediaEngine.Storage;
using MediaEngine.Storage.Playback;
using MediaEngine.Storage.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Api.Tests;

/// <summary>The playback item carries the preview index address only when the file really has previews to show.</summary>
public sealed class ScrubPreviewManifestTests : IDisposable
{
    private readonly ScrubPreviewWorld _world = new(new ScrubPreviewWorld.MissingFfmpeg());
    private readonly PlaybackStateRepository _state;
    private readonly PlaybackCapabilitiesService _playback;

    public ScrubPreviewManifestTests()
    {
        var protection = new EphemeralDataProtectionProvider();
        var accounts = new AccountRepository(_world.Database);
        var applications = new ApplicationRepository(_world.Database);
        var tracks = new TextTrackRepository(_world.Database);
        var hls = new AdaptiveHlsService(
            new AdaptiveHlsPackageRepository(_world.Database), _world.Assets, tracks, _world.Ffmpeg, _world.Configuration,
            new Lifetime(), NullLogger<AdaptiveHlsService>.Instance);
        _state = new PlaybackStateRepository(_world.Database);
        _playback = new PlaybackCapabilitiesService(
            _world.Assets, _world.Ffmpeg, _world.Database, _state, new PlaybackSegmentRepository(_world.Database), tracks,
            new AudiobookChapterTitleOverrideRepository(_world.Database), new MediaTypeExtensionCatalog(), _world.Configuration, hls,
            new HlsAccessGrantService(protection, _world.Configuration, accounts, applications, new IdentityRepository(_world.Database),
                new ClientAuthorizationRepository(_world.Database), new MediaEngine.Domain.Authorization.PermissionRegistry()),
            new HttpContextAccessor(), NullLogger<PlaybackCapabilitiesService>.Instance, _world.Store);
    }

    public void Dispose() => _world.Dispose();

    private async Task<Guid> SeedAsync(string fileName, string videoCodec = "h264")
    {
        var path = Path.Combine(_world.Root, fileName);
        await File.WriteAllBytesAsync(path, [0, 1, 2, 3]);
        var assetId = _world.AddAssetRow(path);
        string hash;
        using (var connection = _world.Database.CreateConnection())
        {
            hash = connection.ExecuteScalar<string>("SELECT content_hash FROM media_assets WHERE id = @assetId;", new { assetId })!;
        }

        await _state.StoreInspectionAsync(assetId, hash, 4, 600, Path.GetExtension(fileName).TrimStart('.'),
            JsonSerializer.Serialize(new MediaProbeResult { Duration = TimeSpan.FromSeconds(600), VideoCodec = videoCodec, AudioCodec = "aac", Width = 1280, Height = 720 }));
        return assetId;
    }

    private void MakePreviews(Guid assetId)
    {
        var staging = _world.Store.NewStagingFolder(assetId);
        Directory.CreateDirectory(staging);
        File.WriteAllBytes(Path.Combine(staging, "sheet-000.jpg"), new byte[10]);
        _world.Store.Publish(assetId, staging, new ScrubPreviewIndexDto { IntervalSeconds = 10, TileWidth = 320, TileHeight = 180, Columns = 10, Rows = 10, FrameCount = 60, Sheets = ["sheet-000.jpg"] });
    }

    [Fact]
    public async Task AVideoWithPreviews_CarriesTheAddressOfItsIndex()
    {
        var assetId = await SeedAsync("film.mp4");
        MakePreviews(assetId);

        var manifest = await _playback.BuildManifestAsync(assetId, "web");

        Assert.Equal($"/stream/{assetId:D}/scrub/index.json", manifest!.ScrubPreviewIndexUrl);
    }

    [Fact]
    public async Task AVideoWithoutPreviews_OrWithTheSwitchOff_CarriesNoAddress()
    {
        var bare = await SeedAsync("bare.mp4");
        var switchedOff = await SeedAsync("off.mp4");
        MakePreviews(switchedOff);

        Assert.Null((await _playback.BuildManifestAsync(bare, "web"))!.ScrubPreviewIndexUrl);
        _world.Save(settings => settings.ScrubPreviews.Enabled = false);
        Assert.Null((await _playback.BuildManifestAsync(switchedOff, "web"))!.ScrubPreviewIndexUrl);
    }

    [Fact]
    public async Task AudioNeverCarriesAPreviewAddress()
    {
        var assetId = await SeedAsync("book.m4b", videoCodec: "");
        MakePreviews(assetId);

        Assert.Null((await _playback.BuildManifestAsync(assetId, "web"))!.ScrubPreviewIndexUrl);
    }

    private sealed class Lifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }
}
