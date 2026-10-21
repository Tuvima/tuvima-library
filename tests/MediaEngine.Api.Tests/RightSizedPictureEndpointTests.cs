using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Dapper;
using MediaEngine.Api.Endpoints;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services.Artwork;
using MediaEngine.Domain;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Services;
using MediaEngine.Providers.Helpers;
using MediaEngine.Storage;
using MediaEngine.Storage.Contracts;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SkiaSharp;

namespace MediaEngine.Api.Tests;

/// <summary>
/// Drives the real person-face and stored-picture routes with real repositories and picture files, to prove a face
/// is served at the size asked for (even one saved the old direct way) and that the browser keep rule follows the
/// exact-copy and change-marker conditions.
/// </summary>
public sealed class RightSizedPictureEndpointTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), $"tuvima_right_sized_{Guid.NewGuid():N}");
    private readonly DatabaseConnection _database;
    private readonly AssetPathService _paths;
    private readonly Guid _libraryId = Guid.NewGuid();

    public RightSizedPictureEndpointTests()
    {
        DapperConfiguration.Configure();
        Directory.CreateDirectory(_tempRoot);
        _database = new DatabaseConnection(Path.Combine(_tempRoot, "library.db"));
        _database.InitializeSchema();
        _database.RunStartupChecks();
        _paths = new AssetPathService(Path.Combine(_tempRoot, "library"));
    }

    public void Dispose()
    {
        try { _database.Dispose(); } catch { }
        try { Directory.Delete(_tempRoot, recursive: true); } catch { }
    }

    [Fact]
    public async Task HeadshotAtTiny_ForAFaceThatStartedAsADirectDownload_ReturnsAFileOf96PixelsOrLess_AndRegistersAManagedHeadshot()
    {
        var personId = await SeedCreditedPersonAsync();
        var facePath = _paths.GetPersonHeadshotPath(personId);
        AssetPathService.EnsureDirectory(facePath);
        File.WriteAllBytes(facePath, CreateJpeg(1000, 1250));
        await SetLocalHeadshotAsync(personId, facePath);
        var assets = new EntityAssetRepository(_database);
        Assert.Empty(await assets.GetByEntityAsync(personId.ToString(), "Headshot"));

        await using var app = await StartAsync();
        using var client = new HttpClient { BaseAddress = app.Address };
        using var response = await client.GetAsync($"/persons/{personId:D}/headshot?size=t");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/jpeg", response.Content.Headers.ContentType?.MediaType);
        var bytes = await response.Content.ReadAsByteArrayAsync();
        using var decoded = SKBitmap.Decode(bytes);
        Assert.True(Math.Max(decoded.Width, decoded.Height) <= 96, $"{decoded.Width}x{decoded.Height}");
        Assert.True(bytes.Length < new FileInfo(facePath).Length);
        var managed = Assert.Single(await assets.GetByEntityAsync(personId.ToString(), "Headshot"));
        Assert.True(managed.IsPreferred);
        Assert.True(File.Exists(managed.LocalImagePathTiny));
        Assert.Equal(facePath, managed.LocalImagePath);
    }

    [Fact]
    public async Task HeadshotAtSmall_ServesTheSmallCopy_NotTheOriginal()
    {
        var personId = await SeedCreditedPersonAsync();
        var asset = await SeedManagedHeadshotAsync(personId);

        await using var app = await StartAsync();
        using var client = new HttpClient { BaseAddress = app.Address };
        var bytes = await client.GetByteArrayAsync($"/persons/{personId:D}/headshot?size=s");

        Assert.Equal(await File.ReadAllBytesAsync(asset.LocalImagePathSmall!), bytes);
    }

    [Fact]
    public async Task StoredArtworkAtTiny_WithTheCurrentChangeMarker_IsKeptForAYear()
    {
        var personId = await SeedCreditedPersonAsync();
        var asset = await SeedManagedHeadshotAsync(personId);
        var marker = ArtworkVersionMarker.ForFile(asset.LocalImagePath);

        await using var app = await StartAsync();
        using var client = new HttpClient { BaseAddress = app.Address };
        using var response = await client.GetAsync($"/stream/artwork/{asset.Id:D}?size=t&v={marker}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("immutable, max-age=31536000, private", CacheControlOf(response));
    }

    [Fact]
    public async Task StoredArtworkAtTiny_WithAWrongOrMissingChangeMarker_IsCheckedOnEachUse()
    {
        var personId = await SeedCreditedPersonAsync();
        var asset = await SeedManagedHeadshotAsync(personId);

        await using var app = await StartAsync();
        using var client = new HttpClient { BaseAddress = app.Address };
        using var wrong = await client.GetAsync($"/stream/artwork/{asset.Id:D}?size=t&v=0000000000");
        using var missing = await client.GetAsync($"/stream/artwork/{asset.Id:D}?size=t");

        Assert.Equal("no-cache, private", CacheControlOf(wrong));
        Assert.Equal("no-cache, private", CacheControlOf(missing));
    }

    [Fact]
    public async Task StoredArtworkAtTiny_WhenAStandInCopyWasServed_IsCheckedOnEachUse()
    {
        var personId = await SeedCreditedPersonAsync();
        var asset = await SeedManagedHeadshotAsync(personId);
        var marker = ArtworkVersionMarker.ForFile(asset.LocalImagePath);
        File.Delete(asset.LocalImagePathTiny!);

        await using var app = await StartAsync();
        using var client = new HttpClient { BaseAddress = app.Address };
        using var response = await client.GetAsync($"/stream/artwork/{asset.Id:D}?size=t&v={marker}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-cache, private", CacheControlOf(response));
        Assert.Equal(await File.ReadAllBytesAsync(asset.LocalImagePathSmall!), await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task StoredArtwork_RejectsASizeOtherThanTinySmallMediumOrLarge()
    {
        var personId = await SeedCreditedPersonAsync();
        var asset = await SeedManagedHeadshotAsync(personId);

        await using var app = await StartAsync();
        using var client = new HttpClient { BaseAddress = app.Address };
        using var response = await client.GetAsync($"/stream/artwork/{asset.Id:D}?size=xl");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("'t', 's', 'm', or 'l'", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    // The client re-orders the directives it parses, so they are compared as a set (sorted).
    private static string CacheControlOf(HttpResponseMessage response) =>
        string.Join(", ", (response.Headers.TryGetValues("Cache-Control", out var values) ? values : [])
            .SelectMany(value => value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            .Order(StringComparer.Ordinal));

    private async Task<Guid> SeedCreditedPersonAsync()
    {
        var personId = Guid.NewGuid();
        var workId = Guid.NewGuid();
        var editionId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        await using var connection = _database.CreateConnection();
        await connection.ExecuteAsync("""
            INSERT INTO works (id, media_type, work_kind, ownership) VALUES (@workId, 'Books', 'standalone', 'Owned');
            INSERT INTO editions (id, work_id) VALUES (@editionId, @workId);
            INSERT INTO media_assets (id, edition_id, content_hash, file_path_root, library_id)
            VALUES (@assetId, @editionId, @hash, 'books/face-test.epub', @libraryId);
            INSERT INTO canonical_values (entity_id, key, value, last_scored_at)
            VALUES (@workId, 'author', @name, datetime('now'));
            INSERT INTO persons (id, name, created_at) VALUES (@personId, @name, datetime('now'));
            """, new
        {
            workId,
            editionId,
            assetId,
            personId,
            hash = "hash-" + personId.ToString("N"),
            name = "Face Test " + personId.ToString("N"),
            libraryId = _libraryId.ToString("D"),
        });
        return personId;
    }

    private async Task SetLocalHeadshotAsync(Guid personId, string path)
    {
        await using var connection = _database.CreateConnection();
        await connection.ExecuteAsync(
            "UPDATE persons SET local_headshot_path = @path WHERE id = @personId;",
            new { path, personId });
    }

    private async Task<EntityAsset> SeedManagedHeadshotAsync(Guid personId)
    {
        var originalPath = _paths.GetPersonHeadshotPath(personId);
        AssetPathService.EnsureDirectory(originalPath);
        File.WriteAllBytes(originalPath, CreateJpeg(1000, 1250));
        var assets = new EntityAssetRepository(_database);
        var managed = await PersonHeadshotAssets.EnsureManagedAsync(
            personId, originalPath, "wikidata", null, assets, _paths, CancellationToken.None);
        Assert.NotNull(managed);
        return managed;
    }

    private static byte[] CreateJpeg(int width, int height)
    {
        using var bitmap = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(bitmap))
        using (var paint = new SKPaint { Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(width, height), [SKColors.Teal, SKColors.Pink], SKShaderTileMode.Clamp) })
        {
            canvas.DrawRect(0, 0, width, height, paint);
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 90);
        return data.ToArray();
    }

    private async Task<TestApplication> StartAsync()
    {
        var authority = new RequestAuthority(
            PrincipalKind.DelegatedUserClient,
            true,
            AccountId: Guid.NewGuid(),
            ActiveProfileId: MediaEngine.Domain.Aggregates.Profile.SeedProfileId,
            ApplicationId: Guid.NewGuid(),
            DeviceId: Guid.NewGuid(),
            AccountEnabled: true,
            GrantEnabled: true,
            ApplicationEnabled: true,
            AccountAuthorizationVersion: 1,
            GrantAuthorizationVersion: 1,
            ApplicationAuthorizationVersion: 1);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddAuthentication("test")
            .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>("test", _ => { });
        builder.Services.AddAuthorization(options => options.AddPolicy(AuthPolicies.Authenticated, policy => policy.RequireAuthenticatedUser()));
        builder.Services.AddSingleton<IAuthorizationHandler, AllowAdministratorOrApplicationHandler>();
        builder.Services.AddSingleton<IDatabaseConnection>(_database);
        builder.Services.AddSingleton<IPersonRepository, PersonRepository>();
        builder.Services.AddSingleton<IEntityAssetRepository, EntityAssetRepository>();
        builder.Services.AddSingleton(_paths);
        builder.Services.AddSingleton<ArtworkRenditionQueue>();
        builder.Services.AddSingleton<IArtworkRenditionService, ArtworkRenditionService>();
        builder.Services.AddHttpClient();
        // Every person and stream route is built when the host starts, so each service type a handler asks for must be
        // known; only the face and stored-picture routes are ever called, so the others are placeholders.
        builder.Services.AddSingleton<MediaEngine.Api.Services.Metadata.ArtworkScopeService>(_ => null!);
        builder.Services.AddSingleton<MediaEngine.Api.Services.Details.DetailComposerService>(_ => null!);
        builder.Services.AddSingleton<MediaEngine.Processors.Contracts.IByteStreamer>(_ => null!);
        builder.Services.AddSingleton<ICanonicalValueRepository>(_ => null!);
        builder.Services.AddSingleton<ICollectionRepository>(_ => null!);
        builder.Services.AddSingleton<MediaEngine.Api.Services.Display.IDisplayProjectionReadService>(_ => null!);
        builder.Services.AddSingleton<IMediaAssetRepository>(_ => null!);
        builder.Services.AddSingleton<IMediaOperationRepository>(_ => null!);
        builder.Services.AddSingleton<IPersonAliasReadService>(_ => null!);
        builder.Services.AddSingleton<IPersonAssetScopeReadService>(_ => null!);
        builder.Services.AddSingleton<IPersonCreditReadService>(_ => null!);
        builder.Services.AddSingleton<IPersonPresenceReadService>(_ => null!);
        builder.Services.AddSingleton<IPersonWorksReadService>(_ => null!);
        builder.Services.AddSingleton<ISystemActivityRepository>(_ => null!);
        builder.Services.AddSingleton<ITextTrackRepository>(_ => null!);
        builder.Services.AddSingleton<IWorkRepository>(_ => null!);
        builder.Services.AddSingleton<MediaEngine.Api.Services.ReadServices.PersonEditorReadService>(_ => null!);
        builder.Services.AddSingleton<MediaEngine.Providers.Workers.TextTrackEnrichmentWorker>(_ => null!);
        builder.Services.AddSingleton<IRequestAuthorityResolver>(new FixedAuthorityResolver(authority));
        builder.Services.AddSingleton<IAccountAccessDecisionService>(new LibraryAccess());
        builder.Services.AddSingleton<MediaEngine.Domain.Contracts.IAuthorizationEvaluator, AllowAuthorizationEvaluator>();
        builder.Services.AddScoped<CatalogueResourceAuthorizationService>();
        var app = builder.Build();
        app.UseDeveloperExceptionPage();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapPersonEndpoints();
        app.MapStreamEndpoints();
        await app.StartAsync();
        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>();
        return new TestApplication(app, new Uri(addresses!.Addresses.Single()));
    }

    private sealed class FixedAuthorityResolver(RequestAuthority authority) : IRequestAuthorityResolver
    {
        public ValueTask<RequestAuthority> ResolveAsync(HttpContext context, CancellationToken ct = default) => ValueTask.FromResult(authority);
    }

    private sealed class LibraryAccess : IAccountAccessDecisionService
    {
        public ValueTask<AuthorizationDecision> EvaluateFeatureAsync(RequestAuthority authority, AccountFeatureId feature, CancellationToken cancellationToken = default) => ValueTask.FromResult(AuthorizationDecision.Allow());
        public ValueTask<AuthorizationDecision> EvaluateLibraryAsync(RequestAuthority authority, Guid requestedLibraryId, CancellationToken cancellationToken = default) => ValueTask.FromResult(AuthorizationDecision.Allow());
        public ValueTask<AuthorizationDecision> EvaluateAdministratorAsync(RequestAuthority authority, bool requireSurfaceUnlock, CancellationToken cancellationToken = default) => ValueTask.FromResult(AuthorizationDecision.Allow());
    }

    private sealed class AllowAuthorizationEvaluator : MediaEngine.Domain.Contracts.IAuthorizationEvaluator
    {
        public ValueTask<AuthorizationDecision> EvaluateAsync(RequestAuthority authority, AuthorizationRequirement requirement, ResourceAuthorizationContext? resource, CancellationToken cancellationToken = default) => ValueTask.FromResult(AuthorizationDecision.Allow());
    }

    private sealed class TestAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() =>
            Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(
                new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "right-sized-test")], Scheme.Name)),
                Scheme.Name)));
    }

    private sealed class AllowAdministratorOrApplicationHandler : AuthorizationHandler<AdministratorOrApplicationRequirement>
    {
        protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, AdministratorOrApplicationRequirement requirement)
        {
            context.Succeed(requirement);
            return Task.CompletedTask;
        }
    }

    private sealed class TestApplication(WebApplication app, Uri address) : IAsyncDisposable
    {
        public Uri Address { get; } = address;
        public async ValueTask DisposeAsync() { await app.StopAsync(); await app.DisposeAsync(); }
    }
}
