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

public sealed class PlaybackPathPackagingTests
{
    [Fact]
    public async Task Repackage_CopiesThePictureAndTheSound_AndNeverStartsAnEncoder()
    {
        using var world = await World.CreateAsync("auto", new HardwareCapabilities { HasNvenc = true, PreferredEncoder = "h264_nvenc" });
        var decision = new PlaybackDecision(PlaybackRungs.Repackage, "MKV is not a Web container", true, ["aac", "mp3"]);

        var result = await world.RunAsync(new HlsStartPlan(decision));

        Assert.Equal("ready", result.Status);
        var videoCall = Assert.Single(world.Ffmpeg.Calls, call => call.Contains("0:v:0"));
        Assert.Equal("copy", videoCall[videoCall.IndexOf("-c:v") + 1]);
        var audioCall = Assert.Single(world.Ffmpeg.Calls, call => call.Contains("0:a:0"));
        Assert.Equal("copy", audioCall[audioCall.IndexOf("-c:a") + 1]);
        Assert.DoesNotContain(world.Ffmpeg.Calls, call => call.Contains("libx264") || call.Contains("h264_nvenc"));
    }

    [Fact]
    public async Task ConvertSound_CopiesThePictureAndConvertsOnlyTheSound()
    {
        using var world = await World.CreateAsync("cpu", new HardwareCapabilities(), audioCodec: "dts");
        var decision = new PlaybackDecision(PlaybackRungs.ConvertSound, "DTS sound cannot be played", true, ["aac", "mp3"]);

        await world.RunAsync(new HlsStartPlan(decision));

        var videoCall = Assert.Single(world.Ffmpeg.Calls, call => call.Contains("0:v:0"));
        Assert.Equal("copy", videoCall[videoCall.IndexOf("-c:v") + 1]);
        var audioCall = Assert.Single(world.Ffmpeg.Calls, call => call.Contains("0:a:0"));
        Assert.Equal("aac", audioCall[audioCall.IndexOf("-c:a") + 1]);
    }

    [Fact]
    public async Task AGraphicsEncoderThatFailsToStart_RestartsAtOnceOnTheProcessor_WithAReason()
    {
        using var world = await World.CreateAsync("auto",
            new HardwareCapabilities { HasNvenc = true, PreferredEncoder = "h264_nvenc" }, failEncoder: "h264_nvenc");
        var decision = new PlaybackDecision(PlaybackRungs.EncodePicture, "HEVC cannot be played", false, ["aac"]);

        var result = await world.RunAsync(new HlsStartPlan(decision, 1080));

        Assert.Equal("ready", result.Status);
        var encoders = world.Ffmpeg.Calls.Where(call => call.Contains("0:v:0")).Select(call => call[call.IndexOf("-c:v") + 1]).ToArray();
        Assert.Equal(["h264_nvenc", "libx264"], encoders);
    }

    private sealed class World : IDisposable
    {
        private readonly string _root;
        private readonly DatabaseConnection _db;
        private readonly ConfigurationDirectoryLoader _config;
        private readonly Guid _asset;
        private string _audioCodec = "aac";
        public FakeFfmpeg Ffmpeg { get; }
        public AdaptiveHlsService Service { get; }

        private World(string root, DatabaseConnection db, ConfigurationDirectoryLoader config, Guid asset, FakeFfmpeg ffmpeg, AdaptiveHlsService service)
        {
            _root = root; _db = db; _config = config; _asset = asset; Ffmpeg = ffmpeg; Service = service;
        }

