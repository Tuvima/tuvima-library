using Dapper;
using MediaEngine.Api.Services.Playback;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Models;
using MediaEngine.Storage;
using MediaEngine.Storage.Playback;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Api.Tests;

public sealed class AdaptiveHlsCleanupServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"tuvima_hls_cleanup_{Guid.NewGuid():N}");
    private readonly DatabaseConnection _database;
    private readonly ConfigurationDirectoryLoader _configuration;

    public AdaptiveHlsCleanupServiceTests()
    {
        Directory.CreateDirectory(_root);
        _database = new DatabaseConnection(Path.Combine(_root, "library.db"));
        _database.InitializeSchema();
        _database.RunStartupChecks();
        _configuration = new ConfigurationDirectoryLoader(Path.Combine(_root, "config"));
        _configuration.SaveCore(new CoreConfiguration { LibraryRoot = _root });
        _configuration.SaveTranscoding(new TranscodingSettings
        {
            VariantCachePath = "variants",
            VariantRetentionDays = 1,
            ShadowStorageLimitGb = 1,
            CleanupLruEnabled = true,
        });
    }

    [Fact]
    public async Task CleanupAsync_ReclaimsExpiredPackageAndDatabaseRow()
    {
        var assetId = SeedAsset();
        var packages = new AdaptiveHlsPackageRepository(_database);
        var packageRoot = Path.Combine(_root, "variants", "hls", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(packageRoot);
        await File.WriteAllTextAsync(Path.Combine(packageRoot, "master.m3u8"), "#EXTM3U");
        var package = await packages.GetOrCreateAsync(assetId, "source-hash", "test-profile", packageRoot);
        await packages.MarkReadyAsync(package.Id, packageRoot, 8);
        using (var connection = _database.CreateConnection())
        {
            connection.Execute(
                "UPDATE adaptive_hls_packages SET last_accessed = @lastAccessed WHERE id = @id;",
                new { id = package.Id, lastAccessed = DateTimeOffset.UtcNow.AddDays(-2).ToString("O") });
        }

        var cleanup = CreateCleanup(packages);

        await cleanup.CleanupAsync();

        Assert.False(Directory.Exists(packageRoot));
        Assert.Null(await packages.FindByIdAsync(package.Id));
    }

    private AdaptiveHlsCleanupService CreateCleanup(AdaptiveHlsPackageRepository packages)
    {
        var hls = new AdaptiveHlsService(
            packages,
            new MediaAssetRepository(_database),
            new TextTrackRepository(_database),
            new UnusedFfmpegService(),
            _configuration,
            new TestHostApplicationLifetime(),
            NullLogger<AdaptiveHlsService>.Instance);
        return new AdaptiveHlsCleanupService(
            packages,
            new PlaybackStateRepository(_database),
            hls,
            _configuration,
            NullLogger<AdaptiveHlsCleanupService>.Instance);
    }

    [Fact]
    public async Task CleanupAsync_RemovesExpiredOfflineVariantsWithTheirFiles_AndKeepsCurrentOnes()
    {
        var assetId = SeedAsset();
        var variantDirectory = Path.Combine(_root, "variants", assetId.ToString("N"), "mobile-small");
        Directory.CreateDirectory(variantDirectory);
        var expiredFile = Path.Combine(variantDirectory, "expired.mp4");
        var currentFile = Path.Combine(variantDirectory, "current.mp4");
        var foreverFile = Path.Combine(variantDirectory, "forever.mp4");
        foreach (var file in new[] { expiredFile, currentFile, foreverFile })
        {
            await File.WriteAllTextAsync(file, "media");
        }

        var expired = SeedVariant(assetId, expiredFile, DateTimeOffset.UtcNow.AddMinutes(-1));
        var current = SeedVariant(assetId, currentFile, DateTimeOffset.UtcNow.AddDays(1));
        var forever = SeedVariant(assetId, foreverFile, null);

        await CreateCleanup(new AdaptiveHlsPackageRepository(_database)).CleanupAsync();

        Assert.False(File.Exists(expiredFile));
        Assert.True(File.Exists(currentFile));
        Assert.True(File.Exists(foreverFile));
        Assert.Equal(new[] { current, forever }.Order(), VariantIds().Order());
        Assert.DoesNotContain(expired, VariantIds());
    }

    [Fact]
    public async Task CleanupAsync_RemovesTheLastFileAndItsEmptyFolders_ButNeverTheCacheRoot()
    {
        var assetId = SeedAsset();
        var variantDirectory = Path.Combine(_root, "variants", assetId.ToString("N"), "mobile-small");
        Directory.CreateDirectory(variantDirectory);
        var file = Path.Combine(variantDirectory, "only.mp4");
        await File.WriteAllTextAsync(file, "media");
        SeedVariant(assetId, file, DateTimeOffset.UtcNow.AddSeconds(-5));

        await CreateCleanup(new AdaptiveHlsPackageRepository(_database)).CleanupAsync();

        Assert.False(Directory.Exists(variantDirectory));
        Assert.False(Directory.Exists(Path.GetDirectoryName(variantDirectory)));
        Assert.True(Directory.Exists(Path.Combine(_root, "variants")));
        Assert.Empty(VariantIds());
    }

    [Fact]
    public async Task CleanupAsync_NeverDeletesAFileOutsideTheVariantCache_ButDropsTheStaleRow()
    {
        var assetId = SeedAsset();
        var outside = Path.Combine(_root, "not-the-cache.mp4");
        await File.WriteAllTextAsync(outside, "precious");
        SeedVariant(assetId, outside, DateTimeOffset.UtcNow.AddMinutes(-1));

        await CreateCleanup(new AdaptiveHlsPackageRepository(_database)).CleanupAsync();

        Assert.True(File.Exists(outside));
        Assert.Empty(VariantIds());
    }

    private Guid SeedVariant(Guid assetId, string outputPath, DateTimeOffset? expiresAt)
    {
        var id = Guid.NewGuid();
        using var connection = _database.CreateConnection();
        connection.Execute(
            """
            INSERT INTO offline_variants
                (id, asset_id, profile_key, source_hash, display_name, status, output_path, file_size, created_at, expires_at)
            VALUES (@id, @assetId, 'mobile-small', 'source-hash', 'Phone', 'ready', @outputPath, 5, @now, @expires);
            """,
            new { id, assetId, outputPath, now = DateTimeOffset.UtcNow.ToString("O"), expires = expiresAt?.ToString("O") });
        return id;
    }

    private List<Guid> VariantIds()
    {
        using var connection = _database.CreateConnection();
        return connection.Query<Guid>("SELECT id FROM offline_variants;").AsList();
    }

    private Guid SeedAsset()
    {
        var workId = Guid.NewGuid();
        var editionId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        using var connection = _database.CreateConnection();
        connection.Execute("INSERT INTO works (id, media_type) VALUES (@workId, 'Movies');", new { workId });
        connection.Execute("INSERT INTO editions (id, work_id) VALUES (@editionId, @workId);", new { editionId, workId });
        connection.Execute("""
            INSERT INTO media_assets (id, edition_id, content_hash, file_path_root, status)
            VALUES (@assetId, @editionId, 'source-hash', @path, 'Normal');
            """, new { assetId, editionId, path = Path.Combine(_root, "source.mp4") });
        return assetId;
    }

    public void Dispose()
    {
        _configuration.Dispose();
        _database.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private sealed class UnusedFfmpegService : IFFmpegService
    {
        public string? FfmpegPath => null;
        public string? FfprobePath => null;
        public bool IsAvailable => false;
        public HardwareCapabilities HardwareCapabilities { get; } = new();
        public Task<MediaProbeResult?> ProbeAsync(string filePath, CancellationToken ct = default) =>
            Task.FromResult<MediaProbeResult?>(null);
        public Task<(int ExitCode, string Output, string Error)> RunAsync(string arguments, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class TestHostApplicationLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }
}
