using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Dapper;
using MediaEngine.Api.Endpoints;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services.Playback;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;
using MediaEngine.Storage;
using MediaEngine.Storage.Contracts;
using MediaEngine.TestSupport;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MediaEngine.Api.Tests;

/// <summary>
/// <c>GET /stream/{assetId}/scrub/...</c> serves a video's scrub-preview index and sprite sheets behind exactly the access checks
/// that streaming the file uses; a profile that may not play the file gets 404. Run through the real route table, real filters and a
/// real data store; only the signed-in person is faked.
/// </summary>
public sealed class ScrubPreviewEndpointTests : IDisposable
{
    private readonly ScrubPreviewWorld _world = new();

    public void Dispose() => _world.Dispose();

    private async Task<Guid> AddVideoWithPreviewsAsync(Guid library, string rating = "PG")
    {
        var assetId = await _world.AddVideoAsync("film.mkv", 5, makeFile: false, libraryId: library.ToString("D"));
        var staging = _world.Store.NewStagingFolder(assetId);
        Directory.CreateDirectory(staging);
        File.WriteAllBytes(Path.Combine(staging, "sheet-000.jpg"), [0xFF, 0xD8, 0xFF, 0xD9]);
        _world.Store.Publish(assetId, staging, new MediaEngine.Contracts.Playback.ScrubPreviewIndexDto
        {
            IntervalSeconds = 10,
            TileWidth = 320,
            TileHeight = 180,
            Columns = 10,
            Rows = 10,
            FrameCount = 12,
            DurationSeconds = 125,
            Sheets = ["sheet-000.jpg"],
        });
        using var connection = _world.Database.CreateConnection();
        var workId = connection.ExecuteScalar<Guid>("SELECT e.work_id FROM editions e JOIN media_assets ma ON ma.edition_id = e.id WHERE ma.id = @assetId;", new { assetId });
        connection.Execute(
            "INSERT INTO canonical_values (entity_id, key, value, last_scored_at) VALUES (@workId, 'content_rating', @rating, CURRENT_TIMESTAMP);",
            new { workId, rating });
        return assetId;
    }

    [Fact]
    public async Task RoutesNeedThePlaybackScopeAndTheSameAssetGateAsTheStream()
    {
        await using var app = await StartAsync(Guid.NewGuid());

        foreach (var pattern in new[] { "/stream/{assetId:guid}/scrub/index.json", "/stream/{assetId:guid}/scrub/{sheet}" })
        {
            var endpoint = app.Routes.Single(route => route.RoutePattern.RawText == pattern);
            Assert.Contains(endpoint.Metadata.OfType<ClientScopeRequirementMetadata>(),
                metadata => metadata.Scope == ApplicationPermissionIds.PlaybackRead.Value);
            Assert.Contains(endpoint.Metadata.OfType<CatalogueAssetAccessMetadata>(),
                metadata => metadata.Permission == ApplicationPermissionIds.PlaybackRead.Value);
        }
    }