        public static async Task<World> CreateAsync(string acceleration, HardwareCapabilities hardware, string audioCodec = "aac", string? failEncoder = null)
        {
            var root = Path.Combine(Path.GetTempPath(), "playback-path-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var db = new DatabaseConnection(Path.Combine(root, "test.db"));
            db.InitializeSchema(); db.RunStartupChecks();
            var config = new ConfigurationDirectoryLoader(Path.Combine(root, "config"));
            config.SaveCore(new CoreConfiguration { LibraryRoot = root });
            config.SaveTranscoding(new TranscodingSettings { HardwareAcceleration = acceleration, VariantCachePath = "variants" });
            var asset = Guid.NewGuid(); var edition = Guid.NewGuid(); var work = Guid.NewGuid();
            var source = Path.Combine(root, "source.mkv"); await File.WriteAllTextAsync(source, "test source");
            using (var conn = db.CreateConnection())
            {
                conn.Execute("""
                    INSERT INTO works(id,media_type) VALUES(@work,'Movies');
                    INSERT INTO editions(id,work_id) VALUES(@edition,@work);
                    INSERT INTO media_assets(id,edition_id,content_hash,file_path_root,status) VALUES(@asset,@edition,'hash',@source,'Normal');
                    """, new { asset, edition, work, source });
            }
            var inspection = new PlaybackStateRepository(db);
            await inspection.StoreInspectionAsync(asset, "hash", 100, 20, "mkv", System.Text.Json.JsonSerializer.Serialize(
                new MediaProbeResult { Height = 1080, Duration = TimeSpan.FromSeconds(20), VideoCodec = "h264", AudioCodec = audioCodec }));
            var ffmpeg = new FakeFfmpeg(hardware, failEncoder);
            var service = new AdaptiveHlsService(new AdaptiveHlsPackageRepository(db), new MediaAssetRepository(db), new TextTrackRepository(db),
                ffmpeg, config, new Lifetime(), NullLogger<AdaptiveHlsService>.Instance, inspection);
            return new World(root, db, config, asset, ffmpeg, service) { _audioCodec = audioCodec };
        }

        public async Task<AdaptiveHlsPreparation> RunAsync(HlsStartPlan plan)
        {
            var tracks = new[] { new PlaybackTrackDto { Index = 0, Codec = _audioCodec, DisplayName = "Audio", IsDefault = true } };
            var result = await Service.EnsurePackageAsync(_asset, "hash", tracks, default, plan);
            for (var attempt = 0; attempt < 80 && result.Status != "ready" && result.Status != "failed"; attempt++)
            {
                await Task.Delay(250);
                result = await Service.EnsurePackageAsync(_asset, "hash", tracks, default, plan);
            }
            return result;
        }

        public void Dispose()
        {
            _config.Dispose(); _db.Dispose();
            try { Directory.Delete(_root, true); } catch (IOException) { /* SQLite pooling can retain the temporary database. */ }
        }
    }

    private sealed class FakeFfmpeg(HardwareCapabilities hardware, string? failEncoder) : IFFmpegService
    {
        private readonly object _gate = new();
        private readonly List<string[]> _calls = [];
        public IReadOnlyList<string[]> Calls { get { lock (_gate) { return _calls.ToArray(); } } }
        public string? FfmpegPath => "fake";
        public string? FfprobePath => "fake";
        public bool IsAvailable => true;
        public HardwareCapabilities HardwareCapabilities { get; } = hardware with { HasHlsMuxer = true, HasH264Encoder = true, HasAacEncoder = true };
        public Task<MediaProbeResult?> ProbeAsync(string path, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<(int ExitCode, string Output, string Error)> RunAsync(string args, CancellationToken ct = default) => throw new NotSupportedException();
        public async Task<(int ExitCode, string Output, string Error)> RunAsync(IReadOnlyList<string> args, CancellationToken ct = default)
        {
            var copy = args.ToArray();
            lock (_gate) { _calls.Add(copy); }
            var playlist = copy[^1];
            if (playlist.EndsWith(".vtt", StringComparison.OrdinalIgnoreCase))
            {
                await File.WriteAllTextAsync(playlist, "WEBVTT\n", ct);
                return (0, "", "");
            }
            var codecIndex = Array.IndexOf(copy, "-c:v");
            if (failEncoder is not null && codecIndex >= 0 && copy[codecIndex + 1] == failEncoder)
            {
                return (1, "", "Driver does not support the required nvenc API version.");
            }
            await File.WriteAllTextAsync(Path.Combine(Path.GetDirectoryName(playlist)!, "segment_00000.ts"), "segment", ct);
            await File.WriteAllTextAsync(playlist, "#EXTM3U\n#EXT-X-TARGETDURATION:4\n#EXTINF:4,\nsegment_00000.ts\n#EXT-X-ENDLIST\n", ct);
            return (0, "", "");
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
