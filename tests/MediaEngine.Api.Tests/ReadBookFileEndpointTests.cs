using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Dapper;
using MediaEngine.Api.Endpoints;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services.Reading;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Configuration;
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
        KidsLimitTestSupport.SetLimit(_database, null); // the seeded profile starts as an ordinary member, not a Kid's profile
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
        Assert.True(response.Headers.CacheControl?.Private == true && response.Headers.CacheControl.NoCache);
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

    [Fact]
    public async Task ComicPageRoutes_UseTheSameGateAndTheReaderLimit()
    {
        await using var app = await StartAsync(Guid.NewGuid());
        foreach (var pattern in new[] { "/read/{assetId:guid}/pages", "/read/{assetId:guid}/pages/{page:int}" })
        {
            var endpoint = app.Routes.Single(route => route.RoutePattern.RawText == pattern);
            Assert.Contains(endpoint.Metadata.OfType<ClientScopeRequirementMetadata>(),
                metadata => metadata.Scope == ApplicationPermissionIds.LibraryRead.Value);
            Assert.Contains(endpoint.Metadata.OfType<CatalogueAssetAccessMetadata>(),
                metadata => metadata.Permission == ApplicationPermissionIds.LibraryRead.Value);
            Assert.Contains(endpoint.Metadata.OfType<EnableRateLimitingAttribute>(),
                attribute => attribute.PolicyName == "reader_files");
        }
    }

    [Fact]
    public async Task ComicPages_AreListedAndServedAtAnAllowedWidth()
    {
        var library = Guid.NewGuid();
        var assetId = await InsertBookAsync(library, ".cbz", content: ComicZip(("1.jpg", ComicPageServiceTests.Jpeg(1000, 1500))));
        await using var app = await StartAsync(library);

        using var list = await app.Client.GetAsync($"/read/{assetId:D}/pages");
        using var page = await app.Client.GetAsync($"/read/{assetId:D}/pages/1?width=480");
        using var badWidth = await app.Client.GetAsync($"/read/{assetId:D}/pages/1?width=7");
        using var missing = await app.Client.GetAsync($"/read/{assetId:D}/pages/9");

        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        Assert.Contains("\"page_count\":1", await list.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Equal("image/jpeg", page.Content.Headers.ContentType?.MediaType);
        Assert.Equal("nosniff", Assert.Single(page.Headers.GetValues("X-Content-Type-Options")));
        Assert.Equal(HttpStatusCode.BadRequest, badWidth.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task ComicPages_AreNotServedForABookOrAPersonWithoutLibraryAccess()
    {
        var library = Guid.NewGuid();
        var book = await InsertBookAsync(library, ".epub");
        var comic = await InsertBookAsync(Guid.NewGuid(), ".cbz", content: ComicZip(("1.jpg", ComicPageServiceTests.Jpeg(10, 10))));
        await using var app = await StartAsync(library);

        using var notAComic = await app.Client.GetAsync($"/read/{book:D}/pages");
        using var notAllowed = await app.Client.GetAsync($"/read/{comic:D}/pages/1");

        Assert.Equal(HttpStatusCode.NotFound, notAComic.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, notAllowed.StatusCode);
    }

    [Theory]
    [InlineData(".epub", "epub")]
    [InlineData(".cbz", "comic")]
    [InlineData(".cbr", "comic")]
    [InlineData(".cb7", "comic")]
    [InlineData(".pdf", "pdf")]
    [InlineData(".azw3", "unsupported")]
    public async Task Format_SaysWhichReaderOpensTheTitle_AndCarriesTheTopLineWords(string extension, string expected)
    {
        var library = Guid.NewGuid();
        var (workId, assetId) = await InsertBookWithIdsAsync(library, extension);
        using (var connection = _database.CreateConnection())
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO canonical_values (entity_id, key, value, last_scored_at) VALUES
                    (@workId, 'title', 'Night Watch', CURRENT_TIMESTAMP),
                    (@workId, 'author', 'Terry Pratchett', CURRENT_TIMESTAMP),
                    (@workId, 'series', 'Discworld', CURRENT_TIMESTAMP),
                    (@workId, 'series_position', '29', CURRENT_TIMESTAMP);
                """,
                new { workId });
        }

        await using var app = await StartAsync(library);

        using var response = await app.Client.GetAsync($"/read/{assetId:D}/format");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains($"\"format\":\"{expected}\"", body);
        Assert.Contains("\"title\":\"Night Watch\"", body);
        Assert.Contains(expected == "comic" ? "\"subtitle\":\"Discworld #29\"" : "\"subtitle\":\"Terry Pratchett\"", body);
    }

    [Fact]
    public async Task Cb7File_IsServedLikeTheOtherReadableKinds()
    {
        var library = Guid.NewGuid();
        var assetId = await InsertBookAsync(library, ".cb7");
        await using var app = await StartAsync(library);

        using var response = await app.Client.GetAsync($"/read/{assetId:D}/file");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static byte[] ComicZip(params (string Name, byte[] Bytes)[] entries)
    {
        using var stream = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, bytes) in entries)
            {
                using var entry = zip.CreateEntry(name).Open();
                entry.Write(bytes);
            }
        }

        return stream.ToArray();
    }

    [Fact]
    public async Task NextIssue_IsTheNextOwnedIssueInTheSeries_SkippingAMissingOne_AndNothingAfterTheLast()
    {
        var library = Guid.NewGuid();
        var series = Guid.NewGuid();
        using (var connection = _database.CreateConnection())
        {
            await connection.ExecuteAsync(
                "INSERT INTO collections (id, display_name, collection_type) VALUES (@series, 'Saga', 'Series');",
                new { series });
        }

        var one = await InsertBookWithIdsAsync(library, ".cbz");
        var two = await InsertBookWithIdsAsync(library, ".cbz");
        var three = await InsertBookWithIdsAsync(library, ".cbz");
        await PlaceInSeriesAsync(series, one.WorkId, 1, owned: true);
        await PlaceInSeriesAsync(series, two.WorkId, 2, owned: false);
        await PlaceInSeriesAsync(series, three.WorkId, 3, owned: true);
        await using var app = await StartAsync(library);

        using var fromFirst = await app.Client.GetAsync($"/read/{one.AssetId:D}/next");
        using var fromLast = await app.Client.GetAsync($"/read/{three.AssetId:D}/next");

        Assert.Equal(HttpStatusCode.OK, fromFirst.StatusCode);
        var body = await fromFirst.Content.ReadAsStringAsync();
        Assert.Contains(three.AssetId.ToString("D"), body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(series.ToString("D"), body, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(HttpStatusCode.NotFound, fromLast.StatusCode);
    }

    [Fact]
    public async Task NextIssue_FollowsTheSeriesOrder_NotTheIssueNumberTheFileCarries()
    {
        var library = Guid.NewGuid();
        var series = Guid.NewGuid();
        using (var connection = _database.CreateConnection())
        {
            await connection.ExecuteAsync(
                "INSERT INTO collections (id, display_name, collection_type, wikidata_qid) VALUES (@series, 'Saga', 'Series', 'Q100');",
                new { series });
        }

        // The files are numbered 1, 2, 3, but the Series (Wikidata's manifest) puts them in the order b, c, a.
        var a = await InsertBookWithIdsAsync(library, ".cbz");
        var b = await InsertBookWithIdsAsync(library, ".cbz");
        var c = await InsertBookWithIdsAsync(library, ".cbz");
        await PlaceInSeriesAsync(series, a.WorkId, 1, owned: true);
        await PlaceInSeriesAsync(series, b.WorkId, 2, owned: true);
        await PlaceInSeriesAsync(series, c.WorkId, 3, owned: true);
        await ManifestAsync(series, a.WorkId, 3);
        await ManifestAsync(series, b.WorkId, 1);
        await ManifestAsync(series, c.WorkId, 2);
        await using var app = await StartAsync(library);

        using var fromB = await app.Client.GetAsync($"/read/{b.AssetId:D}/next");
        using var fromC = await app.Client.GetAsync($"/read/{c.AssetId:D}/next");
        using var fromA = await app.Client.GetAsync($"/read/{a.AssetId:D}/next");

        Assert.Contains(c.AssetId.ToString("D"), await fromB.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains(a.AssetId.ToString("D"), await fromC.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(HttpStatusCode.NotFound, fromA.StatusCode);
    }

    private async Task ManifestAsync(Guid series, Guid workId, double ordinal)
    {
        using var connection = _database.CreateConnection();
        await connection.ExecuteAsync(
            """
            INSERT INTO series_manifest_items
                (id, collection_id, series_qid, item_qid, raw_ordinal, parsed_ordinal, ordinal_scope_qid, membership_scope, order_source, ownership_state, linked_work_id, last_hydrated_at)
            VALUES (@id, @series, 'Q100', @qid, @raw, @ordinal, 'Q100', 'MainSequence', 'SeriesOrdinal', 'Owned', @workId, '2026-11-10T00:00:00Z');
            """,
            new { id = Guid.NewGuid(), series, workId, ordinal, raw = ordinal.ToString("0"), qid = "Q" + Guid.NewGuid().ToString("N")[..8] });
    }

    [Fact]
    public async Task NextIssue_IsNotFoundForAComicOutsideASeries_AndForAnIssueInAnotherLibrary()
    {
        var library = Guid.NewGuid();
        var series = Guid.NewGuid();
        using (var connection = _database.CreateConnection())
        {
            await connection.ExecuteAsync(
                "INSERT INTO collections (id, display_name, collection_type) VALUES (@series, 'Saga', 'Series');",
                new { series });
        }

        var alone = await InsertBookWithIdsAsync(library, ".cbz");
        var first = await InsertBookWithIdsAsync(library, ".cbz");
        var hidden = await InsertBookWithIdsAsync(Guid.NewGuid(), ".cbz"); // a library this person may not read
        await PlaceInSeriesAsync(series, first.WorkId, 1, owned: true);
        await PlaceInSeriesAsync(series, hidden.WorkId, 2, owned: true);
        await using var app = await StartAsync(library);

        using var outsideSeries = await app.Client.GetAsync($"/read/{alone.AssetId:D}/next");
        using var onlyHiddenAfter = await app.Client.GetAsync($"/read/{first.AssetId:D}/next");

        Assert.Equal(HttpStatusCode.NotFound, outsideSeries.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, onlyHiddenAfter.StatusCode);
    }

    [Fact]
    public async Task NextIssueRoute_RequiresLibraryReadScopeAndAssetAccess()
    {
        await using var app = await StartAsync(Guid.NewGuid());
        var endpoint = app.Routes.Single(route => route.RoutePattern.RawText == "/read/{assetId:guid}/next");

        Assert.Contains(endpoint.Metadata.OfType<ClientScopeRequirementMetadata>(),
            metadata => metadata.Scope == ApplicationPermissionIds.LibraryRead.Value);
        Assert.Contains(endpoint.Metadata.OfType<CatalogueAssetAccessMetadata>(),
            metadata => metadata.Permission == ApplicationPermissionIds.LibraryRead.Value);
    }

    private async Task PlaceInSeriesAsync(Guid series, Guid workId, double position, bool owned)
    {
        using var connection = _database.CreateConnection();
        await connection.ExecuteAsync(
            """
            UPDATE works
            SET collection_id = @series, ordinal = @ordinal, ordinal_sort = @position,
                ownership = @ownership, is_catalog_only = @catalogOnly
            WHERE id = @workId;
            """,
            new { series, workId, position, ordinal = (int)position, ownership = owned ? "Owned" : "Unowned", catalogOnly = owned ? 0 : 1 });
    }

    private async Task<Guid> InsertBookAsync(Guid libraryId, string extension, bool writeFile = true, byte[]? content = null) =>
        (await InsertBookWithIdsAsync(libraryId, extension, writeFile, content)).AssetId;

    private async Task<(Guid WorkId, Guid AssetId)> InsertBookWithIdsAsync(Guid libraryId, string extension, bool writeFile = true, byte[]? content = null)
    {
        var workId = Guid.NewGuid();
        var editionId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        var path = Path.Combine(_filesDirectory, $"{assetId:N}{extension}");
        if (writeFile)
        {
            await File.WriteAllBytesAsync(path, content ?? _bytes);
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
        KidsLimitTestSupport.SetLimit(_database, limit);
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
        builder.Services.AddSingleton<IDatabaseConnection>(_database);
        builder.Services.AddSingleton<IMediaAssetRepository, MediaAssetRepository>();
        // The other reader routes share this group; they are never called here, but their services must resolve.
        builder.Services.AddSingleton<IEpubContentService>(_ => null!);
        builder.Services.AddSingleton<IReaderTitleService>(_ => new ReaderTitleService(_database));
        builder.Services.AddScoped<IReaderNextIssueService, ReaderNextIssueService>();
        builder.Services.AddSingleton<IComicPageService>(_ => new ComicPageService(() => new ReaderConfiguration(), Path.Combine(_filesDirectory, "cache")));
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
        TestTemp.ReleaseSqlitePool(_databasePath);
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
