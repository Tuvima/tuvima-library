using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Dapper;
using MediaEngine.Api.Endpoints;
using MediaEngine.Api.Security;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;
using MediaEngine.Storage;
using MediaEngine.Storage.Contracts;
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
/// <c>GET /read/{assetId}/file</c> serves the book file itself with byte ranges, behind the same
/// asset gate as every other reader call. Run through the real route table, real filters and a
/// real data store; only the signed-in person is faked.
/// </summary>
public sealed class ReadBookFileEndpointTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"tuvima_read_file_{Guid.NewGuid():N}.db");
    private readonly string _filesDirectory = Path.Combine(Path.GetTempPath(), $"tuvima_read_file_{Guid.NewGuid():N}");
    private readonly DatabaseConnection _database;
    private readonly byte[] _bytes = Enumerable.Range(0, 1000).Select(value => (byte)(value % 251)).ToArray();

    public ReadBookFileEndpointTests()
    {
        DapperConfiguration.Configure();
        Directory.CreateDirectory(_filesDirectory);
        _database = new DatabaseConnection(_databasePath);
        _database.InitializeSchema();
        _database.RunStartupChecks();
    }

    [Fact]
    public async Task RouteRequiresLibraryReadScopeAssetAccessAndItsOwnReaderLimit()
    {
        await using var app = await StartAsync(Guid.NewGuid());
        var endpoint = app.Routes.Single(route => route.RoutePattern.RawText == "/read/{assetId:guid}/file");

        Assert.Contains(endpoint.Metadata.OfType<ClientScopeRequirementMetadata>(),
            metadata => metadata.Scope == ApplicationPermissionIds.LibraryRead.Value);
        Assert.Contains(endpoint.Metadata.OfType<CatalogueAssetAccessMetadata>(),
            metadata => metadata.Permission == ApplicationPermissionIds.LibraryRead.Value);
        Assert.Contains(endpoint.Metadata.OfType<EnableRateLimitingAttribute>(),
            attribute => attribute.PolicyName == "reader_files");
    }

    [Fact]
    public async Task WholeFile_IsServedWithRangeAdvertisedAndSafeHeaders()
    {
        var library = Guid.NewGuid();
        var assetId = await InsertBookAsync(library, ".epub");
        await using var app = await StartAsync(library);

        using var response = await app.Client.GetAsync($"/read/{assetId:D}/file");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(_bytes, await response.Content.ReadAsByteArrayAsync());
        Assert.Equal("application/epub+zip", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("bytes", response.Headers.AcceptRanges);
        Assert.NotNull(response.Headers.ETag);
        Assert.False(response.Headers.ETag!.IsWeak);
        Assert.NotNull(response.Content.Headers.LastModified);
        Assert.Equal("private, no-cache", response.Headers.CacheControl?.ToString());
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
        Assert.Equal("default-src 'none'; sandbox", Assert.Single(response.Headers.GetValues("Content-Security-Policy")));
        Assert.Null(response.Content.Headers.ContentDisposition);
    }

    [Fact]
    public async Task ByteRange_Returns206WithExactBytesAndContentRange()
    {
        var library = Guid.NewGuid();
        var assetId = await InsertBookAsync(library, ".epub");
        await using var app = await StartAsync(library);

        using var response = await app.Client.SendAsync(Ranged(assetId, new RangeHeaderValue(0, 99)));

        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal(new ContentRangeHeaderValue(0, 99, _bytes.Length), response.Content.Headers.ContentRange);
        Assert.Equal(100, response.Content.Headers.ContentLength);
        Assert.Equal(_bytes[..100], await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task SuffixAndOpenEndedRanges_AreServed()
    {
        var library = Guid.NewGuid();
        var assetId = await InsertBookAsync(library, ".cbz");
        await using var app = await StartAsync(library);

        using var tail = await app.Client.SendAsync(Ranged(assetId, new RangeHeaderValue(null, 16)));
        using var open = await app.Client.SendAsync(Ranged(assetId, new RangeHeaderValue(990, null)));

        Assert.Equal(HttpStatusCode.PartialContent, tail.StatusCode);
        Assert.Equal(new ContentRangeHeaderValue(984, 999, _bytes.Length), tail.Content.Headers.ContentRange);
        Assert.Equal(_bytes[984..], await tail.Content.ReadAsByteArrayAsync());
        Assert.Equal(HttpStatusCode.PartialContent, open.StatusCode);
        Assert.Equal(_bytes[990..], await open.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task RangePastTheEnd_Returns416WithTheFileSize()
    {
        var library = Guid.NewGuid();
        var assetId = await InsertBookAsync(library, ".pdf");
        await using var app = await StartAsync(library);

        using var response = await app.Client.SendAsync(Ranged(assetId, new RangeHeaderValue(5000, 6000)));

        Assert.Equal(HttpStatusCode.RequestedRangeNotSatisfiable, response.StatusCode);
        Assert.Equal(_bytes.Length, response.Content.Headers.ContentRange?.Length);
    }

    [Fact]
    public async Task IfRange_OnlyHonoursTheRangeForTheCurrentVersion()
    {
        var library = Guid.NewGuid();
        var assetId = await InsertBookAsync(library, ".epub");
        await using var app = await StartAsync(library);
        using var first = await app.Client.GetAsync($"/read/{assetId:D}/file");
        var current = first.Headers.ETag!;

        var matching = Ranged(assetId, new RangeHeaderValue(0, 9));
        matching.Headers.IfRange = new RangeConditionHeaderValue(current);
        using var served = await app.Client.SendAsync(matching);

        var stale = Ranged(assetId, new RangeHeaderValue(0, 9));
        stale.Headers.IfRange = new RangeConditionHeaderValue(new EntityTagHeaderValue("\"stale\""));
        using var whole = await app.Client.SendAsync(stale);

        Assert.Equal(HttpStatusCode.PartialContent, served.StatusCode);
        Assert.Equal(HttpStatusCode.OK, whole.StatusCode);
        Assert.Equal(_bytes.Length, (await whole.Content.ReadAsByteArrayAsync()).Length);
    }

    [Fact]
    public async Task MatchingEntityTag_Returns304WithoutBytes()
    {
        var library = Guid.NewGuid();
        var assetId = await InsertBookAsync(library, ".epub");
        await using var app = await StartAsync(library);
        using var first = await app.Client.GetAsync($"/read/{assetId:D}/file");

        using var request = new HttpRequestMessage(HttpMethod.Get, $"/read/{assetId:D}/file");
        request.Headers.IfNoneMatch.Add(first.Headers.ETag!);
        using var response = await app.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotModified, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task ABookInAnotherLibrary_IsRefusedWithoutBytes()
    {
        var assetId = await InsertBookAsync(Guid.NewGuid(), ".epub");
        await using var app = await StartAsync(allowedLibrary: Guid.NewGuid());

        using var response = await app.Client.SendAsync(Ranged(assetId, new RangeHeaderValue(0, 99)));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Null(response.Content.Headers.ContentRange);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
    }

    [Theory]
    [InlineData("R", false)]
    [InlineData("PG", true)]
    public async Task AProfileContentLimit_DecidesWhetherTheFileIsServed(string rating, bool served)
    {
        var library = Guid.NewGuid();
        var (workId, assetId) = await InsertBookWithIdsAsync(library, ".epub");
        await SetRatingAsync(workId, rating);
        SetContentLimit("PG");
        await using var app = await StartAsync(library);

        using var response = await app.Client.SendAsync(Ranged(assetId, new RangeHeaderValue(0, 99)));

        Assert.Equal(served ? HttpStatusCode.PartialContent : HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task UnratedBookIsRefusedForALimitedProfile()
    {
        var library = Guid.NewGuid();
        var assetId = await InsertBookAsync(library, ".epub");
        SetContentLimit("PG");
        await using var app = await StartAsync(library);

        using var response = await app.Client.GetAsync($"/read/{assetId:D}/file");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task UnknownAsset_Returns404()
    {
        await using var app = await StartAsync(Guid.NewGuid());

        using var response = await app.Client.GetAsync($"/read/{Guid.NewGuid():D}/file");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData(".mkv")]
    [InlineData(".m4b")]
    [InlineData(".db")]
    public async Task ARealFileOfAnotherKind_IsNeverServedFromTheReaderRoute(string extension)
    {
        var library = Guid.NewGuid();
        var assetId = await InsertBookAsync(library, extension);
        await using var app = await StartAsync(library);

        using var response = await app.Client.GetAsync($"/read/{assetId:D}/file");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task AFileMissingFromDisk_Returns404()
    {
        var library = Guid.NewGuid();
        var assetId = await InsertBookAsync(library, ".epub", writeFile: false);
        await using var app = await StartAsync(library);

        using var response = await app.Client.GetAsync($"/read/{assetId:D}/file");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static HttpRequestMessage Ranged(Guid assetId, RangeHeaderValue range)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"/read/{assetId:D}/file");
        request.Headers.Range = range;
        return request;
    }

    private async Task<Guid> InsertBookAsync(Guid libraryId, string extension, bool writeFile = true) =>
        (await InsertBookWithIdsAsync(libraryId, extension, writeFile)).AssetId;

    private async Task<(Guid WorkId, Guid AssetId)> InsertBookWithIdsAsync(Guid libraryId, string extension, bool writeFile = true)
    {
        var workId = Guid.NewGuid();
        var editionId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        var path = Path.Combine(_filesDirectory, $"{assetId:N}{extension}");
        if (writeFile)
        {
            await File.WriteAllBytesAsync(path, _bytes);
        }

        using var connection = _database.CreateConnection();
        await connection.ExecuteAsync(
            """
            INSERT INTO works (id, media_type, work_kind, curator_state)
            VALUES (@workId, 'Book', 'standalone', 'accepted');
            INSERT INTO editions (id, work_id) VALUES (@editionId, @workId);
            INSERT INTO media_assets
                (id, edition_id, content_hash, file_path_root, presented_at, library_id)
            VALUES (@assetId, @editionId, @hash, @path, CURRENT_TIMESTAMP, @libraryId);
            """,
            new
            {
                workId,
                editionId,
                assetId,
                hash = Guid.NewGuid().ToString("N"),
                path,
                libraryId = libraryId.ToString("D"),
            });
        return (workId, assetId);
    }

    private async Task SetRatingAsync(Guid workId, string rating)
    {
        using var connection = _database.CreateConnection();
        await connection.ExecuteAsync(
            "INSERT INTO canonical_values (entity_id, key, value, last_scored_at) VALUES (@workId, 'content_rating', @rating, CURRENT_TIMESTAMP);",
            new { workId, rating });
    }

    private void SetContentLimit(string? limit)
    {
        using var connection = _database.CreateConnection();
        connection.Execute(
            "UPDATE profiles SET content_limit=@limit, content_limit_allow_unrated=0 WHERE id=@id",
            new { limit, id = MediaEngine.Domain.Aggregates.Profile.SeedProfileId });
    }

    private async Task<TestApplication> StartAsync(Guid allowedLibrary)
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
        builder.Services.AddAuthorization(options =>
            options.AddPolicy(AuthPolicies.Authenticated, policy => policy.RequireAuthenticatedUser()));
        builder.Services.AddSingleton<IDatabaseConnection>(_database);
        builder.Services.AddSingleton<IMediaAssetRepository, MediaAssetRepository>();
        // The other reader routes share this group; they are never called here, but their services must resolve.
        builder.Services.AddSingleton<IEpubContentService>(_ => null!);
        builder.Services.AddSingleton<IRequestAuthorityResolver>(new FixedAuthorityResolver(authority));
        builder.Services.AddSingleton<IAccountAccessDecisionService>(new LibraryAccess(allowedLibrary));
        builder.Services.AddSingleton<MediaEngine.Domain.Contracts.IAuthorizationEvaluator, AllowAuthorizationEvaluator>();
        builder.Services.AddScoped<CatalogueResourceAuthorizationService>();
        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapReadEndpoints();
        await app.StartAsync();
        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>();
        return new TestApplication(app, new Uri(addresses!.Addresses.Single()));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { _database.Dispose(); } catch (ObjectDisposedException) { /* Already closed by the pool sweep. */ }
        try { File.Delete(_databasePath); } catch (IOException) { /* The OS temp sweep removes a handle SQLite releases late. */ }
        try { Directory.Delete(_filesDirectory, recursive: true); } catch (IOException) { /* Same: best-effort temp cleanup. */ }
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
                : AuthorizationDecision.Deny(AuthorizationDenialReason.MissingLibraryGrant));

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
