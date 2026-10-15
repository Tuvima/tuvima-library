using Dapper;
using MediaEngine.Api.Services.Playback;
using MediaEngine.Contracts.Playback;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Models;
using MediaEngine.Storage;
using MediaEngine.Storage.Playback;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Api.Tests;

/// <summary>An encode that fails or is interrupted leaves no folder behind; a good one keeps its own.</summary>
public sealed class EncodeQueueServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"tuvima_encode_queue_{Guid.NewGuid():N}");
    private readonly DatabaseConnection _database;
    private readonly ConfigurationDirectoryLoader _configuration;
    private readonly PlaybackStateRepository _state;
    private readonly Guid _assetId;

    public EncodeQueueServiceTests()
    {
        Directory.CreateDirectory(_root);
        DapperConfiguration.Configure();
        _database = new DatabaseConnection(Path.Combine(_root, "library.db"));
        _database.InitializeSchema();
        _database.RunStartupChecks();
        _configuration = new ConfigurationDirectoryLoader(Path.Combine(_root, "config"));
        _configuration.SaveCore(new CoreConfiguration { LibraryRoot = _root });
        _configuration.SaveTranscoding(new TranscodingSettings { VariantCachePath = "variants", VariantRetentionDays = 30 });
        _state = new PlaybackStateRepository(_database);
        _assetId = SeedAsset();
    }

    [Fact]
    public async Task AFailedEncode_RemovesItsOwnFolder_AndNothingElse()
    {
        var neighbour = Path.Combine(_root, "variants", _assetId.ToString("N"), "mobile-small", "keep-me");
        Directory.CreateDirectory(neighbour);
        var ffmpeg = new FakeFfmpeg((output, _) =>
        {
            File.WriteAllText(output, "half written");
            return (1, "", "boom");
        });
        await _state.QueueEncodeJobAsync(_assetId, "mobile-small", "source-hash", null);

        await CreateService(ffmpeg).ProcessNextJobAsync(default);

        var job = Assert.Single(await _state.ListEncodeJobsAsync());
        Assert.Equal(EncodeJobStatuses.Failed, job.Status);
        Assert.NotNull(ffmpeg.LastOutput);
        Assert.False(Directory.Exists(Path.GetDirectoryName(ffmpeg.LastOutput)));
        Assert.True(Directory.Exists(neighbour));
        Assert.True(Directory.Exists(Path.Combine(_root, "variants")));
    }

    [Fact]
    public async Task AnInterruptedEncode_RemovesItsOwnFolder_AndTheInterruptionStillPropagates()
    {
        var ffmpeg = new FakeFfmpeg((output, _) =>
        {
            File.WriteAllText(output, "half written");
            throw new OperationCanceledException();
        });
        await _state.QueueEncodeJobAsync(_assetId, "mobile-small", "source-hash", null);

        await Assert.ThrowsAsync<OperationCanceledException>(() => CreateService(ffmpeg).ProcessNextJobAsync(default));

        Assert.False(Directory.Exists(Path.GetDirectoryName(ffmpeg.LastOutput)));
    }

    [Fact]
    public async Task ASuccessfulEncode_KeepsItsFolder_AndStampsTheVariantExpiry()
    {
        var ffmpeg = new FakeFfmpeg((output, _) =>
        {
            File.WriteAllText(output, "media");
            return (0, "", "");
        });
        await _state.QueueEncodeJobAsync(_assetId, "mobile-small", "source-hash", null);

        await CreateService(ffmpeg).ProcessNextJobAsync(default);

        Assert.True(File.Exists(ffmpeg.LastOutput));
        using var connection = _database.CreateConnection();
        Assert.NotNull(connection.ExecuteScalar<string?>("SELECT expires_at FROM offline_variants;"));
    }

    private EncodeQueueService CreateService(FakeFfmpeg ffmpeg) =>
        new(_state, new MediaAssetRepository(_database), ffmpeg, _configuration, NullLogger<EncodeQueueService>.Instance);

    private Guid SeedAsset()
    {
        var workId = Guid.NewGuid();
        var editionId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        var source = Path.Combine(_root, "source.mp4");
        File.WriteAllText(source, "source");
        using var connection = _database.CreateConnection();
        connection.Execute("INSERT INTO works (id, media_type) VALUES (@workId, 'Movies');", new { workId });
        connection.Execute("INSERT INTO editions (id, work_id) VALUES (@editionId, @workId);", new { editionId, workId });
        connection.Execute("""
            INSERT INTO media_assets (id, edition_id, content_hash, file_path_root, status)
            VALUES (@assetId, @editionId, 'source-hash', @source, 'Normal');
            """, new { assetId, editionId, source });
        return assetId;
    }

    public void Dispose()
    {
        _configuration.Dispose();
        _database.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { /* the temp folder is disposable */ }
    }

    /// <summary>A stand-in for ffmpeg: it is handed the output path (the last quoted argument) and decides what happens.</summary>
    private sealed class FakeFfmpeg(Func<string, string, (int ExitCode, string Output, string Error)> run) : IFFmpegService
    {
        public string? LastOutput { get; private set; }
        public string? FfmpegPath => "ffmpeg";
        public string? FfprobePath => "ffprobe";
        public bool IsAvailable => true;
        public HardwareCapabilities HardwareCapabilities { get; } = new();
        public Task<MediaProbeResult?> ProbeAsync(string filePath, CancellationToken ct = default) =>
            Task.FromResult<MediaProbeResult?>(null);

        public Task<(int ExitCode, string Output, string Error)> RunAsync(string arguments, CancellationToken ct = default)
        {
            var end = arguments.LastIndexOf('"');
            var start = arguments.LastIndexOf('"', end - 1);
            LastOutput = arguments[(start + 1)..end];
            return Task.FromResult(run(LastOutput, arguments));
        }
    }
}
