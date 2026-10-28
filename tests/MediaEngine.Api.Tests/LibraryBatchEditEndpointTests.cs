using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Dapper;
using MediaEngine.Api.Endpoints;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services.BatchEdit;
using MediaEngine.Api.Services.Display;
using MediaEngine.Api.Services.EditSession;
using MediaEngine.Api.Services.ReadServices;
using MediaEngine.Application.Services;
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
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MediaEngine.Api.Tests;

/// <summary>Drives the two batch edit routes over HTTP with the real services, real repositories and the real resource check.</summary>
public sealed class LibraryBatchEditEndpointTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"tuvima-batch-edit-http-{Guid.NewGuid():N}.db");
    private readonly DatabaseConnection _db;
    private readonly Guid _allowedLibrary = Guid.NewGuid();
    private readonly Guid _otherLibrary = Guid.NewGuid();

    public LibraryBatchEditEndpointTests()
    {
        DapperConfiguration.Configure();
        _db = new DatabaseConnection(_dbPath);
        _db.InitializeSchema();
        _db.RunStartupChecks();
    }

    public void Dispose()
    {
        try { _db.Dispose(); } catch (Exception) { /* test cleanup: the temp file goes next */ }
        try { File.Delete(_dbPath); } catch (IOException) { /* a leftover temp file is harmless */ }
    }

    [Fact]
    public async Task Preview_ForThreeEpisodesWithTheSameGenre_ReturnsThatValueAsNotMixed()
    {
        var episodes = SeedEpisodes(3, _allowedLibrary);
        foreach (var episode in episodes)
        {
            SeedGenre(episode.AssetId, "Drama");
        }

        await using var app = await StartAsync();
        using var client = app.CreateClient(administrator: true);
        using var response = await client.PostAsync("/library/batch-edit/preview", Json(
            $$"""{"entity_ids":[{{string.Join(",", episodes.Select(e => $"\"{e.WorkId}\""))}}],"field_keys":["genre","director"]}"""));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(3, json.RootElement.GetProperty("affected_count").GetInt32());
        var fields = json.RootElement.GetProperty("fields").EnumerateArray().ToDictionary(f => f.GetProperty("key").GetString()!);
        var genre = fields["genre"];
        Assert.Equal("list", genre.GetProperty("kind").GetString());
        Assert.False(genre.GetProperty("is_mixed").GetBoolean());
        Assert.Equal("Drama", genre.GetProperty("values")[0].GetString());
        Assert.True(genre.GetProperty("editable").GetBoolean());
        var director = fields["director"];
        Assert.False(director.GetProperty("editable").GetBoolean());
        Assert.Equal(3, director.GetProperty("locked_items").GetArrayLength());
    }

    [Theory]
    [InlineData("rating", "add")]
    [InlineData("genre", "set")]
    [InlineData("custom_tags", "clear")]
    public async Task Apply_WithAnOperationThatDoesNotFitTheFieldKind_Returns400AndChangesNothing(string key, string op)
    {
        var episodes = SeedEpisodes(2, _allowedLibrary);
        await using var app = await StartAsync();
        using var client = app.CreateClient(administrator: true);

        using var response = await client.PostAsync("/library/batch-edit", Json(Body(
            episodes, $$"""{"key":"{{key}}","op":"{{op}}","value":"x","values":["x"]}""")));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.All(episodes, episode => Assert.Empty(OverridesOf(episode.WorkId)));
    }

    [Theory]
    [InlineData("""{"key":"director","op":"add","values":["Someone"]}""")]
    [InlineData("""{"key":"title","op":"set","value":"Renamed"}""")]
    [InlineData("""{"key":"rating","op":"set"}""")]
    [InlineData("""{"key":"rating","op":"set","value":"eleven"}""")]
    public async Task Apply_WithAProviderFactAnUnknownFieldOrABadValue_Returns400(string change)
    {
        var episodes = SeedEpisodes(1, _allowedLibrary);
        await using var app = await StartAsync();
        using var client = app.CreateClient(administrator: true);

        using var response = await client.PostAsync("/library/batch-edit", Json(Body(episodes, change)));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Apply_WithMoreItemsThanABatchTakes_Returns400()
    {
        await using var app = await StartAsync();
        using var client = app.CreateClient(administrator: true);
        var ids = string.Join(",", Enumerable.Range(0, 501).Select(_ => $"\"{Guid.NewGuid()}\""));

        using var response = await client.PostAsync("/library/batch-edit", Json(
            $$"""{"entity_ids":[{{ids}}],"field_changes":[{"key":"custom_tags","op":"add","values":["x"]}]}"""));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task BothRoutes_RefuseACallerWhoIsNotAnAdministrator()
    {
        var episodes = SeedEpisodes(1, _allowedLibrary);
        await using var app = await StartAsync();
        using var client = app.CreateClient(administrator: false);

        using var preview = await client.PostAsync("/library/batch-edit/preview", Json($$"""{"entity_ids":["{{episodes[0].WorkId}}"]}"""));
        using var apply = await client.PostAsync("/library/batch-edit", Json(Body(episodes, """{"key":"custom_tags","op":"add","values":["x"]}""")));

        Assert.Equal(HttpStatusCode.Forbidden, preview.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, apply.StatusCode);
        Assert.Empty(OverridesOf(episodes[0].WorkId));
    }

    [Fact]
    public async Task Apply_ForThreeEpisodes_UpdatesThemInOneRequestAndReportsEachRow()
    {
        var episodes = SeedEpisodes(3, _allowedLibrary);
        await using var app = await StartAsync();
        using var client = app.CreateClient(administrator: true);

        using var response = await client.PostAsync("/library/batch-edit", Json(Body(
            episodes, """{"key":"custom_tags","op":"add","values":["Binge"]}""")));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(3, json.RootElement.GetProperty("updated_count").GetInt32());
        Assert.Equal(0, json.RootElement.GetProperty("sent_to_review_count").GetInt32());
        Assert.Equal(3, json.RootElement.GetProperty("items").GetArrayLength());
        Assert.All(episodes, episode => Assert.Equal("Binge", OverridesOf(episode.WorkId)["custom_tags"]));
    }

    [Fact]
    public async Task Apply_WhenOneEpisodeInheritsALockedGenre_TwoAreUpdatedAndOneIsSentToNeedsReview()
    {
        var episodes = SeedEpisodes(3, _allowedLibrary);
        SeedGenre(episodes[0].AssetId, "Comedy");
        SeedGenre(episodes[1].AssetId, "Comedy");
        SeedGenre(episodes[0].SeasonId, "Drama");
        await using var app = await StartAsync();
        using var client = app.CreateClient(administrator: true);

        using var response = await client.PostAsync("/library/batch-edit", Json(Body(
            episodes, """{"key":"genre","op":"add","values":["Thriller"]}""")));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(2, json.RootElement.GetProperty("updated_count").GetInt32());
        Assert.Equal(1, json.RootElement.GetProperty("sent_to_review_count").GetInt32());
        var review = json.RootElement.GetProperty("items").EnumerateArray()
            .Single(row => row.GetProperty("outcome").GetString() == "sent_to_review");
        Assert.Equal(episodes[2].WorkId.ToString(), review.GetProperty("entity_id").GetString());
        Assert.Equal("genre", review.GetProperty("fields_not_applied")[0].GetString());
        var reviews = new ReviewQueueRepository(_db);
        var entry = await reviews.GetByIdAsync(Guid.Parse(review.GetProperty("review_id").GetString()!));
        Assert.Equal(episodes[2].WorkId, entry!.EntityId);
        Assert.Equal("BatchEditFailed", entry.Trigger);
        Assert.Equal("Comedy, Thriller", OverridesOf(episodes[0].WorkId)["genre"]);
        Assert.Empty(OverridesOf(episodes[2].WorkId));
    }

    [Fact]
    public async Task Apply_ForAnItemInALibraryTheCallerCannotEdit_RefusesThatRowAndUpdatesTheRest()
    {
        var allowed = SeedEpisodes(2, _allowedLibrary);
        var notAllowed = SeedEpisodes(1, _otherLibrary);
        await using var app = await StartAsync();
        using var client = app.CreateClient(administrator: true);

        using var response = await client.PostAsync("/library/batch-edit", Json(Body(
            [.. allowed, .. notAllowed], """{"key":"custom_tags","op":"add","values":["Binge"]}""")));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(2, json.RootElement.GetProperty("updated_count").GetInt32());
        Assert.Equal(1, json.RootElement.GetProperty("refused_count").GetInt32());
        var refused = json.RootElement.GetProperty("items").EnumerateArray()
            .Single(row => row.GetProperty("outcome").GetString() == "refused");
        Assert.Equal(notAllowed[0].WorkId.ToString(), refused.GetProperty("entity_id").GetString());
        Assert.Contains("not allowed", refused.GetProperty("reason").GetString(), StringComparison.Ordinal);
        Assert.Empty(OverridesOf(notAllowed[0].WorkId));
        Assert.All(allowed, episode => Assert.Equal("Binge", OverridesOf(episode.WorkId)["custom_tags"]));
    }

    [Fact]
    public async Task Preview_ListsAnItemTheCallerCannotEditAsUnavailable()
    {
        var allowed = SeedEpisodes(1, _allowedLibrary);
        var notAllowed = SeedEpisodes(1, _otherLibrary);
        await using var app = await StartAsync();
        using var client = app.CreateClient(administrator: true);

        using var response = await client.PostAsync("/library/batch-edit/preview", Json(
            $$"""{"entity_ids":["{{allowed[0].WorkId}}","{{notAllowed[0].WorkId}}"],"field_keys":["rating"]}"""));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(1, json.RootElement.GetProperty("affected_count").GetInt32());
        var unavailable = Assert.Single(json.RootElement.GetProperty("unavailable_items").EnumerateArray());
        Assert.Equal(notAllowed[0].WorkId.ToString(), unavailable.GetProperty("entity_id").GetString());
    }

    // ── Harness ─────────────────────────────────────────────────────────────

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    private static string Body(IReadOnlyList<SeededEpisode> episodes, string change) =>
        $$"""{"entity_ids":[{{string.Join(",", episodes.Select(e => $"\"{e.WorkId}\""))}}],"field_changes":[{{change}}]}""";

    private Dictionary<string, string> OverridesOf(Guid workId)
    {
        using var conn = _db.CreateConnection();
        var json = conn.QuerySingleOrDefault<string?>(
            "SELECT display_overrides_json FROM works WHERE id = @workId;",
            new { workId = GuidSql.ToBlob(workId) });
        return string.IsNullOrWhiteSpace(json)
            ? new(StringComparer.OrdinalIgnoreCase)
            : new(JsonSerializer.Deserialize<Dictionary<string, string>>(json)!, StringComparer.OrdinalIgnoreCase);
    }

    private void SeedGenre(Guid ownerId, string genre)
    {
        using var conn = _db.CreateConnection();
        conn.Execute(
            "INSERT INTO canonical_value_arrays (entity_id, key, value, ordinal) VALUES (@ownerId, 'genre', @genre, 0);",
            new { ownerId = GuidSql.ToBlob(ownerId), genre });
    }

    private List<SeededEpisode> SeedEpisodes(int count, Guid libraryId)
    {
        var showId = Guid.NewGuid();
        var seasonId = Guid.NewGuid();
        var episodes = new List<SeededEpisode>();
        using var conn = _db.CreateConnection();
        conn.Execute(
            """
            INSERT INTO works (id, media_type, work_kind) VALUES (@showId, 'TV', 'parent');
            INSERT INTO works (id, parent_work_id, media_type, work_kind, ordinal) VALUES (@seasonId, @showId, 'TV', 'parent', 1);
            """,
            new { showId = GuidSql.ToBlob(showId), seasonId = GuidSql.ToBlob(seasonId) });
        for (var number = 1; number <= count; number++)
        {
            var workId = Guid.NewGuid();
            var assetId = Guid.NewGuid();
            conn.Execute(
                """
                INSERT INTO works (id, parent_work_id, media_type, work_kind, ordinal) VALUES (@workId, @seasonId, 'TV', 'child', @number);
                INSERT INTO editions (id, work_id) VALUES (@editionId, @workId);
                INSERT INTO media_assets (id, edition_id, content_hash, file_path_root, library_id)
                    VALUES (@assetId, @editionId, @hash, @path, @libraryId);
                """,
                new
                {
                    workId = GuidSql.ToBlob(workId),
                    seasonId = GuidSql.ToBlob(seasonId),
                    editionId = GuidSql.ToBlob(Guid.NewGuid()),
                    assetId = GuidSql.ToBlob(assetId),
                    number,
                    hash = $"hash-{assetId:N}",
                    path = $"C:/library/tv/{assetId:N}.mkv",
                    libraryId = libraryId.ToString("D"),
                });
            episodes.Add(new SeededEpisode(workId, assetId, seasonId));
        }

        return episodes;
    }

    private sealed record SeededEpisode(Guid WorkId, Guid AssetId, Guid SeasonId);

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
        {
            options.AddPolicy(AuthPolicies.Authenticated, policy => policy.RequireAuthenticatedUser());
            options.AddPolicy(AuthPolicies.Administrator, policy => policy
                .RequireAuthenticatedUser()
                .RequireAssertion(context => context.User.HasClaim(TestAuthenticationHandler.AdministratorClaim, "true")));
        });
        builder.Services.AddSingleton<IDatabaseConnection>(_db);
        builder.Services.AddSingleton<ILibraryCurationReadService, LibraryCurationReadService>();
        builder.Services.AddSingleton<IItemCanonicalRepository, ItemCanonicalRepository>();
        builder.Services.AddSingleton<IReviewQueueRepository, ReviewQueueRepository>();
        builder.Services.AddSingleton<ISystemActivityRepository, SystemActivityRepository>();
        builder.Services.AddSingleton<ILibraryBatchEditService, LibraryBatchEditService>();
        builder.Services.AddScoped<LibraryBatchEditAccessService>();
        builder.Services.AddScoped<EditSessionAccessService>();
        builder.Services.AddSingleton(new EditSubjectDirectory([]));
        builder.Services.AddSingleton<IEditSessionService, EditSessionService>();
        builder.Services.AddScoped<CatalogueResourceAuthorizationService>();
        builder.Services.AddSingleton<IRequestAuthorityResolver>(new FixedAuthorityResolver(authority));
        builder.Services.AddSingleton<IAccountAccessDecisionService>(new LibraryAccess(_allowedLibrary));
        builder.Services.AddSingleton<MediaEngine.Domain.Contracts.IAuthorizationEvaluator, AllowAuthorizationEvaluator>();

        // The other library routes are mapped with the group but never called here; their services only need to exist.
        builder.Services.AddSingleton<ILibraryItemRepository>(_ => null!);
        builder.Services.AddSingleton<ILibraryOverviewReadService>(_ => null!);
        builder.Services.AddSingleton<ILibraryWorkFeedReadService>(_ => null!);
        builder.Services.AddSingleton<IDisplayProjectionReadService>(_ => null!);
        builder.Services.AddSingleton<ICollectionRepository>(_ => null!);
        builder.Services.AddSingleton<IProfileRepository>(_ => null!);

        var app = builder.Build();
        app.UseDeveloperExceptionPage();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapLibraryEndpoints();
        await app.StartAsync();
        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>();
        return new TestApplication(app, new Uri(addresses!.Addresses.Single()));
    }

    private sealed class FixedAuthorityResolver(RequestAuthority authority) : IRequestAuthorityResolver
    {
        public ValueTask<RequestAuthority> ResolveAsync(HttpContext context, CancellationToken ct = default) => ValueTask.FromResult(authority);
    }

    private sealed class LibraryAccess(Guid libraryId) : IAccountAccessDecisionService
    {
        public ValueTask<AuthorizationDecision> EvaluateFeatureAsync(RequestAuthority authority, AccountFeatureId feature, CancellationToken cancellationToken = default) => ValueTask.FromResult(AuthorizationDecision.Allow());
        public ValueTask<AuthorizationDecision> EvaluateLibraryAsync(RequestAuthority authority, Guid requestedLibraryId, CancellationToken cancellationToken = default) => ValueTask.FromResult(requestedLibraryId == libraryId ? AuthorizationDecision.Allow() : AuthorizationDecision.Deny(AuthorizationDenialReason.LibraryNotVisible));
        public ValueTask<AuthorizationDecision> EvaluateAdministratorAsync(RequestAuthority authority, bool requireSurfaceUnlock, CancellationToken cancellationToken = default) => ValueTask.FromResult(AuthorizationDecision.Allow());
    }

    private sealed class AllowAuthorizationEvaluator : MediaEngine.Domain.Contracts.IAuthorizationEvaluator
    {
        public ValueTask<AuthorizationDecision> EvaluateAsync(RequestAuthority authority, AuthorizationRequirement requirement, ResourceAuthorizationContext? resource, CancellationToken cancellationToken = default) => ValueTask.FromResult(AuthorizationDecision.Allow());
    }

    /// <summary>Signs every request in; the <c>X-Test-Administrator</c> header decides whether it also counts as an administrator.</summary>
    private sealed class TestAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string AdministratorClaim = "test-administrator";
        public const string AdministratorHeader = "X-Test-Administrator";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, "batch-edit-test") };
            if (Request.Headers.ContainsKey(AdministratorHeader))
            {
                claims.Add(new Claim(AdministratorClaim, "true"));
            }

            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(
                new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name)), Scheme.Name)));
        }
    }

    private sealed class TestApplication(WebApplication app, Uri address) : IAsyncDisposable
    {
        public HttpClient CreateClient(bool administrator)
        {
            var client = new HttpClient { BaseAddress = address };
            if (administrator)
            {
                client.DefaultRequestHeaders.Add(TestAuthenticationHandler.AdministratorHeader, "1");
            }

            return client;
        }

        public async ValueTask DisposeAsync() { await app.StopAsync(); await app.DisposeAsync(); }
    }
}
