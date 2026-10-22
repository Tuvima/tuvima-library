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
    private PlaybackCapabilitiesService _playback = null!;
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
        _playback = new PlaybackCapabilitiesService(
            assets, ffmpeg, _database, _state, new PlaybackSegmentRepository(_database), tracks,
            new AudiobookChapterTitleOverrideRepository(_database), new MediaTypeExtensionCatalog(), configuration, hls,
            new HlsAccessGrantService(protection, configuration, accounts, applications, identities, clients, new PermissionRegistry()),
            new HttpContextAccessor(), NullLogger<PlaybackCapabilitiesService>.Instance);
        _sessions = new CastSessionService(_playback, hls, _grants, tracks, ffmpeg);
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

    [Fact]
    public async Task TheManifestSaysWhetherTheFileIsInTheVault_SoTheDashboardCanKeepItsTitleOutOfTheTabAndMediaControls()
    {
        var ordinary = await SeedVideoAsync("home.mp4", "h264", "aac", 1080, durationSeconds: 60);
        var vault = await SeedVideoAsync("private.mp4", "h264", "aac", 1080, durationSeconds: 60);
        var vaultLibrary = Guid.NewGuid().ToString("D");
        var now = DateTimeOffset.UtcNow;
        await new VaultLibraryRepository(_database).AddAsync(
            new MediaEngine.Domain.Contracts.VaultLibrary(vaultLibrary, MediaEngine.Domain.Contracts.VaultLookupSources.None, null, now, null, now, now));
        using (var connection = _database.CreateConnection())
        {
            // The data store refuses to move an existing file into a Vault, so the same file is entered again as a Vault file.
            connection.Execute(
                """
                CREATE TEMP TABLE vault_copy AS SELECT id, edition_id, content_hash, file_path_root FROM media_assets WHERE id = @vault;
                DELETE FROM media_assets WHERE id = @vault;
                INSERT INTO media_assets (id, edition_id, content_hash, file_path_root, status, library_id)
                SELECT id, edition_id, content_hash, file_path_root, 'Normal', @vaultLibrary FROM vault_copy;
                DROP TABLE vault_copy;
                """,
                new { vault, vaultLibrary });
        }

        Assert.False((await _playback.BuildManifestAsync(ordinary, "web"))!.IsVault);
        Assert.True((await _playback.BuildManifestAsync(vault, "web"))!.IsVault);
    }

    [Fact]
    public async Task EachProfileResumesFromItsOwnPositionOnASharedTitle()
    {
        var assetId = await SeedVideoAsync("shared.mp4", "h264", "aac", 1080, durationSeconds: 7200);
        var watchedFor40Minutes = Guid.NewGuid();
        var watchedFor5Minutes = Guid.NewGuid();
        var neverWatched = Guid.NewGuid();
        SeedProgress(watchedFor40Minutes, assetId, positionSeconds: 2400, progressPct: 33.3, lastAccessed: "2026-10-01 10:00:00");
        // The other profile touched the title more recently: asset-only lookup would pick this row for everybody.
        SeedProgress(watchedFor5Minutes, assetId, positionSeconds: 300, progressPct: 4.2, lastAccessed: "2026-10-02 10:00:00");

        var first = await _playback.BuildManifestAsync(assetId, "web", watchedFor40Minutes);
        var second = await _playback.BuildManifestAsync(assetId, "web", watchedFor5Minutes);
        var third = await _playback.BuildManifestAsync(assetId, "web", neverWatched);

        Assert.Equal(2400, first!.Resume!.PositionSeconds);
        Assert.Equal(300, second!.Resume!.PositionSeconds);
        Assert.Null(third!.Resume);
    }

    [Fact]
    public async Task ManifestWithNoActiveProfileHasNoResumeEvenWhenOthersHaveProgress()
    {
        var assetId = await SeedVideoAsync("anon.mp4", "h264", "aac", 1080, durationSeconds: 7200);
        SeedProgress(Guid.NewGuid(), assetId, positionSeconds: 2400, progressPct: 33.3, lastAccessed: "2026-10-01 10:00:00");

        var withoutProfile = await _playback.BuildManifestAsync(assetId, "web", profileId: null);
        var emptyProfile = await _playback.BuildManifestAsync(assetId, "web", Guid.Empty);

        Assert.Null(withoutProfile!.Resume);
        Assert.Null(emptyProfile!.Resume);
    }

    [Fact]
    public async Task ACastSessionResumesFromThePairedDevicesProfileNotAnotherOne()
    {
        var assetId = await SeedVideoAsync("cast-shared.mp4", "h264", "aac", 1080, durationSeconds: 7200);
        var otherProfile = Guid.NewGuid();
        SeedProgress(_paired.Authority.ActiveProfileId!.Value, assetId, positionSeconds: 2400, progressPct: 33.3, lastAccessed: "2026-10-01 10:00:00");
        // The other profile touched the title more recently: asset-only lookup would pick this row for everybody.
        SeedProgress(otherProfile, assetId, positionSeconds: 300, progressPct: 4.2, lastAccessed: "2026-10-02 10:00:00");

        var session = await CreateAsync(assetId);

        Assert.Equal(2400, session!.ResumeSeconds);
    }

    [Fact]
    public async Task ACastSessionHasNoResumeWhenOnlyAnotherProfileWatchedTheTitle()
    {
        var assetId = await SeedVideoAsync("cast-unseen.mp4", "h264", "aac", 1080, durationSeconds: 7200);
        SeedProgress(Guid.NewGuid(), assetId, positionSeconds: 300, progressPct: 4.2, lastAccessed: "2026-10-02 10:00:00");

        var session = await CreateAsync(assetId);

        Assert.Equal(CastSessionStatuses.Ready, session!.Status);
        Assert.Null(session.ResumeSeconds);
    }

    [Fact]
    public async Task ACastPlanResumesPerProfileAndNotAtAllWithoutOne()
    {
        var assetId = await SeedVideoAsync("cast-plan.mp4", "h264", "aac", 1080, durationSeconds: 7200);
        var watchedFor40Minutes = Guid.NewGuid();
        var watchedFor5Minutes = Guid.NewGuid();
        SeedProgress(watchedFor40Minutes, assetId, positionSeconds: 2400, progressPct: 33.3, lastAccessed: "2026-10-01 10:00:00");
        SeedProgress(watchedFor5Minutes, assetId, positionSeconds: 300, progressPct: 4.2, lastAccessed: "2026-10-02 10:00:00");

        var first = await _playback.PlanCastAsync(assetId, watchedFor40Minutes);
        var second = await _playback.PlanCastAsync(assetId, watchedFor5Minutes);
        var unseen = await _playback.PlanCastAsync(assetId, Guid.NewGuid());
        var none = await _playback.PlanCastAsync(assetId, profileId: null);

        Assert.Equal(2400, first!.ResumeSeconds);
        Assert.Equal(300, second!.ResumeSeconds);
        Assert.Null(unseen!.ResumeSeconds);
        Assert.Null(none!.ResumeSeconds);
    }

    private Task<CastSessionDto?> CreateAsync(Guid assetId) =>
        _sessions.CreateAsync(assetId, _paired.Authority, _paired.Token.Id, PlaybackRead);

    private void SeedProgress(Guid profileId, Guid assetId, double positionSeconds, double progressPct, string lastAccessed)
    {
        using var connection = _database.CreateConnection();
        connection.Execute("""
            INSERT INTO user_states (user_id, asset_id, progress_pct, last_accessed, extended_properties)
            VALUES (@profileId, @assetId, @progressPct, @lastAccessed, @extended);
            """,
            new
            {
                profileId,
                assetId,
                progressPct,
                lastAccessed,
                extended = JsonSerializer.Serialize(new Dictionary<string, string>
                {
                    ["position_seconds"] = positionSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
                }),
            });
    }

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