    [Fact]
    public async Task ThePersonWhoMayPlayTheFile_GetsTheIndexAndTheSheets()
    {
        var library = Guid.NewGuid();
        var assetId = await AddVideoWithPreviewsAsync(library);
        await using var app = await StartAsync(library);

        using var index = await app.Client.GetAsync($"/stream/{assetId:D}/scrub/index.json");
        using var sheet = await app.Client.GetAsync($"/stream/{assetId:D}/scrub/sheet-000.jpg");

        Assert.Equal(HttpStatusCode.OK, index.StatusCode);
        Assert.Equal("application/json", index.Content.Headers.ContentType?.MediaType);
        var parsed = System.Text.Json.JsonSerializer.Deserialize<MediaEngine.Contracts.Playback.ScrubPreviewIndexDto>(
            await index.Content.ReadAsStringAsync(), new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        Assert.Equal(["sheet-000.jpg"], parsed!.Sheets);
        Assert.Equal(12, parsed.FrameCount);
        Assert.Equal(HttpStatusCode.OK, sheet.StatusCode);
        Assert.Equal("image/jpeg", sheet.Content.Headers.ContentType?.MediaType);
        Assert.Equal(4, (await sheet.Content.ReadAsByteArrayAsync()).Length);
        Assert.True(sheet.Headers.CacheControl?.Private == true);
    }

    [Fact]
    public async Task AProfileWithoutAccessToTheLibrary_GetsNotFound_NotForbidden()
    {
        var assetId = await AddVideoWithPreviewsAsync(Guid.NewGuid());
        await using var app = await StartAsync(allowedLibrary: Guid.NewGuid());

        using var index = await app.Client.GetAsync($"/stream/{assetId:D}/scrub/index.json");
        using var sheet = await app.Client.GetAsync($"/stream/{assetId:D}/scrub/sheet-000.jpg");

        Assert.Equal(HttpStatusCode.NotFound, index.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, sheet.StatusCode);
        Assert.Empty(await sheet.Content.ReadAsByteArrayAsync());
    }

    [Theory]
    [InlineData("R", false)]
    [InlineData("PG", true)]
    public async Task AKidProfilesLimit_DecidesWhetherThePreviewsAreServed(string rating, bool served)
    {
        var library = Guid.NewGuid();
        var assetId = await AddVideoWithPreviewsAsync(library, rating);
        KidsLimitTestSupport.SetLimit(_world.Database, "PG");
        await using var app = await StartAsync(library);

        using var response = await app.Client.GetAsync($"/stream/{assetId:D}/scrub/sheet-000.jpg");

        Assert.Equal(served ? HttpStatusCode.OK : HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ATitleWithoutPreviews_OrAnUnknownFile_OrAnUnlistedSheet_IsNotFound()
    {
        var library = Guid.NewGuid();
        var assetId = await AddVideoWithPreviewsAsync(library);
        var bare = await _world.AddVideoAsync("bare.mkv", 5, makeFile: false, libraryId: library.ToString("D"));
        await using var app = await StartAsync(library);

        foreach (var url in new[]
        {
            $"/stream/{bare:D}/scrub/index.json",
            $"/stream/{Guid.NewGuid():D}/scrub/index.json",
            $"/stream/{assetId:D}/scrub/sheet-007.jpg",
            $"/stream/{assetId:D}/scrub/..%2F..%2Fsecret.db",
        })
        {
            using var response = await app.Client.GetAsync(url);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
    }

    [Fact]
    public async Task WithTheSwitchOff_NoPreviewIsServed()
    {
        var library = Guid.NewGuid();
        var assetId = await AddVideoWithPreviewsAsync(library);
        _world.Save(settings => settings.ScrubPreviews.Enabled = false);
        await using var app = await StartAsync(library);

        using var response = await app.Client.GetAsync($"/stream/{assetId:D}/scrub/index.json");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task OnlyAnAdministratorMayReadOrChangeTheSwitch()
    {
        await using var app = await StartAsync(Guid.NewGuid());
        foreach (var method in new[] { HttpMethods.Get, HttpMethods.Put })
        {
            var endpoint = app.Routes.Single(route =>
                (route.RoutePattern.RawText ?? string.Empty).TrimEnd('/') == "/settings/playback/scrub-previews"
                && (route.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? []).Contains(method));
            Assert.Contains(endpoint.Metadata.OfType<IAuthorizeData>(), data => data.Policy == AuthPolicies.Administrator);
        }
    }

    /// <summary>A person on their own Dashboard session, the only kind of caller that can switch Vault mode on.</summary>
    private RequestAuthority VaultPerson(Guid account, Guid session) => new(
        PrincipalKind.Human,
        true,
        AccountId: account,
        ActiveProfileId: MediaEngine.Domain.Aggregates.Profile.SeedProfileId,
        SessionId: session,
        DeviceId: Guid.NewGuid(),
        AccountEnabled: true,
        ActiveProfileInUserHousehold: true,
        AccountAuthorizationVersion: 1,
        ProfileAuthorizationVersion: 1);

    private (Guid Account, Guid Session) MakeVaultLibrary(Guid library)
    {
        var account = Guid.NewGuid();
        var session = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        using var connection = _world.Database.CreateConnection();
        connection.Execute(
            "INSERT INTO accounts(id, email, normalized_email, created_at, updated_at, profile_id) VALUES(@account,@email,@email,@now,@now,@profile);",
            new { account, email = $"{account:N}@example.com", now = now.ToString("O"), profile = MediaEngine.Domain.Aggregates.Profile.SeedProfileId });
        connection.Execute("UPDATE profiles SET role='StandardUser' WHERE id=@profile;", new { profile = MediaEngine.Domain.Aggregates.Profile.SeedProfileId });
        connection.Execute(
            "INSERT INTO vault_libraries(library_id, lookup_source, adults_confirmed_at, created_at, updated_at) VALUES(@library,'none',@now,@now,@now);",
            new { library = library.ToString("D"), now = now.ToString("O") });
        connection.Execute(
            "INSERT INTO vault_section_grants(library_id, profile_id, created_at) VALUES(@library,@profile,@now);",
            new { library = library.ToString("D"), profile = MediaEngine.Domain.Aggregates.Profile.SeedProfileId, now = now.ToString("O") });
        connection.Execute(
            """
            INSERT INTO auth_sessions(id,account_id,active_profile_id,token_hash,device_id,device_name,client,authentication_method,security_stamp,created_at,last_seen_at,expires_at)
            VALUES(@session,@account,@profile,@hash,'device','Device','Dashboard','password','stamp',@now,@now,@later);
            """,
            new
            {
                session,
                account,
                profile = MediaEngine.Domain.Aggregates.Profile.SeedProfileId,
                hash = Guid.NewGuid().ToString("N"),
                now = now.ToString("O"),
                later = now.AddDays(30).ToString("O"),
            });
        return (account, session);
    }

    [Fact]
    public async Task AVaultVideosPreviews_AreNotFoundWhileTheVaultIsLocked()
    {
        var library = Guid.NewGuid();
        var assetId = await AddVideoWithPreviewsAsync(library);
        var (account, session) = MakeVaultLibrary(library);
        await using var app = await StartAsync(library, VaultPerson(account, session));

        using var index = await app.Client.GetAsync($"/stream/{assetId:D}/scrub/index.json");
        using var sheet = await app.Client.GetAsync($"/stream/{assetId:D}/scrub/sheet-000.jpg");

        Assert.Equal(HttpStatusCode.NotFound, index.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, sheet.StatusCode);
        Assert.Empty(await sheet.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task AVaultVideosPreviews_AreServedOnlyToAnUnlockedSession_AndAreNeverCached()
    {
        var library = Guid.NewGuid();
        var assetId = await AddVideoWithPreviewsAsync(library);
        var (account, session) = MakeVaultLibrary(library);
        var nsfw = new NsfwAccessRepository(_world.Database);
        await nsfw.SetAsync(MediaEngine.Domain.Aggregates.Profile.SeedProfileId, NsfwAccessMode.Allowed, null, null, DateTimeOffset.UtcNow);
        await nsfw.UnlockAsync(session, MediaEngine.Domain.Aggregates.Profile.SeedProfileId, NsfwUnlockMethod.Allowed, 15, DateTimeOffset.UtcNow);
        await using var app = await StartAsync(library, VaultPerson(account, session));

        using var index = await app.Client.GetAsync($"/stream/{assetId:D}/scrub/index.json");
        using var sheet = await app.Client.GetAsync($"/stream/{assetId:D}/scrub/sheet-000.jpg");

        Assert.Equal(HttpStatusCode.OK, index.StatusCode);
        Assert.Equal(HttpStatusCode.OK, sheet.StatusCode);
        foreach (var response in new[] { index, sheet })
        {
            Assert.True(response.Headers.CacheControl?.NoStore == true, "a Vault preview is never kept by a cache");
            Assert.True(response.Headers.CacheControl?.Private == true);
            Assert.Null(response.Headers.CacheControl?.MaxAge);
        }
    }

    private async Task<TestApplication> StartAsync(Guid allowedLibrary, RequestAuthority? signedIn = null)
    {
        var authority = signedIn ?? new RequestAuthority(
            PrincipalKind.DelegatedUserClient,
            true,
            AccountId: Guid.NewGuid(),
            ActiveProfileId: MediaEngine.Domain.Aggregates.Profile.SeedProfileId,
            ApplicationId: Guid.NewGuid(),
            DeviceId: Guid.NewGuid(),
            AccountEnabled: true,
            ActiveProfileInUserHousehold: true,
            ApplicationEnabled: true,
            AccountAuthorizationVersion: 1,
            ProfileAuthorizationVersion: 1,
            ApplicationAuthorizationVersion: 1);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddAuthentication("test")
            .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>("test", _ => { });
        builder.Services.AddAuthorization(options =>
            options.AddPolicy(AuthPolicies.Authenticated, policy => policy.RequireAuthenticatedUser()));
        builder.Services.AddSingleton<IDatabaseConnection>(_world.Database);
        builder.Services.AddSingleton<IMediaAssetRepository, MediaAssetRepository>();
        builder.Services.AddSingleton<IConfigurationLoader>(_world.Configuration);
        builder.Services.AddSingleton(_world.Store);
        builder.Services.AddSingleton(new ScrubPreviewSettingsService(
            _world.Configuration, _world.Store, _world.Ffmpeg, Microsoft.Extensions.Logging.Abstractions.NullLogger<ScrubPreviewSettingsService>.Instance));
        builder.Services.AddSingleton<IRequestAuthorityResolver>(new FixedAuthorityResolver(authority));
        builder.Services.AddSingleton<IAccountAccessDecisionService>(new LibraryAccess(allowedLibrary));
        builder.Services.AddSingleton<MediaEngine.Domain.Contracts.IAuthorizationEvaluator, AllowAuthorizationEvaluator>();
        builder.Services.AddScoped<CatalogueResourceAuthorizationService>();
        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapScrubPreviewEndpoints();
        await app.StartAsync();
        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>();
        return new TestApplication(app, new Uri(addresses!.Addresses.Single()));
    }

    private sealed class FixedAuthorityResolver(RequestAuthority authority) : IRequestAuthorityResolver
    {
        public ValueTask<RequestAuthority> ResolveAsync(HttpContext context, CancellationToken ct = default) =>
            ValueTask.FromResult(authority);
    }

    private sealed class LibraryAccess(Guid libraryId) : IAccountAccessDecisionService
    {
        public ValueTask<AuthorizationDecision> EvaluateFeatureAsync(
            RequestAuthority authority, AccountFeatureId feature, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(AuthorizationDecision.Allow());

        public ValueTask<AuthorizationDecision> EvaluateLibraryAsync(
            RequestAuthority authority, Guid requestedLibraryId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(requestedLibraryId == libraryId
                ? AuthorizationDecision.Allow()
                : AuthorizationDecision.Deny(AuthorizationDenialReason.LibraryNotVisible));

        public ValueTask<AuthorizationDecision> EvaluateAdministratorAsync(
            RequestAuthority authority, bool requireSurfaceUnlock, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(AuthorizationDecision.Deny(AuthorizationDenialReason.AdministratorRequired));
    }

    private sealed class AllowAuthorizationEvaluator : MediaEngine.Domain.Contracts.IAuthorizationEvaluator
    {
        public ValueTask<AuthorizationDecision> EvaluateAsync(
            RequestAuthority authority,
            AuthorizationRequirement requirement,
            ResourceAuthorizationContext? resource,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(AuthorizationDecision.Allow());
    }

    private sealed class TestAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() =>
            Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(
                new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "read-file-test")], Scheme.Name)),
                Scheme.Name)));
    }

    private sealed class TestApplication(WebApplication app, Uri address) : IAsyncDisposable
    {
        public HttpClient Client { get; } = new() { BaseAddress = address };

        public IReadOnlyList<RouteEndpoint> Routes =>
            ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints).OfType<RouteEndpoint>().ToArray();

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
}
