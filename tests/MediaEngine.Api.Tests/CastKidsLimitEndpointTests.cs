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
/// A Kids profile limited to PG cannot start a cast session for an R title, and a grant made before the limit was lowered
/// stops working. Run through the real route table with the REAL catalogue asset filter, the REAL catalogue cast authorizer,
/// real account and permission checks and a real temp data store; only the signed-in paired phone is faked.
/// </summary>
public sealed class CastKidsLimitEndpointTests : IAsyncLifetime
{
    private const string RestrictedTitle = "Midnight Slasher Returns";
    private const string FamilyTitle = "Puppy Picnic";

    private static readonly string[] PlaybackRead = [ApplicationPermissionIds.PlaybackRead.Value];

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"tuvima_cast_kids_{Guid.NewGuid():N}");
    private readonly GrantCountingProtectionProvider _protection = new();
    private readonly Guid _libraryId = Guid.NewGuid();
    private DatabaseConnection _database = null!;
    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private PlaybackStateRepository _state = null!;
    private PairedDevice _paired = null!;
    private Guid _ratedRAsset;
    private Guid _ratedGAsset;

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
        _paired = await PairedDevice.CreateAsync(_database, accounts, applications, DateTimeOffset.UtcNow);
        // Nothing is hidden from the paired person, so the library is visible through the Watch area alone.
        _state = new PlaybackStateRepository(_database);
        _ratedRAsset = await SeedVideoAsync(RestrictedTitle, "R");
        _ratedGAsset = await SeedVideoAsync(FamilyTitle, "G");
        SetContentLimit("PG");

        var grants = new CastAccessGrantService(_protection, accounts, applications, clients, new PermissionRegistry());
        var assets = new MediaAssetRepository(_database);
        var tracks = new TextTrackRepository(_database);
        var ffmpeg = new UnusedFfmpegService();
        var hls = new AdaptiveHlsService(
            new AdaptiveHlsPackageRepository(_database), assets, tracks, ffmpeg, configuration,
            new TestHostApplicationLifetime(), NullLogger<AdaptiveHlsService>.Instance);
        var playback = new PlaybackCapabilitiesService(
            assets, ffmpeg, _database, _state, new PlaybackSegmentRepository(_database), tracks,
            new AudiobookChapterTitleOverrideRepository(_database), new MediaTypeExtensionCatalog(), configuration, hls,
            new HlsAccessGrantService(_protection, configuration, accounts, applications, identities, clients, new PermissionRegistry()),
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
        builder.Services.AddSingleton<IRequestAuthorityResolver>(new FixedAuthorityResolver(_paired.Authority));
        // The real checks: account feature and library grants, application permissions, and the profile's content limit.
        builder.Services.AddSingleton<IAccountRepository>(accounts);
        builder.Services.AddSingleton<IProfileAccessRepository>(new ProfileAccessRepository(_database));
        builder.Services.AddSingleton<IApplicationRepository>(applications);
        builder.Services.AddSingleton<IPermissionRegistry, PermissionRegistry>();
        builder.Services.AddSingleton<IAuthorizationAuditWriter, DiscardingAuditWriter>();
        builder.Services.AddSingleton<IGrantAdminUnlockService, UnusedUnlockService>();
        builder.Services.AddSingleton<IVaultLibraryRepository>(new VaultLibraryRepository(_database));
        builder.Services.AddSingleton<MediaEngine.Domain.Contracts.ILibraryProfileGrantRepository>(new MediaEngine.Storage.LibraryProfileGrantRepository(_database));
        builder.Services.AddSingleton<MediaEngine.Domain.Contracts.ILibraryAccessCatalog>(new FixedLibraryAccessCatalog());
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
        // Stand-in for any other playback route: the same filter, so the same refusal is expected from the cast route.
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
    public async Task AnRTitleIsRefusedForAPgProfile_LikeAnyOtherPlaybackRoute_AndNoGrantIsIssued()
    {
        using var otherRoute = await _client.GetAsync($"/test/playback/{_ratedRAsset:D}");
        Assert.Equal(HttpStatusCode.Forbidden, otherRoute.StatusCode);

        using var response = await StartCastAsync(_ratedRAsset);

        Assert.Equal(otherRoute.StatusCode, response.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, _protection.GrantsIssued);
    }

    [Fact]
    public async Task TheRefusalNeverRevealsTheTitleOrItsRating()
    {
        using var response = await StartCastAsync(_ratedRAsset);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        var everything = response + body;
        Assert.Empty(body);
        Assert.DoesNotContain(RestrictedTitle, everything, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"R\"", everything, StringComparison.Ordinal);
        Assert.DoesNotContain("rating", everything, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("rated", everything, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/cast/v1/", everything, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AGTitleIsOfferedToAPgProfileAsAReadySession()
    {
        using var response = await StartCastAsync(_ratedGAsset);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var session = await response.Content.ReadFromJsonAsync<CastSessionDto>();
        Assert.NotNull(session);
        Assert.Equal(CastSessionStatuses.Ready, session.Status);
        Assert.Equal(_ratedGAsset, session.AssetId);
        Assert.Matches("^/cast/v1/[A-Za-z0-9_-]+/media$", session.MediaPath);
        Assert.Equal(1, _protection.GrantsIssued);
    }

    [Fact]
    public async Task AGrantMadeAtNoLimit_StopsWorkingOnTheTvOnceTheLimitIsLoweredToPg()
    {
        SetContentLimit(null);
        using var started = await StartCastAsync(_ratedRAsset);
        Assert.Equal(HttpStatusCode.OK, started.StatusCode);
        var session = (await started.Content.ReadFromJsonAsync<CastSessionDto>())!;
        Assert.Equal(CastSessionStatuses.Ready, session.Status);
        Assert.Equal(1, _protection.GrantsIssued);
        using (var playing = await _client.GetAsync(session.MediaPath))
        {
            Assert.Equal(HttpStatusCode.OK, playing.StatusCode);
        }

        SetContentLimit("PG");

        using var refused = await _client.GetAsync(session.MediaPath);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        var body = await refused.Content.ReadAsStringAsync();
        Assert.Empty(body);
        Assert.DoesNotContain(RestrictedTitle, refused + body, StringComparison.OrdinalIgnoreCase);

        using var again = await StartCastAsync(_ratedRAsset);
        Assert.Equal(HttpStatusCode.Forbidden, again.StatusCode);
        Assert.Equal(1, _protection.GrantsIssued);
    }

    private Task<HttpResponseMessage> StartCastAsync(Guid assetId) =>
        _client.PostAsJsonAsync(
            $"/api/v1/playback/{assetId:D}/cast",
            new CastSessionRequestDto { Receiver = CastReceivers.GoogleCastDefault });

    private void SetContentLimit(string? limit)
    {
        using var connection = _database.CreateConnection();
        connection.Execute(
            "UPDATE profiles SET content_limit=@limit, content_limit_allow_unrated=0 WHERE id=@id",
            new { limit, id = MediaEngine.Domain.Aggregates.Profile.SeedProfileId });
    }

    /// <summary>A direct-stream MP4 with stored inspection facts, so an allowed cast session is "ready".</summary>
    private async Task<Guid> SeedVideoAsync(string title, string rating)
    {
        var workId = Guid.NewGuid();
        var editionId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        var hash = assetId.ToString("N");
        var path = Path.Combine(_root, $"{hash}.mp4");
        await File.WriteAllBytesAsync(path, [0, 1, 2, 3]);
        using (var connection = _database.CreateConnection())
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO works (id, media_type) VALUES (@workId, 'Movies');
                INSERT INTO editions (id, work_id) VALUES (@editionId, @workId);
                INSERT INTO media_assets (id, edition_id, content_hash, file_path_root, status, library_id)
                VALUES (@assetId, @editionId, @hash, @path, 'Normal', @libraryId);
                INSERT INTO canonical_values (entity_id, key, value, last_scored_at)
                VALUES (@workId, 'title', @title, CURRENT_TIMESTAMP);
                INSERT INTO canonical_values (entity_id, key, value, last_scored_at)
                VALUES (@workId, 'content_rating', @rating, CURRENT_TIMESTAMP);
                """,
                new { workId, editionId, assetId, hash, path, libraryId = _libraryId.ToString("D"), title, rating });
        }

        var probe = new MediaProbeResult
        {
            Duration = TimeSpan.FromSeconds(5400),
            VideoCodec = "h264",
            AudioCodec = "aac",
            Width = 1920,
            Height = 1080,
        };
        await _state.StoreInspectionAsync(assetId, hash, 4, 5400, "mp4", JsonSerializer.Serialize(probe));
        return assetId;
    }

    private sealed class FixedAuthorityResolver(RequestAuthority authority) : IRequestAuthorityResolver
    {
        public ValueTask<RequestAuthority> ResolveAsync(HttpContext context, CancellationToken ct = default) =>
            ValueTask.FromResult(authority);
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
                new Claim(ClaimTypes.NameIdentifier, "cast-kids-test"),
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

    /// <summary>The id of the paired phone's live access token, shared with the fake sign-in handler.</summary>
    private sealed class PairedTokenHolder(Guid tokenId)
    {
        public Guid TokenId { get; } = tokenId;
    }
}
