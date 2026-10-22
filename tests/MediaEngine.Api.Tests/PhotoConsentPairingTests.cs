using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Dapper;
using MediaEngine.Api.Endpoints;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services.View;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Contracts.LocalAssets;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.PersonalMedia;
using MediaEngine.Domain.Services;
using MediaEngine.Storage;
using MediaEngine.Storage.Contracts;
using MediaEngine.TestSupport;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;

namespace MediaEngine.Api.Tests;

/// <summary>
/// Photos and personal videos for paired apps, end to end with nothing stubbed in the access path: a real pairing request and
/// decision, a real device token, the real bearer-token authentication, the real authority resolver and authorization
/// evaluator, and the real <c>/api/v1/view</c> routes over a real data store. A paired app sees the person's photos only when the
/// person ticked the photo consent; every other pairing, and a revoked device, is refused.
/// </summary>
public sealed class PhotoConsentPairingTests : IAsyncLifetime
{
    private const string ClientId = "tuvima-mobile";
    private const string Everything =
        "library.read artwork.read view.personal.read view.originals.read view.galleries.read";

    private static readonly string[] AllPhotoScopes = ["view.personal.read", "view.originals.read", "view.galleries.read"];

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"tuvima-photo-consent-{Guid.NewGuid():N}.db");
    private readonly string _thumbnailRoot = Path.Combine(Path.GetTempPath(), $"tuvima-photo-consent-thumbs-{Guid.NewGuid():N}");
    private readonly string _mediaRoot = Path.Combine(Path.GetTempPath(), $"tuvima-photo-consent-media-{Guid.NewGuid():N}");
    private DatabaseConnection _database = null!;
    private AccountRepository _accounts = null!;
    private ApplicationRepository _applications = null!;
    private ClientAuthorizationService _authorization = null!;
    private WebApplication _app = null!;
    private Guid _photo;
    private Guid _video;
    private Guid _gallery;
    private string[] _routes = [];
    private int _fileNumber;

    public async Task InitializeAsync()
    {
        DapperConfiguration.Configure();
        _database = new DatabaseConnection(_path);
        _database.InitializeSchema();
        _database.RunStartupChecks();
        Directory.CreateDirectory(_thumbnailRoot);
        Directory.CreateDirectory(_mediaRoot);

        _accounts = new AccountRepository(_database);
        _applications = new ApplicationRepository(_database);
        await _accounts.InsertAsync(new Account
        {
            Id = Account.SeedAccountId,
            Email = "owner@example.com",
            NormalizedEmail = "OWNER@EXAMPLE.COM",
            IsEnabled = true,
            IsAdministrator = true,
            AuthorizationVersion = 1,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await _accounts.GrantProfileAsync(new AccountProfileGrant
        {
            AccountId = Account.SeedAccountId,
            ProfileId = Profile.SeedProfileId,
            IsDefault = true,
            IsEnabled = true,
            AdminEnabled = true,
            AuthorizationVersion = 1,
            GrantedAt = DateTimeOffset.UtcNow,
        });
        _authorization = new ClientAuthorizationService(
            new ClientAuthorizationRepository(_database), _accounts, _applications, new PermissionRegistry(), TimeProvider.System);

        var assets = new LocalAssetRepository(_database);
        var spaces = new ViewPersonalSpaceRepository(_database);
        var galleries = new ViewGalleryRepository(_database);
        var libraryId = Guid.NewGuid();
        var space = await spaces.GetByOwnerAsync(Profile.SeedProfileId) ?? await spaces.CreateAsync(Profile.SeedProfileId, libraryId);
        _photo = await AddAsync(assets, space.Id, libraryId, LocalAssetMediaKinds.Image, ".jpg", "image/jpeg", Jpeg());
        _video = await AddAsync(assets, space.Id, libraryId, LocalAssetMediaKinds.Video, ".mp4", "video/mp4",
            Enumerable.Range(0, 4096).Select(index => (byte)(index % 251)).ToArray());
        _gallery = (await galleries.CreateAsync(
            new CreateViewGalleryCommand(Profile.SeedProfileId, space.Id, "Trip", ViewGalleryKind.Manual))).Id;
        _routes =
        [
            "/api/v1/view/timeline", "/api/v1/view/galleries", $"/api/v1/view/galleries/{_gallery:D}/items",
            "/api/v1/view/people", "/api/v1/view/people/ana/items", "/api/v1/view/places",
            $"/api/v1/view/items/{_photo:D}", $"/api/v1/view/items/{_photo:D}/thumbnail",
            $"/api/v1/view/items/{_photo:D}/preview", $"/api/v1/view/items/{_photo:D}/original",
            $"/api/v1/view/items/{_video:D}/playback",
        ];

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        // The real bearer-token handler; only the Dashboard-service and no-credential paths use the two stand-ins.
        builder.Services.AddAuthentication(TuvimaAuthDefaults.Scheme)
            .AddScheme<AuthenticationSchemeOptions, TuvimaAuthenticationHandler>(TuvimaAuthDefaults.Scheme, _ => { });
        builder.Services.AddAuthorization(options =>
            options.AddPolicy(AuthPolicies.Authenticated, policy => policy.RequireAuthenticatedUser()));
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddSingleton<MediaEngine.Identity.Contracts.IFirstPartyIdentityService>(_ => null!);
        builder.Services.AddSingleton<MediaEngine.Domain.Contracts.IConfigurationLoader>(_ => null!);
        builder.Services.AddSingleton(_authorization);
        builder.Services.AddSingleton<IApplicationRepository>(_applications);
        builder.Services.AddSingleton<IAccountRepository>(_accounts);
        builder.Services.AddSingleton<IProfileRepository, ProfileRepository>();
        builder.Services.AddSingleton<IRequestAuthorityResolver, RequestAuthorityResolver>();
        builder.Services.AddSingleton<IPermissionRegistry>(new PermissionRegistry());
        builder.Services.AddSingleton<IGrantAdminUnlockService>(new GrantAdminUnlockService(
            _accounts, new StubProfilePins(), TimeProvider.System));
        builder.Services.AddSingleton<IAccountAccessDecisionService, AccountAccessDecisionService>();
        builder.Services.AddSingleton<IAuthorizationAuditWriter, AuthorizationAuditWriter>();
        builder.Services.AddScoped<MediaEngine.Domain.Contracts.IAuthorizationEvaluator, AuthorizationEvaluator>();
        builder.Services.AddSingleton<IDatabaseConnection>(_database);
        builder.Services.AddSingleton<ILocalAssetRepository>(assets);
        builder.Services.AddSingleton<IViewProfileRepository>(new ViewProfileRepository(_database));
        builder.Services.AddSingleton<IViewPersonalSpaceRepository>(spaces);
        builder.Services.AddSingleton<IViewGalleryRepository>(galleries);
        builder.Services.AddSingleton<IViewSharedLibraryRepository, ViewSharedLibraryRepository>();
        builder.Services.AddSingleton<IViewScopeStore, ViewScopePersistenceService>();
        builder.Services.AddSingleton<IViewScopeResolver, ViewScopeResolver>();
        builder.Services.AddSingleton<IViewResourceStore, ViewResourcePersistenceService>();
        builder.Services.AddScoped<IViewResourceAuthorizationService, ViewResourceAuthorizationService>();
        builder.Services.AddSingleton<IViewAssetQueryBackend, ViewAssetQueryService>();
        builder.Services.AddSingleton<IViewSmartGalleryQueryService, ViewSmartGalleryQueryService>();
        builder.Services.AddScoped<IViewQueryOrchestrator, ViewQueryOrchestrator>();
        builder.Services.AddScoped<IViewRequestProfileContext, HttpViewRequestProfileContext>();
        builder.Services.AddSingleton<IViewDiscoveryRepository, ViewDiscoveryRepository>();
        builder.Services.AddScoped<ViewDiscoveryService>();
        builder.Services.AddSingleton(new ViewThumbnailService(
            new AssetPathService(_thumbnailRoot), NullLogger<ViewThumbnailService>.Instance));
        _app = builder.Build();
        _app.UseAuthentication();
        _app.UseAuthorization();
        _app.MapViewClientEndpoints();
        await _app.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _app.DisposeAsync();
        _database.Dispose();
        TestTemp.ReleaseSqlitePool(_path);
        foreach (var file in new[] { _path, $"{_path}-wal", $"{_path}-shm" }.Where(File.Exists))
        {
            File.Delete(file);
        }

        foreach (var directory in new[] { _thumbnailRoot, _mediaRoot }.Where(Directory.Exists))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    // ------------------------------------------------------------------ what the consent screen decides

    [Fact]
    public async Task APairingApprovedWithoutThePhotoConsentGets403OnEveryViewRoute()
    {
        // The app asked for photos, the person approved only the ordinary scopes.
        var token = await PairAsync(Everything, ["library.read", "artwork.read"]);

        Assert.Equal("artwork.read library.read", token.Scope);
        await AssertEveryRouteAsync(token.AccessToken, HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task AnApprovalThatOmitsTheScopesIsRefusedAndGrantsNothing()
    {
        // An approval must name its scopes: omitting them is refused exactly like an empty list, not read as "everything".
        var started = await BeginAsync(Everything);

        var refusal = await Assert.ThrowsAsync<ArgumentException>(() => DecideAsync(started.UserCode, null));
        Assert.Contains("Choose at least one thing this device may do", refusal.Message, StringComparison.Ordinal);

        var waiting = await _authorization.ExchangeAsync(new OAuthTokenRequest
        {
            GrantType = ClientAuthorizationService.DeviceGrantType,
            ClientId = ClientId,
            DeviceCode = started.DeviceCode,
        });
        Assert.Equal("authorization_pending", waiting.Error?.Error);
        Assert.Empty(await _authorization.GetDevicesAsync(Profile.SeedProfileId));
    }

    [Fact]
    public async Task ADecidedCodeAnswersNotFoundBeforeTheNothingToGrantRefusal()
    {
        var started = await BeginAsync(Everything);
        Assert.True(await DecideAsync(started.UserCode, ["library.read"]));

        // Already decided: same "false" (404) as ever, not a 400 about scopes, for an empty, omitted or real list.
        Assert.False(await DecideAsync(started.UserCode, []));
        Assert.False(await DecideAsync(started.UserCode, null));
        Assert.False(await DecideAsync(started.UserCode, ["library.read"]));
        Assert.False(await DecideAsync("ZZZZ-ZZZZ", []));
    }

    [Fact]
    public async Task AnExplicitDecisionThatGrantsNothingIsRefusedAndTheRequestStaysPending()
    {
        var started = await BeginAsync(Everything);

        var empty = await Assert.ThrowsAsync<ArgumentException>(() => DecideAsync(started.UserCode, []));
        Assert.Contains("Choose at least one thing this device may do", empty.Message, StringComparison.Ordinal);

        // A list that names only what the app never asked for also leaves nothing to grant.
        await Assert.ThrowsAsync<ArgumentException>(() => DecideAsync(started.UserCode, ["progress.read"]));

        // Nothing was stored: the request is still waiting and no device exists.
        var waiting = await _authorization.ExchangeAsync(new OAuthTokenRequest
        {
            GrantType = ClientAuthorizationService.DeviceGrantType,
            ClientId = ClientId,
            DeviceCode = started.DeviceCode,
        });
        Assert.Equal("authorization_pending", waiting.Error?.Error);
        Assert.Empty(await _authorization.GetDevicesAsync(Profile.SeedProfileId));

        // The person can still make a real choice afterwards.
        Assert.True(await DecideAsync(started.UserCode, ["library.read"]));
    }

    [Fact]
    public async Task DenyingNeverNeedsAScopeAndGrantsNone()
    {
        var started = await BeginAsync(Everything);

        Assert.True(await _authorization.DecideAsync(
            new PairingDecisionRequest { UserCode = started.UserCode, Approved = false, Scopes = [] },
            Account.SeedAccountId, Profile.SeedProfileId, Profile.SeedProfileId));

        var result = await _authorization.ExchangeAsync(new OAuthTokenRequest
        {
            GrantType = ClientAuthorizationService.DeviceGrantType,
            ClientId = ClientId,
            DeviceCode = started.DeviceCode,
        });
        Assert.Equal("access_denied", result.Error?.Error);
    }

    [Fact]
    public async Task AnAppThatNeverAskedForPhotosGets403OnEveryViewRoute()
    {
        var token = await PairAsync("library.read progress.read", ["library.read", "progress.read"]);

        await AssertEveryRouteAsync(token.AccessToken, HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task APhotoScopeTheAppDidNotAskForCannotBeAddedByTheDecision()
    {
        var token = await PairAsync("library.read", ["library.read", .. AllPhotoScopes]);

        Assert.Equal("library.read", token.Scope);
        await AssertEveryRouteAsync(token.AccessToken, HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task APairingApprovedWithThePhotoConsentGets200OnEveryViewRoute()
    {
        var token = await PairAsync(Everything, ["library.read", .. AllPhotoScopes]);

        Assert.Equal("library.read view.galleries.read view.originals.read view.personal.read", token.Scope);
        await AssertEveryRouteAsync(token.AccessToken, HttpStatusCode.OK);
    }

    [Fact]
    public async Task EachPhotoScopeOpensOnlyItsOwnRoutes()
    {
        // Browsing and galleries without originals: the original and the playback manifest are refused, the rest answer.
        var browseOnly = await PairAsync(Everything, ["view.personal.read", "view.galleries.read"]);
        var statuses = await StatusesAsync(browseOnly.AccessToken);
        Assert.Equal(HttpStatusCode.Forbidden, statuses[$"/api/v1/view/items/{_photo:D}/original"]);
        Assert.Equal(HttpStatusCode.Forbidden, statuses[$"/api/v1/view/items/{_video:D}/playback"]);
        Assert.All(statuses.Where(pair => !pair.Key.EndsWith("/original", StringComparison.Ordinal) && !pair.Key.EndsWith("/playback", StringComparison.Ordinal)),
            pair => Assert.True(pair.Value == HttpStatusCode.OK, $"{pair.Key} answered {(int)pair.Value}."));

        // Galleries only: nothing that needs personal browsing or originals.
        var galleriesOnly = await PairAsync(Everything, ["view.galleries.read"]);
        var gallery = await StatusesAsync(galleriesOnly.AccessToken);
        Assert.Equal(
            ["/api/v1/view/galleries", $"/api/v1/view/galleries/{_gallery:D}/items"],
            gallery.Where(pair => pair.Value == HttpStatusCode.OK).Select(pair => pair.Key).Order().ToArray());
    }

    [Fact]
    public async Task RefreshingATokenNeverAddsOrDropsThePhotoConsent()
    {
        var without = await PairAsync(Everything, ["library.read"]);
        var refreshedWithout = await RefreshAsync(without);
        Assert.Equal(without.Scope, refreshedWithout.Scope);
        await AssertEveryRouteAsync(refreshedWithout.AccessToken, HttpStatusCode.Forbidden);

        var with = await PairAsync(Everything, ["library.read", .. AllPhotoScopes]);
        var refreshedWith = await RefreshAsync(with);
        Assert.Equal(with.Scope, refreshedWith.Scope);
        await AssertEveryRouteAsync(refreshedWith.AccessToken, HttpStatusCode.OK);
    }

    // ------------------------------------------------------------------ the Application must hold the scopes too

    [Fact]
    public async Task TheBuiltInNativeApplicationHoldsThePhotoScopesButNoTokenCarriesThemUntilConsent()
    {
        var held = await _applications.GetApplicationPermissionsAsync(BuiltInApplicationIds.NativeClient);
        Assert.All(ApplicationPermissionIds.NativeClientPhotos, id => Assert.Contains(id, held));

        var token = await PairAsync(Everything, ["library.read"]);
        Assert.DoesNotContain("view.", token.Scope, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ThePairingReviewOffersPhotoScopesOnlyWhileTheApplicationHoldsThem()
    {
        var before = await BeginAsync(Everything);
        Assert.True((await _authorization.ReviewAsync(before.UserCode))!.OffersPhotoScopes);

        await RemovePhotoGrantsAsync();

        var after = await BeginAsync(Everything);
        Assert.False((await _authorization.ReviewAsync(after.UserCode))!.OffersPhotoScopes);
    }

    [Fact]
    public async Task ThePairingReviewOffersNoPhotoScopesWhenJustOneIsMissing()
    {
        var held = await _applications.GetApplicationPermissionsAsync(BuiltInApplicationIds.NativeClient);
        await _applications.ReplacePermissionsAsync(
            BuiltInApplicationIds.NativeClient,
            held.Where(id => id != ApplicationPermissionIds.ViewOriginalsRead).ToHashSet(),
            DateTimeOffset.UtcNow);

        var started = await BeginAsync(Everything);

        Assert.False((await _authorization.ReviewAsync(started.UserCode))!.OffersPhotoScopes);
    }

    [Fact]
    public async Task WhenTheApplicationDoesNotHoldThePhotoScopesTheConsentIsRefusedAtTheDecisionAndNoDeviceIsCreated()
    {
        await RemovePhotoGrantsAsync();
        var started = await BeginAsync(Everything);

        // A crafted decision that ticks photos is refused with a clear reason, not paired and failed later.
        var refusal = await Assert.ThrowsAsync<ArgumentException>(() => DecideAsync(started.UserCode, ["library.read", .. AllPhotoScopes]));
        Assert.Contains("not allowed to use photos and videos", refusal.Message, StringComparison.Ordinal);

        var waiting = await _authorization.ExchangeAsync(new OAuthTokenRequest
        {
            GrantType = ClientAuthorizationService.DeviceGrantType,
            ClientId = ClientId,
            DeviceCode = started.DeviceCode,
        });
        Assert.Equal("authorization_pending", waiting.Error?.Error);
        Assert.Empty(await _authorization.GetDevicesAsync(Profile.SeedProfileId));

        // Ordinary access can still be approved for that app, without photos.
        Assert.True(await DecideAsync(started.UserCode, ["library.read"]));
    }

    [Fact]
    public async Task WhenTheApplicationDoesNotHoldThePhotoScopesAnOrdinaryDecisionStillPairsWithoutThem()
    {
        await RemovePhotoGrantsAsync();

        var token = await PairAsync(Everything, ["library.read", "artwork.read"]);

        Assert.Equal("artwork.read library.read", token.Scope);
    }

    [Fact]
    public async Task AnApplicationThatLosesThePhotoScopesLaterStopsServingTheDevice()
    {
        var token = await PairAsync(Everything, ["library.read", .. AllPhotoScopes]);
        await AssertEveryRouteAsync(token.AccessToken, HttpStatusCode.OK);

        await _applications.ReplacePermissionsAsync(
            BuiltInApplicationIds.NativeClient,
            new HashSet<ApplicationPermissionId> { ApplicationPermissionIds.LibraryRead, ApplicationPermissionIds.ArtworkRead },
            DateTimeOffset.UtcNow);

        await AssertEveryRouteAsync(token.AccessToken, HttpStatusCode.Unauthorized);
    }

    // ------------------------------------------------------------------ revocation

    [Fact]
    public async Task ARevokedDeviceIsRefusedOnEveryViewRoute()
    {
        var token = await PairAsync(Everything, ["library.read", .. AllPhotoScopes]);
        await AssertEveryRouteAsync(token.AccessToken, HttpStatusCode.OK);

        Assert.True(await _authorization.RevokeDeviceAsync(token.DeviceId, Profile.SeedProfileId));

        foreach (var (route, status) in await StatusesAsync(token.AccessToken))
        {
            Assert.True(status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden,
                $"{route} answered {(int)status} for a revoked device.");
        }

        // The refresh token of that device no longer works either, so nothing can bring the access back.
        var refreshed = await _authorization.ExchangeAsync(new OAuthTokenRequest
        {
            GrantType = ClientAuthorizationService.RefreshGrantType,
            ClientId = ClientId,
            RefreshToken = token.RefreshToken,
        });
        Assert.Null(refreshed.Success);
    }

    [Fact]
    public async Task RevokingOneDeviceLeavesAnotherDevicesPhotoAccessAlone()
    {
        var first = await PairAsync(Everything, ["library.read", .. AllPhotoScopes]);
        var second = await PairAsync(Everything, ["library.read", .. AllPhotoScopes]);

        Assert.True(await _authorization.RevokeDeviceAsync(first.DeviceId, Profile.SeedProfileId));

        await AssertEveryRouteAsync(first.AccessToken, HttpStatusCode.Unauthorized);
        await AssertEveryRouteAsync(second.AccessToken, HttpStatusCode.OK);
    }

    [Fact]
    public async Task AnUnknownTokenIsRefusedOnEveryViewRoute()
    {
        await AssertEveryRouteAsync("not-a-real-token", HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task DocumentsStayANotFoundOnThisSurface()
    {
        var token = await PairAsync(Everything, ["library.read", .. AllPhotoScopes]);
        var space = (await new ViewPersonalSpaceRepository(_database).GetByOwnerAsync(Profile.SeedProfileId))!;
        var document = await AddAsync(new LocalAssetRepository(_database), space.Id, space.LibraryId,
            LocalAssetMediaKinds.Document, ".pdf", "application/pdf", "%PDF-1.4 test"u8.ToArray());

        using var client = Client(token.AccessToken);
        var nobody = await client.GetAsync($"/api/v1/view/items/{Guid.NewGuid():D}");
        foreach (var route in new[]
                 {
                     $"/api/v1/view/items/{document:D}", $"/api/v1/view/items/{document:D}/thumbnail",
                     $"/api/v1/view/items/{document:D}/original", $"/api/v1/view/items/{document:D}/playback",
                 })
        {
            using var response = await client.GetAsync(route);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        Assert.Equal(HttpStatusCode.NotFound, nobody.StatusCode);
    }

    // ------------------------------------------------------------------ helpers

    private async Task AssertEveryRouteAsync(string accessToken, HttpStatusCode expected)
    {
        foreach (var (route, status) in await StatusesAsync(accessToken))
        {
            Assert.True(status == expected, $"{route} answered {(int)status}, expected {(int)expected}.");
        }
    }

    private async Task<Dictionary<string, HttpStatusCode>> StatusesAsync(string accessToken)
    {
        using var client = Client(accessToken);
        var statuses = new Dictionary<string, HttpStatusCode>();
        foreach (var route in _routes)
        {
            using var response = await client.GetAsync(route);
            statuses[route] = response.StatusCode;
        }

        return statuses;
    }

    private HttpClient Client(string accessToken)
    {
        var address = new Uri(_app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
        var client = new HttpClient { BaseAddress = address };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return client;
    }

    private Task RemovePhotoGrantsAsync() =>
        _applications.ReplacePermissionsAsync(
            BuiltInApplicationIds.NativeClient,
            new HashSet<ApplicationPermissionId> { ApplicationPermissionIds.LibraryRead, ApplicationPermissionIds.ArtworkRead },
            DateTimeOffset.UtcNow);

    private Task<DeviceAuthorizationResponse> BeginAsync(string requestedScope) =>
        _authorization.BeginAsync(new DeviceAuthorizationRequest
        {
            ClientId = ClientId,
            ClientName = "Tuvima",
            ClientVersion = "1.0",
            DeviceName = "Mary's phone",
            DeviceClass = "mobile",
            Scope = requestedScope,
            Capabilities = new ClientCapabilitiesDto(),
        }, "https://library.example");

    private Task<bool> DecideAsync(string userCode, string[]? decisionScopes) =>
        _authorization.DecideAsync(
            new PairingDecisionRequest { UserCode = userCode, Approved = true, Scopes = decisionScopes },
            Account.SeedAccountId, Profile.SeedProfileId, Profile.SeedProfileId);

    private async Task<OAuthTokenResponse> PairAsync(string requestedScope, string[]? decisionScopes) =>
        Assert.IsType<OAuthTokenResponse>((await TryPairAsync(requestedScope, decisionScopes)).Success);

    private async Task<ClientTokenResult> TryPairAsync(string requestedScope, string[]? decisionScopes)
    {
        var started = await BeginAsync(requestedScope);
        Assert.True(await DecideAsync(started.UserCode, decisionScopes));
        return await _authorization.ExchangeAsync(new OAuthTokenRequest
        {
            GrantType = ClientAuthorizationService.DeviceGrantType,
            ClientId = ClientId,
            DeviceCode = started.DeviceCode,
        });
    }

    private async Task<OAuthTokenResponse> RefreshAsync(OAuthTokenResponse token) =>
        Assert.IsType<OAuthTokenResponse>((await _authorization.ExchangeAsync(new OAuthTokenRequest
        {
            GrantType = ClientAuthorizationService.RefreshGrantType,
            ClientId = ClientId,
            RefreshToken = token.RefreshToken,
        })).Success);

    private async Task<Guid> AddAsync(
        LocalAssetRepository assets, Guid spaceId, Guid libraryId, string kind, string extension, string mime, byte[] contents)
    {
        var path = Path.Combine(_mediaRoot, $"file-{Interlocked.Increment(ref _fileNumber):D3}{extension}");
        File.WriteAllBytes(path, contents);
        var sourceId = Guid.NewGuid();
        using (var connection = _database.CreateConnection())
        {
            connection.Execute("""
                INSERT INTO view_sources
                    (id, scope_kind, personal_space_id, library_id, source_type, name, source_key, storage_mode,
                     relative_path, external_path, include_subdirectories, enabled, created_at, updated_at)
                VALUES (@sourceId, 'personal', @spaceId, @libraryId, 'browser_upload', 'Uploads', @sourceKey, 'managed',
                        'Personal/Uploads/test', NULL, 1, 1, @now, @now);
                """, new { sourceId, spaceId, libraryId, sourceKey = $"upload:{sourceId:N}", now = DateTimeOffset.UtcNow });
        }

        var isVideo = kind == LocalAssetMediaKinds.Video;
        var isImage = kind == LocalAssetMediaKinds.Image;
        return (await assets.UpsertAsync(new LocalAssetRegistration(
            libraryId, spaceId, Profile.SeedProfileId, kind, $"Item {_fileNumber}",
            new DateTimeOffset(2026, 3, 2, 9, 0, 0, TimeSpan.Zero),
            [new LocalAssetFileRegistration(path, $"{Guid.NewGuid():N}{Guid.NewGuid():N}", Path.GetFileName(path), mime,
                new FileInfo(path).Length, DateTimeOffset.UtcNow) with { SourceId = sourceId }],
            Width: isVideo ? 1920 : isImage ? 64 : null,
            Height: isVideo ? 1080 : isImage ? 48 : null,
            DurationSeconds: isVideo ? 12.5 : null,
            VideoCodec: isVideo ? "h264" : null,
            FrameRate: isVideo ? 30 : null))).ItemId;
    }

    private static byte[] Jpeg()
    {
        using var bitmap = new SKBitmap(64, 48);
        bitmap.Erase(SKColors.CornflowerBlue);
        using var image = SKImage.FromBitmap(bitmap);
        return image.Encode(SKEncodedImageFormat.Jpeg, 80).ToArray();
    }
}
