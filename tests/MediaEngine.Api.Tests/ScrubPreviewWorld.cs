using Dapper;
using MediaEngine.Api.Services.Playback;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Models;
using MediaEngine.Domain.Services;
using MediaEngine.Processors;
using MediaEngine.Storage;
using MediaEngine.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Api.Tests;

/// <summary>
/// A real data store, a real config folder and a real preview cache in a private temp folder, with videos registered the way the
/// library registers them. Only FFmpeg is swappable (the real one, or a stand-in that is missing).
/// </summary>
internal sealed class ScrubPreviewWorld : IDisposable
{
    public ScrubPreviewWorld(IFFmpegService? ffmpeg = null)
    {
        DapperConfiguration.Configure();
        Root = Path.Combine(Path.GetTempPath(), $"tuvima_scrub_{Guid.NewGuid():N}");
        Directory.CreateDirectory(Root);
        Database = new DatabaseConnection(Path.Combine(Root, "test.db"));
        Database.InitializeSchema();
        Database.RunStartupChecks();
        Configuration = new ConfigurationDirectoryLoader(Path.Combine(Root, "config"));
        // Small, fast previews for the tests: a frame every 2 seconds, 80 px wide, 3 x 2 per sheet.
        Save(settings =>
        {
            settings.ScrubPreviews.IntervalSeconds = 2;
            settings.ScrubPreviews.TileWidth = 80;
            settings.ScrubPreviews.Columns = 3;
            settings.ScrubPreviews.Rows = 2;
        });
        AssetPaths = new AssetPathService(Path.Combine(Root, "library"), dataRoot: Path.Combine(Root, "data"));
        Store = new ScrubPreviewCache(AssetPaths);
        Ffmpeg = ffmpeg ?? (IFFmpegService?)RealFfmpeg.Find() ?? new MissingFfmpeg();
        Assets = new MediaAssetRepository(Database);
        Operations = new MediaOperationRepository(Database);
        Generator = new ScrubPreviewGenerator(
            Assets, Ffmpeg, new VideoFrameExtractor(Ffmpeg), Store, Configuration, NullLogger<ScrubPreviewGenerator>.Instance);
        Queue = new ScrubPreviewQueue(Operations, Store);
    }

    public string Root { get; }
    public DatabaseConnection Database { get; }
    public ConfigurationDirectoryLoader Configuration { get; }
    public AssetPathService AssetPaths { get; }
    public ScrubPreviewCache Store { get; }
    public IFFmpegService Ffmpeg { get; }
    public MediaAssetRepository Assets { get; }
    public MediaOperationRepository Operations { get; }
    public ScrubPreviewGenerator Generator { get; }
    public ScrubPreviewQueue Queue { get; }

    public void Save(Action<TranscodingSettings> change)
    {
        var settings = Configuration.LoadTranscoding();
        change(settings);
        Configuration.SaveTranscoding(settings);
    }

    /// <summary>Registers a video file as an identified, visible asset and returns its id.</summary>
    public async Task<Guid> AddVideoAsync(string fileName, int seconds, DateTimeOffset? presentedAt = null, bool makeFile = true, string? libraryId = null)
    {
        var path = Path.Combine(Root, "media", fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (makeFile)
        {
            if (Ffmpeg is RealFfmpeg real && !fileName.EndsWith(".m4a", StringComparison.OrdinalIgnoreCase))
            {
                await real.MakeSampleVideoAsync(path, seconds);
            }
            else
            {
                await File.WriteAllTextAsync(path, "not a real video");
            }
        }

        return AddAssetRow(path, presentedAt, libraryId);
    }

    public Guid AddAssetRow(string path, DateTimeOffset? presentedAt = null, string? libraryId = null)
    {
        var workId = Guid.NewGuid();
        var editionId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        using var connection = Database.CreateConnection();
        connection.Execute(
            """
            INSERT INTO works (id, media_type, work_kind, curator_state) VALUES (@workId, 'Movies', 'standalone', 'accepted');
            INSERT INTO editions (id, work_id) VALUES (@editionId, @workId);
            INSERT INTO media_assets (id, edition_id, content_hash, file_path_root, presented_at, library_id)
            VALUES (@assetId, @editionId, @hash, @path, @presentedAt, @libraryId);
            """,
            new
            {
                workId,
                editionId,
                assetId,
                hash = Guid.NewGuid().ToString("N"),
                path,
                presentedAt = (presentedAt ?? DateTimeOffset.UtcNow).ToString("O"),
                libraryId,
            });
        return assetId;
    }

    public void Dispose()
    {
        TestTemp.ReleaseSqlitePool(Path.Combine(Root, "test.db"));
        try { Database.Dispose(); } catch (ObjectDisposedException) { /* Already closed by the pool sweep. */ }
        Configuration.Dispose();
        try { Directory.Delete(Root, recursive: true); } catch (IOException) { /* The OS temp sweep removes a handle SQLite releases late. */ }
    }

    /// <summary>A machine with no FFmpeg at all.</summary>
    internal sealed class MissingFfmpeg : IFFmpegService
    {
        public string? FfmpegPath => null;
        public string? FfprobePath => null;
        public bool IsAvailable => false;
        public HardwareCapabilities HardwareCapabilities { get; } = new();
        public Task<MediaProbeResult?> ProbeAsync(string filePath, CancellationToken ct = default) => Task.FromResult<MediaProbeResult?>(null);
        public Task<(int ExitCode, string Output, string Error)> RunAsync(string arguments, CancellationToken ct = default) =>
            throw new InvalidOperationException("FFmpeg is not available.");
    }
}
