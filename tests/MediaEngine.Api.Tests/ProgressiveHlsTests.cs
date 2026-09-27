using Dapper;
using MediaEngine.Api.Services.Playback;
using MediaEngine.Contracts.Playback;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Models;
using MediaEngine.Storage;
using MediaEngine.Storage.Playback;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Api.Tests;

public sealed class ProgressiveHlsTests
{
    [Fact]
    public async Task FirstSegmentIsPlayableBeforeFullEncodeAndConcurrentRequestsSharePreparation()
    {
        var root = Path.Combine(Path.GetTempPath(), "progressive-hls-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var db = new DatabaseConnection(Path.Combine(root,"test.db"));
        db.InitializeSchema(); db.RunStartupChecks();
        using var config = new ConfigurationDirectoryLoader(Path.Combine(root,"config"));
        config.SaveCore(new CoreConfiguration { LibraryRoot = root });
        config.SaveTranscoding(new TranscodingSettings { HardwareAcceleration = "cpu", VariantCachePath="variants" });
        var asset = Guid.NewGuid(); var edition = Guid.NewGuid(); var work = Guid.NewGuid();
        var source = Path.Combine(root,"source.mp4"); await File.WriteAllTextAsync(source,"test source");
        using (var conn = db.CreateConnection()) conn.Execute("""
            INSERT INTO works(id,media_type) VALUES(@work,'Movies');
            INSERT INTO editions(id,work_id) VALUES(@edition,@work);
            INSERT INTO media_assets(id,edition_id,content_hash,file_path_root,status) VALUES(@asset,@edition,'hash',@source,'Normal');
            """,new {asset,edition,work,source});
        var ffmpeg = new SegmentFfmpeg();
        var packages = new AdaptiveHlsPackageRepository(db);
        var inspection = new PlaybackStateRepository(db);
        await inspection.StoreInspectionAsync(asset, "hash", 100, 120, "mp4", System.Text.Json.JsonSerializer.Serialize(new MediaProbeResult { Height = 360, Duration = TimeSpan.FromSeconds(120), SubtitleLanguages = ["en"] }));
        var service = new AdaptiveHlsService(packages,new MediaAssetRepository(db),new TextTrackRepository(db),ffmpeg,config,new Lifetime(),NullLogger<AdaptiveHlsService>.Instance,inspection);
        var first = await service.EnsurePackageAsync(asset,"hash",[]);
        try
        {
            Assert.Equal("streaming",first.Status);
            Assert.False(ffmpeg.Completion.Task.IsCompleted);
            var second = await service.EnsurePackageAsync(asset,"hash",[]);
            Assert.Equal(first.PackageId,second.PackageId);
            Assert.Equal(2,ffmpeg.Calls);
            await using var resource = await service.OpenResourceAsync(first.PackageId,asset,"v0/segment_00000.ts");
            Assert.NotNull(resource);
            Assert.Equal("preparing",(await packages.FindByIdAsync(first.PackageId))!.Status);
        }
        finally
        {
            ffmpeg.Completion.TrySetResult();
            for (var i=0;i<100 && service.IsActive(first.PackageId);i++) await Task.Delay(20);
        }
        Assert.Equal("ready",(await packages.FindByIdAsync(first.PackageId))!.Status);
        await using (var captions = await service.OpenResourceAsync(first.PackageId,asset,"captions.json"))
        {
            Assert.NotNull(captions);
        }
        config.Dispose(); db.Dispose();
        try { Directory.Delete(root,true); } catch (IOException) { /* SQLite pooling can retain the temporary database. */ }
    }

    private sealed class SegmentFfmpeg : IFFmpegService
    {
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls;
        public string? FfmpegPath => "fake";
        public string? FfprobePath => "fake";
        public bool IsAvailable => true;
        public HardwareCapabilities HardwareCapabilities { get; } = new() { HasHlsMuxer=true,HasH264Encoder=true,HasAacEncoder=true };
        public Task<MediaProbeResult?> ProbeAsync(string path,CancellationToken ct=default) => throw new InvalidOperationException("Playback must use ingested inspection facts.");
        public Task<(int ExitCode,string Output,string Error)> RunAsync(string args,CancellationToken ct=default) => throw new NotSupportedException();
        public async Task<(int ExitCode,string Output,string Error)> RunAsync(IReadOnlyList<string> args,CancellationToken ct=default)
        {
            Interlocked.Increment(ref Calls);
            var playlist=args[^1];
            if (playlist.EndsWith(".vtt",StringComparison.OrdinalIgnoreCase))
            {
                await Completion.Task.WaitAsync(ct);
                await File.WriteAllTextAsync(playlist,"WEBVTT\n\n00:00:01.000 --> 00:00:02.000\nCaption\n",ct);
                return (0,"","");
            }
            await File.WriteAllTextAsync(Path.Combine(Path.GetDirectoryName(playlist)!,"segment_00000.ts"),"complete segment",ct);
            await File.WriteAllTextAsync(playlist,"#EXTM3U\n#EXT-X-TARGETDURATION:4\n#EXTINF:4,\nsegment_00000.ts\n",ct);
            await Completion.Task.WaitAsync(ct);
            return (0,"","");
        }
    }
    private sealed class Lifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }
}
