using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Dapper;
using MediaEngine.Api.Endpoints;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services.Playback;
using MediaEngine.Contracts.Playback;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Models;
using MediaEngine.Processors;
using MediaEngine.Processors.Contracts;
using MediaEngine.Storage;
using MediaEngine.Storage.Contracts;
using MediaEngine.Storage.Playback;
using MediaEngine.Storage.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MediaEngine.Api.Tests;

/// <summary>
/// Who may start a cast session, and what a session grant can reach, through the real route table with the REAL catalogue asset
/// filter, the REAL catalogue cast authorizer, real sessions and a real temp data store. A paired phone asks for
/// <c>google-cast-default</c>; a signed-in Dashboard session asks for <c>browser-remote-playback</c>; nobody else casts, and a Vault
/// item never casts.
/// </summary>
public sealed class CastDashboardSessionEndpointTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"tuvima_cast_dashboard_{Guid.NewGuid():N}");
    private readonly GrantCountingProtectionProvider _protection = new();
    private readonly SwitchableAuthorityResolver _authority = new();
    private readonly Guid _libraryId = Guid.NewGuid();
    private DatabaseConnection _database = null!;
    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private PlaybackStateRepository _state = null!;
    private IdentityRepository _identities = null!;
    private PairedDevice _paired = null!;
    private RequestAuthority _dashboard = null!;
    private Guid _sessionId;
    private Guid _video;
    private Guid _song;
    private Guid _vaultVideo;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        DapperConfiguration.Configure();
        _database = new DatabaseConnection(Path.Combine(_root, "library.db"));
        _database.InitializeSchema();
        _database.RunStartupChecks();
        KidsLimitTestSupport.SetLimit(_database, null);
        var configuration = new ConfigurationDirectoryLoader(Path.Combine(_root, "config"));
        configuration.SaveCore(new CoreConfiguration { LibraryRoot = _root });
        configuration.SaveTranscoding(new TranscodingSettings { VariantCachePath = "variants" });

        var accounts = new AccountRepository(_database);
        var applications = new ApplicationRepository(_database);
        _identities = new IdentityRepository(_database);
        var clients = new ClientAuthorizationRepository(_database);
        _paired = await PairedDevice.CreateAsync(_database, accounts, applications, DateTimeOffset.UtcNow);
        _state = new PlaybackStateRepository(_database);
        _video = await SeedAsync("film.mp4", "Movies", withProbe: true);
        _song = await SeedAsync("song.mp3", "Music", withProbe: false);
        _vaultVideo = await SeedAsync("private.mp4", "Movies", withProbe: true);
        await MoveIntoTheVaultAsync(_vaultVideo);
        _sessionId = await SignInAsync();
        _dashboard = _paired.Authority with
        {
            PrincipalKind = PrincipalKind.Human,
            ApplicationId = null,
            DeviceId = null,
            ApplicationEnabled = false,
            SessionId = _sessionId,
        };
        _authority.Current = _paired.Authority;

        var grants = new CastAccessGrantService(
            _protection, accounts, applications, clients, new PermissionRegistry(), identities: _identities);
        var assets = new MediaAssetRepository(_database);
        var tracks = new TextTrackRepository(_database);
        var ffmpeg = new UnusedFfmpegService();
        var hls = new AdaptiveHlsService(
            new AdaptiveHlsPackageRepository(_database), assets, tracks, ffmpeg, configuration,
            new TestHostApplicationLifetime(), NullLogger<AdaptiveHlsService>.Instance);
        var playback = new PlaybackCapabilitiesService(
            assets, ffmpeg, _database, _state, new PlaybackSegmentRepository(_database), tracks,
            new AudiobookChapterTitleOverrideRepository(_database), new MediaTypeExtensionCatalog(), configuration, hls,
            new HlsAccessGrantService(_protection, configuration, accounts, applications, _identities, clients, new PermissionRegistry()),
            new HttpContextAccessor(), NullLogger<PlaybackCapabilitiesService>.Instance);

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddRateLimiter(options =>
        {
            options.AddPolicy("streaming", _ => System.Threading.RateLimiting.RateLimitPartition.GetNoLimiter("test"));
            options.AddPolicy("adaptive_streaming", _ => System.Threading.RateLimiting.RateLimitPartition.GetNoLimiter("test"));
            options.AddPolicy("cast_grants", _ => System.Threading.RateLimiting.RateLimitPartition.GetNoLimiter("test"));
        });
        builder.Services.AddAuthentication("test")
            .AddScheme<AuthenticationSchemeOptions, PairedPhoneAuthenticationHandler>("test", _ => { });
        builder.Services.AddAuthorization(options =>
            options.AddPolicy(AuthPolicies.Authenticated, policy => policy.RequireAuthenticatedUser()));
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddSingleton(new PairedTokenHolder(_paired.Token.Id));
        builder.Services.AddSingleton<IDatabaseConnection>(_database);
        builder.Services.AddSingleton<IRequestAuthorityResolver>(_authority);
        builder.Services.AddSingleton<IAccountRepository>(accounts);
        builder.Services.AddSingleton<IProfileAccessRepository>(new ProfileAccessRepository(_database));
        builder.Services.AddSingleton<IApplicationRepository>(applications);
        builder.Services.AddSingleton<IPermissionRegistry, PermissionRegistry>();
        builder.Services.AddSingleton<IAuthorizationAuditWriter, DiscardingAuditWriter>();
        builder.Services.AddSingleton<IGrantAdminUnlockService, UnusedUnlockService>();
        builder.Services.AddSingleton<IVaultLibraryRepository>(new VaultLibraryRepository(_database));
        builder.Services.AddSingleton<MediaEngine.Domain.Contracts.IHouseholdLibraryAccessRepository>(new MediaEngine.Storage.HouseholdLibraryAccessRepository(_database));
        builder.Services.AddSingleton<LibraryVisibilityRule>();
        builder.Services.AddSingleton<IAccountAccessDecisionService, AccountAccessDecisionService>();
        builder.Services.AddSingleton<MediaEngine.Domain.Contracts.IAuthorizationEvaluator, AuthorizationEvaluator>();
        builder.Services.AddScoped<CatalogueResourceAuthorizationService>();
        builder.Services.AddScoped<ICastAssetAuthorizer, CatalogueCastAssetAuthorizer>();
        builder.Services.AddSingleton(grants);
        builder.Services.AddSingleton(new CastSessionService(playback, hls, grants, tracks, ffmpeg));
        builder.Services.AddSingleton<IMediaAssetRepository>(assets);
        builder.Services.AddSingleton<ITextTrackRepository>(tracks);
        builder.Services.AddSingleton<IWorkRepository>(new WorkRepository(_database));
        builder.Services.AddSingleton<IEntityAssetRepository>(new EntityAssetRepository(_database));
        builder.Services.AddSingleton<IByteStreamer, ByteStreamer>();
        builder.Services.AddSingleton(hls);
        _app = builder.Build();
        _app.UseAuthentication();
        _app.UseAuthorization();
        _app.UseRateLimiter();
        _app.MapCastEndpoints();
        // Stand-in for any other playback route: the same filter, so it shows what an unlocked person may reach.
        _app.MapGet("/test/playback/{assetId:guid}", () => Results.Ok())
            .RequireAuthorization(AuthPolicies.Authenticated)
            .RequireCatalogueAssetAccess(ApplicationPermissionIds.PlaybackRead);
        await _app.StartAsync();
        var address = new Uri(_app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.Single());
        _client = new HttpClient { BaseAddress = address };
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
        _database.Dispose();
        using (var pool = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(_root, "library.db")}"))
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearPool(pool);
        }
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task ASignedInDashboardSessionStartsACast_TheReceiverPlaysIt_AndSigningOutStopsItAtTheNextRequest()
    {
        _authority.Current = _dashboard;

        using var started = await StartCastAsync(_video, CastReceivers.BrowserRemotePlayback);

        Assert.Equal(HttpStatusCode.OK, started.StatusCode);
        Assert.True(started.Headers.CacheControl?.NoStore);
        Assert.True(started.Headers.CacheControl?.Private);
        var session = (await started.Content.ReadFromJsonAsync<CastSessionDto>())!;
        Assert.Equal(CastSessionStatuses.Ready, session.Status);
        Assert.Equal(1, _protection.GrantsIssued);
        using (var playing = await _client.GetAsync(session.MediaPath))
        {
            Assert.Equal(HttpStatusCode.OK, playing.StatusCode);
            Assert.Equal("video/mp4", playing.Content.Headers.ContentType?.MediaType);
        }

        await _identities.RevokeSessionAsync(_sessionId, DateTimeOffset.UtcNow, "signed out");

        using var stopped = await _client.GetAsync(session.MediaPath);
        Assert.Equal(HttpStatusCode.Forbidden, stopped.StatusCode);
        using var poster = await _client.GetAsync(session.PosterPath);
        Assert.Equal(HttpStatusCode.Forbidden, poster.StatusCode);
    }

    [Fact]
    public async Task ASongCastsThroughTheSameMediaRouteWithItsOwnContentType()
    {
        _authority.Current = _dashboard;

        using var started = await StartCastAsync(_song, CastReceivers.BrowserRemotePlayback);

        Assert.Equal(HttpStatusCode.OK, started.StatusCode);
        var session = (await started.Content.ReadFromJsonAsync<CastSessionDto>())!;
        Assert.Equal(CastSessionStatuses.Ready, session.Status);
        Assert.Equal("audio/mpeg", session.ContentType);
        Assert.Empty(session.TextTracks);
        using var playing = await _client.GetAsync(session.MediaPath);
        Assert.Equal(HttpStatusCode.OK, playing.StatusCode);
        Assert.Equal("audio/mpeg", playing.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task APairedPhoneStillCastsWithTheGoogleReceiver()
    {
        using var started = await StartCastAsync(_video, CastReceivers.GoogleCastDefault);

        Assert.Equal(HttpStatusCode.OK, started.StatusCode);
        var session = (await started.Content.ReadFromJsonAsync<CastSessionDto>())!;
        using var playing = await _client.GetAsync(session.MediaPath);
        Assert.Equal(HttpStatusCode.OK, playing.StatusCode);
    }

    [Fact]
    public async Task EachCallerMayAskOnlyForItsOwnReceiver()
    {
        _authority.Current = _dashboard;
        using var dashboardAsPhone = await StartCastAsync(_video, CastReceivers.GoogleCastDefault);
        _authority.Current = _paired.Authority;
        using var phoneAsDashboard = await StartCastAsync(_video, CastReceivers.BrowserRemotePlayback);
        using var unknown = await StartCastAsync(_video, "airplay");

        Assert.Equal(HttpStatusCode.Forbidden, dashboardAsPhone.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, phoneAsDashboard.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        Assert.Equal(0, _protection.GrantsIssued);
    }

    [Fact]
    public async Task AnApplicationAnUnauthenticatedCallerAndASessionlessPersonAreRefused()
    {
        var application = new RequestAuthority(
            PrincipalKind.ServiceApplication, true, ApplicationId: _paired.Application.Id, ApplicationEnabled: true,
            ApplicationAuthorizationVersion: _paired.Application.AuthorizationVersion);
        foreach (var authority in new[]
        {
            application,
            new RequestAuthority(PrincipalKind.Anonymous, false),
            new RequestAuthority(PrincipalKind.Setup, true),
            _dashboard with { SessionId = null },
        })
        {
            _authority.Current = authority;
            foreach (var receiver in new[] { CastReceivers.BrowserRemotePlayback, CastReceivers.GoogleCastDefault })
            {
                using var response = await StartCastAsync(_video, receiver);

                Assert.True(
                    response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized,
                    $"{authority.PrincipalKind} / {receiver} answered {response.StatusCode}.");
            }
        }

        Assert.Equal(0, _protection.GrantsIssued);
    }

    [Fact]
    public async Task AVaultItemNeverCasts_EvenToAnUnlockedSessionThatMayOtherwiseOpenIt()
    {
        _authority.Current = _dashboard;
        await UnlockVaultAsync(_sessionId);
        using (var control = await _client.GetAsync($"/test/playback/{_vaultVideo:D}"))
        {
            // The unlocked person can open the file on any other playback route, so the refusal below is the cast rule itself.
            Assert.Equal(HttpStatusCode.OK, control.StatusCode);
        }

        using var response = await StartCastAsync(_vaultVideo, CastReceivers.BrowserRemotePlayback);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Casting not allowed", body, StringComparison.Ordinal);
        Assert.DoesNotContain("/cast/v1/", body, StringComparison.Ordinal);
        Assert.Equal(0, _protection.GrantsIssued);
    }

    [Fact]
    public async Task AGrantMadeForAnOrdinaryTitleStopsTheMomentTheFileIsInTheVault()
    {
        _authority.Current = _dashboard;
        using var started = await StartCastAsync(_video, CastReceivers.BrowserRemotePlayback);
        var session = (await started.Content.ReadFromJsonAsync<CastSessionDto>())!;
        using (var playing = await _client.GetAsync(session.MediaPath))
        {
            Assert.Equal(HttpStatusCode.OK, playing.StatusCode);
        }

        await MoveIntoTheVaultAsync(_video);
        await UnlockVaultAsync(_sessionId);

        using var refused = await _client.GetAsync(session.MediaPath);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
    }

    private Task<HttpResponseMessage> StartCastAsync(Guid assetId, string receiver) =>
        _client.PostAsJsonAsync($"/api/v1/playback/{assetId:D}/cast", new CastSessionRequestDto { Receiver = receiver });

    private async Task<Guid> SignInAsync()
    {
        var now = DateTimeOffset.UtcNow;
        var session = new AuthSession
        {
            Id = Guid.NewGuid(),
            AccountId = _paired.Account.Id,
            ActiveProfileId = Profile.SeedProfileId,
            TokenHash = Guid.NewGuid().ToString("N"),
            DeviceId = "browser",
            DeviceName = "Browser",
            Client = "web",
            AuthenticationMethod = "password",
            SecurityStamp = Guid.NewGuid().ToString("N"),
            CreatedAt = now,
            LastSeenAt = now,
            ExpiresAt = now.AddDays(1),
        };
        await _identities.InsertSessionAsync(session);
        return session.Id;
    }

    /// <summary>Opens the seeded profile up and switches Vault mode on for one session.</summary>
    private async Task UnlockVaultAsync(Guid session)
    {
        var nsfw = new NsfwAccessRepository(_database);
        await nsfw.SetAsync(Profile.SeedProfileId, NsfwAccessMode.Allowed, null, null, DateTimeOffset.UtcNow);
        await nsfw.UnlockAsync(session, Profile.SeedProfileId, NsfwUnlockMethod.Allowed, 15, DateTimeOffset.UtcNow);
    }

    private async Task MoveIntoTheVaultAsync(Guid asset)
    {
        var vaultLibrary = Guid.NewGuid().ToString("D");
        var now = DateTimeOffset.UtcNow;
        await new VaultLibraryRepository(_database).AddAsync(
            new MediaEngine.Domain.Contracts.VaultLibrary(vaultLibrary, MediaEngine.Domain.Contracts.VaultLookupSources.None, null, now, null, now, now));
        using var connection = _database.CreateConnection();
        // The data store refuses to move an existing file into a Vault, so the same file is entered again as a Vault file.
        connection.Execute(
            """
            CREATE TEMP TABLE vault_copy AS SELECT id, edition_id, content_hash, file_path_root FROM media_assets WHERE id = @asset;
            DELETE FROM media_assets WHERE id = @asset;
            INSERT INTO media_assets (id, edition_id, content_hash, file_path_root, status, library_id)
            SELECT id, edition_id, content_hash, file_path_root, 'Normal', @vaultLibrary FROM vault_copy;
            DROP TABLE vault_copy;
            """,
            new { asset, vaultLibrary });
    }

    private async Task<Guid> SeedAsync(string fileName, string mediaType, bool withProbe)
    {
        var workId = Guid.NewGuid();
        var editionId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        var hash = assetId.ToString("N");
        var path = Path.Combine(_root, $"{hash}{Path.GetExtension(fileName)}");
        await File.WriteAllBytesAsync(path, [0, 1, 2, 3]);
        using (var connection = _database.CreateConnection())
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO works (id, media_type) VALUES (@workId, @mediaType);
                INSERT INTO editions (id, work_id) VALUES (@editionId, @workId);
                INSERT INTO media_assets (id, edition_id, content_hash, file_path_root, status, library_id)
                VALUES (@assetId, @editionId, @hash, @path, 'Normal', @libraryId);
                """,
                new { workId, editionId, assetId, hash, path, mediaType, libraryId = _libraryId.ToString("D") });
        }

        if (withProbe)
        {
            var probe = new MediaProbeResult
            {
                Duration = TimeSpan.FromSeconds(5400),
                VideoCodec = "h264",
                AudioCodec = "aac",
                Width = 1920,
                Height = 1080,
            };
            await _state.StoreInspectionAsync(assetId, hash, 4, 5400, "mp4", JsonSerializer.Serialize(probe));
        }

        return assetId;
    }

    private sealed class SwitchableAuthorityResolver : IRequestAuthorityResolver
    {
        public RequestAuthority Current { get; set; } = new(PrincipalKind.Anonymous, false);

        public ValueTask<RequestAuthority> ResolveAsync(HttpContext context, CancellationToken ct = default) =>
            ValueTask.FromResult(Current);
    }

    private sealed class DiscardingAuditWriter : IAuthorizationAuditWriter
    {
        public ValueTask WriteAsync(AuthorizationAuditEvent auditEvent, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;
    }

    private sealed class UnusedUnlockService : IGrantAdminUnlockService
    {
        public ValueTask<GrantAdminUnlockState> GetStateAsync(RequestAuthority authority, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<GrantAdminUnlockState> UnlockAsync(RequestAuthority authority, string pin, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask LockAsync(RequestAuthority authority, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    /// <summary>Counts how many cast grants were sealed: every issued grant passes through one Protect call.</summary>
    private sealed class GrantCountingProtectionProvider : IDataProtectionProvider
    {
        private readonly EphemeralDataProtectionProvider _inner = new();
        private int _issued;

        public int GrantsIssued => Volatile.Read(ref _issued);

        public IDataProtector CreateProtector(string purpose) =>
            purpose == CastAccessGrantService.ProtectorPurpose
                ? new CountingProtector(_inner.CreateProtector(purpose), () => Interlocked.Increment(ref _issued))
                : _inner.CreateProtector(purpose);

        private sealed class CountingProtector(IDataProtector inner, Action onProtect) : IDataProtector
        {
            public IDataProtector CreateProtector(string purpose) => inner.CreateProtector(purpose);

            public byte[] Protect(byte[] plaintext)
            {
                onProtect();
                return inner.Protect(plaintext);
            }

            public byte[] Unprotect(byte[] protectedData) => inner.Unprotect(protectedData);
        }
    }

    private sealed class PairedPhoneAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var tokenId = Context.RequestServices.GetRequiredService<PairedTokenHolder>().TokenId;
            var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, "cast-dashboard-test"),
                new Claim(TuvimaClaimTypes.TokenId, tokenId.ToString("D")),
                new Claim(TuvimaClaimTypes.Scope, ApplicationPermissionIds.PlaybackRead.Value),
            ], Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
        }
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

    private sealed class PairedTokenHolder(Guid tokenId)
    {
        public Guid TokenId { get; } = tokenId;
    }
}
