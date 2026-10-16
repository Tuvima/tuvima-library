using System.Text.Json;
using Dapper;
using MediaEngine.Api.Services.Playback;
using MediaEngine.Contracts.Playback;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Models;
using MediaEngine.Storage;
using MediaEngine.Storage.Playback;
using MediaEngine.Storage.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Api.Tests;

/// <summary>What a phone is told when it starts a cast session, from a real temp data store and stored inspection facts.</summary>
public sealed class CastSessionServiceTests : IAsyncLifetime
{
    private static readonly string[] PlaybackRead = [ApplicationPermissionIds.PlaybackRead.Value];

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"tuvima_cast_sessions_{Guid.NewGuid():N}");
    private DatabaseConnection _database = null!;
    private CastSessionService _sessions = null!;
    private CastAccessGrantService _grants = null!;
    private PlaybackStateRepository _state = null!;
    private PairedDevice _paired = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        DapperConfiguration.Configure();
        _database = new DatabaseConnection(Path.Combine(_root, "library.db"));
        _database.InitializeSchema();
        _database.RunStartupChecks();
        var configuration = new ConfigurationDirectoryLoader(Path.Combine(_root, "config"));
        configuration.SaveCore(new CoreConfiguration { LibraryRoot = _root });
        configuration.SaveTranscoding(new TranscodingSettings { VariantCachePath = "variants" });

        var accounts = new AccountRepository(_database);
        var applications = new ApplicationRepository(_database);
        var identities = new IdentityRepository(_database);
        var clients = new ClientAuthorizationRepository(_database);
        var protection = new EphemeralDataProtectionProvider();
        _paired = await PairedDevice.CreateAsync(_database, accounts, applications, DateTimeOffset.UtcNow);
        _grants = new CastAccessGrantService(protection, accounts, applications, clients, new PermissionRegistry());

        var assets = new MediaAssetRepository(_database);
        var tracks = new TextTrackRepository(_database);
        var ffmpeg = new UnusedFfmpegService();
        var hls = new AdaptiveHlsService(
            new AdaptiveHlsPackageRepository(_database), assets, tracks, ffmpeg, configuration,
            new TestHostApplicationLifetime(), NullLogger<AdaptiveHlsService>.Instance);
        _state = new PlaybackStateRepository(_database);
        var playback = new PlaybackCapabilitiesService(
            assets, ffmpeg, _database, _state, new PlaybackSegmentRepository(_database), tracks,
            new AudiobookChapterTitleOverrideRepository(_database), new MediaTypeExtensionCatalog(), configuration, hls,
            new HlsAccessGrantService(protection, configuration, accounts, applications, identities, clients, new PermissionRegistry()),
            new HttpContextAccessor(), NullLogger<PlaybackCapabilitiesService>.Instance);
        _sessions = new CastSessionService(playback, hls, _grants, tracks, ffmpeg);
    }

    public Task DisposeAsync()
    {
        _database.Dispose();
        using (var pool = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(_root, "library.db")}"))
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearPool(pool);
        }
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task AnMp4WithH264AndAacIsOfferedAsADirectStreamWithAPosterAndAnExpiry()
    {
        var assetId = await SeedVideoAsync("film.mp4", "h264", "aac", 1080, durationSeconds: 5400);

        var session = await CreateAsync(assetId);

        Assert.NotNull(session);
        Assert.Equal(CastSessionStatuses.Ready, session.Status);
        Assert.Equal(PlaybackDeliveryModes.DirectStream, session.Delivery);
        Assert.Equal("video/mp4", session.ContentType);
        Assert.Matches($"^/cast/v1/[A-Za-z0-9_-]+/media$", session.MediaPath);
        Assert.Matches("^/cast/v1/[A-Za-z0-9_-]+/poster$", session.PosterPath);
        Assert.Equal(5400, session.DurationSeconds);
        Assert.NotNull(session.ExpiresAt);

        var grant = session.MediaPath!.Split('/')[3];
        var validation = await _grants.ValidateAsync(grant);
        Assert.Equal(assetId, validation?.AssetId);
        Assert.Null(validation?.PackageId);
    }

    [Fact]
    public async Task AnMkvIsNotSentAsItIsAndWithoutAConverterNothingPlayableIsOffered()
    {
        var assetId = await SeedVideoAsync("film.mkv", "h264", "aac", 1080, durationSeconds: 5400);

        var session = await CreateAsync(assetId);

        Assert.NotNull(session);
        Assert.Equal(CastSessionStatuses.Unavailable, session.Status);
        Assert.Equal(PlaybackDeliveryModes.Hls, session.Delivery);
        Assert.Null(session.MediaPath);
        Assert.Null(session.ExpiresAt);
        Assert.False(string.IsNullOrWhiteSpace(session.Message));
    }

    [Theory]
    [InlineData("hevc", "aac", 1080)]
    [InlineData("h264", "ac3", 1080)]
    [InlineData("h264", "aac", 2160)]
    public async Task CodecsOrSizesAReceiverCannotPlayAreNotSentAsTheyAre(string video, string audio, int height)
    {
        var assetId = await SeedVideoAsync("film.mp4", video, audio, height, durationSeconds: 3600);

        var session = await CreateAsync(assetId);

        Assert.Equal(PlaybackDeliveryModes.Hls, session!.Delivery);
        Assert.Null(session.MediaPath);
    }

    [Fact]
    public async Task ATitleWithoutStoredInspectionIsReportedAsNotReadyAndIssuesNoGrant()
    {
        var assetId = await SeedVideoAsync("fresh.mp4", null, null, 0, durationSeconds: 0);

        var session = await CreateAsync(assetId);

        Assert.Equal(CastSessionStatuses.Unavailable, session!.Status);
        Assert.Null(session.MediaPath);
        Assert.Null(session.ExpiresAt);
    }

    [Fact]
    public async Task AnUnknownTitleIsNull()
    {
        Assert.Null(await CreateAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task TheAnswerUsesTheAgreedWireNames()
    {
        var assetId = await SeedVideoAsync("film.mp4", "h264", "aac", 720, durationSeconds: 600);

        var json = JsonSerializer.Serialize(await CreateAsync(assetId));

        using var document = JsonDocument.Parse(json);
        Assert.Equal(
            ["assetId", "contentType", "delivery", "durationSeconds", "expiresAt", "mediaPath", "message", "posterPath", "resumeSeconds", "status", "textTracks"],
            document.RootElement.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal).ToArray());
    }

    private Task<CastSessionDto?> CreateAsync(Guid assetId) =>
        _sessions.CreateAsync(assetId, _paired.Authority, _paired.Token.Id, PlaybackRead);

    private async Task<Guid> SeedVideoAsync(string fileName, string? video, string? audio, int height, double durationSeconds)
    {
        var workId = Guid.NewGuid();
        var editionId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        var hash = assetId.ToString("N");
        var path = Path.Combine(_root, fileName);
        await File.WriteAllBytesAsync(path, [0, 1, 2, 3]);
        using (var connection = _database.CreateConnection())
        {
            connection.Execute("INSERT INTO works (id, media_type) VALUES (@workId, 'Movies');", new { workId });
            connection.Execute("INSERT INTO editions (id, work_id) VALUES (@editionId, @workId);", new { editionId, workId });
            connection.Execute("""
                INSERT INTO media_assets (id, edition_id, content_hash, file_path_root, status)
                VALUES (@assetId, @editionId, @hash, @path, 'Normal');
                """, new { assetId, editionId, hash, path });
        }

        if (video is not null)
        {
            var probe = new MediaProbeResult
            {
                Duration = TimeSpan.FromSeconds(durationSeconds),
                VideoCodec = video,
                AudioCodec = audio,
                Width = height * 16 / 9,
                Height = height,
            };
            await _state.StoreInspectionAsync(
                assetId, hash, 4, durationSeconds, Path.GetExtension(fileName).TrimStart('.'), JsonSerializer.Serialize(probe));
        }

        return assetId;
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
