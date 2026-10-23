using System.Net;
using System.Net.Http.Headers;
using Dapper;
using MediaEngine.Api.Endpoints;
using MediaEngine.Api.Services.Playback;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Models;
using MediaEngine.Processors;
using MediaEngine.Processors.Contracts;
using MediaEngine.Storage;
using MediaEngine.Storage.Playback;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Api.Tests;

/// <summary>
/// The cast receiver routes (<c>/cast/v1/{grant}/...</c>) on a real local server with a real temp data store: what a TV can
/// and cannot reach with a grant, and that the grant is never written to a log.
/// </summary>
public sealed class CastEndpointTests : IAsyncLifetime
{
    private static readonly string[] PlaybackRead = [ApplicationPermissionIds.PlaybackRead.Value];

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"tuvima_cast_endpoints_{Guid.NewGuid():N}");
    private readonly CapturingLoggerProvider _logs = new();
    private readonly SwitchableAuthorizer _authorizer = new();
    private readonly byte[] _video = Enumerable.Range(0, 4096).Select(i => (byte)(i % 251)).ToArray();
    private DatabaseConnection _database = null!;
    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private CastAccessGrantService _grants = null!;
    private Guid _assetId;
    private Guid _otherAssetId;
    private Guid _packageId;
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

        var videoPath = Path.Combine(_root, "film.mp4");
        await File.WriteAllBytesAsync(videoPath, _video);
        _assetId = SeedAsset(videoPath);
        _otherAssetId = SeedAsset(videoPath);
        _packageId = Guid.NewGuid();

        var accounts = new AccountRepository(_database);
        var applications = new ApplicationRepository(_database);
        var clients = new ClientAuthorizationRepository(_database);
        _paired = await PairedDevice.CreateAsync(_database, accounts, applications, DateTimeOffset.UtcNow);
        _grants = new CastAccessGrantService(
            new EphemeralDataProtectionProvider(), accounts, applications, clients, new PermissionRegistry());

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Trace);
        builder.Logging.AddProvider(_logs);
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddRateLimiter(options =>
        {
            options.AddPolicy("streaming", _ => System.Threading.RateLimiting.RateLimitPartition.GetNoLimiter("test"));
            options.AddPolicy("adaptive_streaming", _ => System.Threading.RateLimiting.RateLimitPartition.GetNoLimiter("test"));
            options.AddPolicy("cast_grants", _ => System.Threading.RateLimiting.RateLimitPartition.GetNoLimiter("test"));
        });
        builder.Services.AddSingleton(_grants);
        builder.Services.AddSingleton<ICastAssetAuthorizer>(_authorizer);
        builder.Services.AddSingleton<IMediaAssetRepository>(new MediaAssetRepository(_database));
        builder.Services.AddSingleton<ITextTrackRepository>(new TextTrackRepository(_database));
        builder.Services.AddSingleton<IWorkRepository>(new WorkRepository(_database));
        builder.Services.AddSingleton<IEntityAssetRepository>(new EntityAssetRepository(_database));
        builder.Services.AddSingleton<IByteStreamer, ByteStreamer>();
        builder.Services.AddSingleton(new AdaptiveHlsService(
            new AdaptiveHlsPackageRepository(_database),
            new MediaAssetRepository(_database),
            new TextTrackRepository(_database),
            new UnusedFfmpegService(),
            configuration,
            new TestHostApplicationLifetime(),
            NullLogger<AdaptiveHlsService>.Instance));
        // The phone's route is mapped by the same call but is not exercised here; it only needs its services to exist.
        builder.Services.AddAuthorization();
        builder.Services.AddSingleton<MediaEngine.Api.Security.IRequestAuthorityResolver, NoAuthorityResolver>();
        builder.Services.AddSingleton(new CastSessionService(null!, null!, _grants, new TextTrackRepository(_database), new UnusedFfmpegService()));
        _app = builder.Build();
        _app.UseRateLimiter();
        _app.MapCastEndpoints();
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
    public async Task RangeRequestGets206WithTheReceiverHeaders()
    {
        var grant = await GrantAsync(_assetId);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/cast/v1/{grant}/media");
        request.Headers.Range = new RangeHeaderValue(100, 199);

        using var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal(_video[100..200], await response.Content.ReadAsByteArrayAsync());
        Assert.Equal("*", response.Headers.GetValues("Access-Control-Allow-Origin").Single());
        Assert.Contains("Content-Range", response.Headers.GetValues("Access-Control-Expose-Headers").Single());
        Assert.False(response.Headers.Contains("Access-Control-Allow-Credentials"));
        Assert.Equal("no-referrer", response.Headers.GetValues("Referrer-Policy").Single());
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Contains("no-store", response.Headers.CacheControl!.ToString());
        Assert.Equal("video/mp4", response.Content.Headers.ContentType!.MediaType);
    }

    [Fact]
    public async Task WholeFileAndHeadAreServed()
    {
        var grant = await GrantAsync(_assetId);

        using var whole = await _client.GetAsync($"/cast/v1/{grant}/media");
        Assert.Equal(HttpStatusCode.OK, whole.StatusCode);
        Assert.Equal(_video, await whole.Content.ReadAsByteArrayAsync());

        using var head = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Head, $"/cast/v1/{grant}/media"));
        Assert.Equal(HttpStatusCode.OK, head.StatusCode);
        Assert.Equal(_video.Length, head.Content.Headers.ContentLength);
    }

    [Fact]
    public async Task PreflightIsAnswered204WithoutAGrantCheckOrCredentials()
    {
        using var response = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Options, "/cast/v1/anything/media"));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("GET, HEAD, OPTIONS", response.Headers.GetValues("Access-Control-Allow-Methods").Single());
        Assert.Equal("Range", response.Headers.GetValues("Access-Control-Allow-Headers").Single());
        Assert.Equal("600", response.Headers.GetValues("Access-Control-Max-Age").Single());
        Assert.False(response.Headers.Contains("Access-Control-Allow-Credentials"));
    }

    [Fact]
    public async Task AnAuthorizationHeaderChangesNothing()
    {
        var grant = await GrantAsync(_assetId);
        using var withBearer = new HttpRequestMessage(HttpMethod.Get, $"/cast/v1/{grant}/media");
        withBearer.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "some-other-token");
        using var bad = new HttpRequestMessage(HttpMethod.Get, "/cast/v1/not-a-grant/media");
        bad.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "some-other-token");

        using var accepted = await _client.SendAsync(withBearer);
        using var refused = await _client.SendAsync(bad);

        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
    }

    [Fact]
    public async Task WritesAreNotAllowed()
    {
        var grant = await GrantAsync(_assetId);

        using var response = await _client.PostAsync($"/cast/v1/{grant}/media", new StringContent("x"));

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }

    [Fact]
    public async Task ACatalogueLimitAppliedAfterTheGrantStopsThePlayback()
    {
        var grant = await GrantAsync(_assetId);
        using (var allowed = await _client.GetAsync($"/cast/v1/{grant}/media"))
        {
            Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        }

        _authorizer.Allow = false;

        using var refused = await _client.GetAsync($"/cast/v1/{grant}/media");
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal(_assetId, _authorizer.LastAsset);
    }

    [Fact]
    public async Task AGrantNamingNoPackageNeverServesAPackageAndAPackageGrantNeverServesTheFile()
    {
        var fileGrant = await GrantAsync(_assetId);
        var packageGrant = await GrantAsync(_assetId, _packageId);

        using var wrongPackage = await _client.GetAsync($"/cast/v1/{fileGrant}/hls/{_packageId}/master.m3u8");
        using var otherPackage = await _client.GetAsync($"/cast/v1/{packageGrant}/hls/{Guid.NewGuid()}/master.m3u8");
        using var fileFromPackage = await _client.GetAsync($"/cast/v1/{packageGrant}/media");

        Assert.Equal(HttpStatusCode.Forbidden, wrongPackage.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, otherPackage.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, fileFromPackage.StatusCode);
    }

    [Fact]
    public async Task OnlyThisTitlesSubtitlesAreServed_NeverLyricsOrAnotherTitlesTrack()
    {
        var own = await SeedTrackAsync(_assetId, TextTrackKind.Subtitles, "WEBVTT\n\n00:00.000 --> 00:01.000\nHi\n");
        var lyrics = await SeedTrackAsync(_assetId, TextTrackKind.Lyrics, "la la la");
        var foreign = await SeedTrackAsync(_otherAssetId, TextTrackKind.Subtitles, "WEBVTT\n");
        var grant = await GrantAsync(_assetId);

        using var served = await _client.GetAsync($"/cast/v1/{grant}/text/{own}");
        using var lyricsResponse = await _client.GetAsync($"/cast/v1/{grant}/text/{lyrics}");
        using var foreignResponse = await _client.GetAsync($"/cast/v1/{grant}/text/{foreign}");

        Assert.Equal(HttpStatusCode.OK, served.StatusCode);
        Assert.StartsWith("WEBVTT", await served.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal("text/vtt", served.Content.Headers.ContentType!.MediaType);
        Assert.Equal(HttpStatusCode.NotFound, lyricsResponse.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, foreignResponse.StatusCode);
    }

    [Fact]
    public async Task TheCastCodeNeverLogsTheGrant_OnlyAShortFingerprint()
    {
        var grant = await GrantAsync(_assetId);
        using (await _client.GetAsync($"/cast/v1/{grant}/media")) { }
        var tampered = grant[..^1] + (grant[^1] == 'a' ? 'b' : 'a');
        using (await _client.GetAsync($"/cast/v1/{tampered}/media")) { }
        _authorizer.Allow = false;
        using (await _client.GetAsync($"/cast/v1/{grant}/media")) { }

        // The web server's own request lines spell the address; Program.cs drops every event that contains "/cast/v1/".
        var ours = _logs.MessagesFrom("MediaEngine.Api.CastReceiver");
        Assert.NotEmpty(ours);
        Assert.DoesNotContain(ours, message =>
            message.Contains(grant, StringComparison.Ordinal) || message.Contains(tampered, StringComparison.Ordinal));
        Assert.Contains(ours, message => message.Contains(CastAccessGrantService.LogId(tampered), StringComparison.Ordinal));
    }

    [Fact]
    public void TheEngineDropsEveryLogEventThatSpellsACastAddress()
    {
        var program = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "MediaEngine.Api", "Program.cs"));

        Assert.Contains("Filter.ByExcluding", program, StringComparison.Ordinal);
        Assert.Contains("\"/cast/v1/\"", program, StringComparison.Ordinal);
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MediaEngine.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }

    private async Task<string> GrantAsync(Guid assetId, Guid? packageId = null)
    {
        var created = await _grants.CreateAsync(
            assetId, packageId, _paired.Authority, _paired.Token.Id, PlaybackRead, 3600);
        return created.Value;
    }

    private Guid SeedAsset(string path)
    {
        var workId = Guid.NewGuid();
        var editionId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        using var connection = _database.CreateConnection();
        connection.Execute("INSERT INTO works (id, media_type) VALUES (@workId, 'Movies');", new { workId });
        connection.Execute("INSERT INTO editions (id, work_id) VALUES (@editionId, @workId);", new { editionId, workId });
        connection.Execute("""
            INSERT INTO media_assets (id, edition_id, content_hash, file_path_root, status)
            VALUES (@assetId, @editionId, @hash, @path, 'Normal');
            """, new { assetId, editionId, hash = assetId.ToString("N"), path });
        return assetId;
    }

    private async Task<Guid> SeedTrackAsync(Guid assetId, TextTrackKind kind, string content)
    {
        var file = Path.Combine(_root, $"{Guid.NewGuid():N}.vtt");
        await File.WriteAllTextAsync(file, content);
        var track = new TextTrack
        {
            AssetId = assetId,
            Kind = kind,
            Language = "en",
            Provider = "test",
            SourceFormat = "vtt",
            NormalizedFormat = "vtt",
            LocalPath = file,
        };
        await new TextTrackRepository(_database).UpsertAsync(track);
        return track.Id;
    }

    private sealed class NoAuthorityResolver : MediaEngine.Api.Security.IRequestAuthorityResolver
    {
        public ValueTask<RequestAuthority> ResolveAsync(Microsoft.AspNetCore.Http.HttpContext context, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class SwitchableAuthorizer : ICastAssetAuthorizer
    {
        public bool Allow { get; set; } = true;
        public Guid LastAsset { get; private set; }

        public ValueTask<bool> AllowsPlaybackAsync(RequestAuthority authority, Guid assetId, CancellationToken ct = default)
        {
            LastAsset = assetId;
            return ValueTask.FromResult(Allow);
        }
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly List<(string Category, string Message)> _messages = [];

        public IReadOnlyList<string> MessagesFrom(string category)
        {
            lock (_messages) { return [.. _messages.Where(entry => entry.Category == category).Select(entry => entry.Message)]; }
        }

        public ILogger CreateLogger(string categoryName) => new Capturing(this, categoryName);
        public void Dispose() { }

        private sealed class Capturing(CapturingLoggerProvider owner, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                lock (owner._messages) { owner._messages.Add((category, formatter(state, exception) + (exception?.ToString() ?? string.Empty))); }
            }
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
}

/// <summary>A paired phone for tests: account, user-client application, device, token family and the matching request authority.</summary>
internal sealed record PairedDevice(
    Account Account,
    MediaEngine.Domain.Entities.Application Application,
    ClientDevice Device,
    ClientToken Token,
    RequestAuthority Authority)
{
    public static async Task<PairedDevice> CreateAsync(
        DatabaseConnection database,
        AccountRepository accounts,
        ApplicationRepository applications,
        DateTimeOffset now)
    {
        var account = new Account
        {
            Id = Guid.NewGuid(),
            Email = $"{Guid.NewGuid():N}@example.com",
            NormalizedEmail = $"{Guid.NewGuid():N}@EXAMPLE.COM",
            IsEnabled = true,
            AuthorizationVersion = 1,
            CreatedAt = now,
            UpdatedAt = now,
        };
        await accounts.CreateAccountAsync(
            account,
            new AccountProfileGrant
            {
                AccountId = account.Id,
                ProfileId = Profile.SeedProfileId,
                IsDefault = true,
                IsEnabled = true,
                AuthorizationVersion = 1,
                GrantedAt = now,
            },
            new HashSet<AccountFeatureId> { AccountFeatureId.Watch },
            new HashSet<Guid>());

        var application = new MediaEngine.Domain.Entities.Application
        {
            Id = Guid.NewGuid(),
            Name = "Cast test",
            ApplicationType = ApplicationType.UserClient,
            IsEnabled = true,
            AuthorizationVersion = 1,
            CreatedAt = now,
            UpdatedAt = now,
        };
        await applications.InsertApplicationAsync(
            application, new HashSet<ApplicationPermissionId> { ApplicationPermissionIds.PlaybackRead });
        await applications.ReplaceClientBindingsAsync(application.Id, new HashSet<string> { "phone" }, now);
        application = (await applications.GetApplicationAsync(application.Id))!;
        var accountGrant = (await accounts.GetGrantAsync(account.Id, Profile.SeedProfileId))!;

        var device = new ClientDevice
        {
            Id = Guid.NewGuid(),
            ApplicationId = application.Id,
            AccountId = account.Id,
            ProfileId = Profile.SeedProfileId,
            DeviceName = "Phone",
            DeviceClass = "mobile",
            ClientId = "phone",
            ClientName = "Phone",
            ClientVersion = "1",
            Scopes = ApplicationPermissionIds.PlaybackRead.Value,
            CreatedAt = now,
            LastSeenAt = now,
        };
        var token = new ClientToken
        {
            Id = Guid.NewGuid(),
            ApplicationId = application.Id,
            AccountId = account.Id,
            ProfileId = Profile.SeedProfileId,
            DeviceId = device.Id,
            TokenFamilyId = Guid.NewGuid(),
            Kind = "access",
            TokenHash = Guid.NewGuid().ToString("N"),
            Scopes = ApplicationPermissionIds.PlaybackRead.Value,
            AccountAuthorizationVersion = account.AuthorizationVersion,
            GrantAuthorizationVersion = accountGrant.AuthorizationVersion,
            ApplicationAuthorizationVersion = application.AuthorizationVersion,
            CreatedAt = now,
            ExpiresAt = now.AddDays(30),
        };
        using (var connection = database.CreateConnection())
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO client_devices
                    (id,application_id,account_id,profile_id,device_name,device_class,client_id,client_name,client_version,
                     scopes,capabilities_json,created_at,last_seen_at)
                VALUES
                    (@Id,@ApplicationId,@AccountId,@ProfileId,@DeviceName,@DeviceClass,@ClientId,@ClientName,@ClientVersion,
                     @Scopes,'{}',@CreatedAt,@LastSeenAt);
                """,
                device);
            await connection.ExecuteAsync(
                """
                INSERT INTO client_tokens
                    (id,application_id,account_id,device_id,profile_id,token_family_id,token_kind,token_hash,scopes,
                     account_authorization_version,grant_authorization_version,application_authorization_version,
                     generation,created_at,expires_at)
                VALUES
                    (@Id,@ApplicationId,@AccountId,@DeviceId,@ProfileId,@TokenFamilyId,@Kind,@TokenHash,@Scopes,
                     @AccountAuthorizationVersion,@GrantAuthorizationVersion,@ApplicationAuthorizationVersion,
                     @Generation,@CreatedAt,@ExpiresAt);
                """,
                token);
        }

        var authority = new RequestAuthority(
            PrincipalKind.DelegatedUserClient, true, account.Id, Profile.SeedProfileId, application.Id,
            DeviceId: device.Id, AccountEnabled: true, ActiveProfileInUserHousehold: true, ApplicationEnabled: true,
            AccountAuthorizationVersion: account.AuthorizationVersion,
            ProfileAuthorizationVersion: accountGrant.AuthorizationVersion,
            ApplicationAuthorizationVersion: application.AuthorizationVersion);
        return new PairedDevice(account, application, device, token, authority);
    }
}
