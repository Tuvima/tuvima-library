using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
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
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MediaEngine.Api.Tests;

/// <summary>
/// The supporting-files routes over HTTP with the real services, the real data store and the real resource check
/// (universal editor v5 WP4). The list needs the right to read the title; the bulk actions need an administrator and the right to edit it.
/// </summary>
public sealed class SupportingFileEndpointTests : IDisposable
{
    private readonly SupportingFileTestWorld _world = new();
    private readonly Guid _allowedLibrary = Guid.NewGuid();
    private readonly Guid _otherLibrary = Guid.NewGuid();

    public void Dispose() => _world.Dispose();

    [Fact]
    public async Task List_ForATitleTheCallerMayRead_ReturnsTheFilteredFiles()
    {
        await _world.SeedGeneratedFilesAsync();
        var title = SeedTitle(_allowedLibrary);
        await using var app = await StartAsync();
        using var client = app.CreateClient(administrator: false);

        using var response = await client.GetAsync($"/api/v1/supporting-files/titles/{title}?where=season:2&type=document&pageSize=5");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(9, json.RootElement.GetProperty("total").GetInt32());
        Assert.Equal(5, json.RootElement.GetProperty("items").GetArrayLength());
        Assert.Equal(2, json.RootElement.GetProperty("pageCount").GetInt32());
        Assert.Equal(SupportingFileTestWorld.GeneratedFiles, json.RootElement.GetProperty("totalAll").GetInt32());
    }

    [Fact]
    public async Task List_ForATitleInALibraryTheCallerCannotSee_AnswersNotFound()
    {
        await _world.SeedGeneratedFilesAsync();
        var title = SeedTitle(_otherLibrary);
        await using var app = await StartAsync();
        using var client = app.CreateClient(administrator: true);

        using var response = await client.GetAsync($"/api/v1/supporting-files/titles/{title}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task BulkUpdate_ByAProfileThatIsNotAnAdministrator_Answers403_AndChangesNothing()
    {
        await _world.SeedGeneratedFilesAsync();
        var title = SeedTitle(_allowedLibrary);
        var ids = (await _world.Extras.ListAllInItemFolderAsync(_world.Show)).Take(3).Select(row => row.Id).ToList();
        await using var app = await StartAsync();
        using var client = app.CreateClient(administrator: false);

        using var update = await client.PostAsync($"/api/v1/supporting-files/titles/{title}/bulk-update", Json(ids, """{"groupName":"Homework"}"""));
        using var delete = await client.PostAsync($"/api/v1/supporting-files/titles/{title}/bulk-delete", Json(ids, null));

        Assert.Equal(HttpStatusCode.Forbidden, update.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, delete.StatusCode);
        Assert.Empty(await _world.Extras.ListGroupsAsync(_world.Show));
        Assert.Equal(SupportingFileTestWorld.GeneratedFiles, (await _world.Extras.ListAllInItemFolderAsync(_world.Show)).Count);
    }

    [Fact]
    public async Task BulkUpdate_ForATitleInALibraryTheCallerCannotEdit_AnswersNotFound_AndChangesNothing()
    {
        await _world.SeedGeneratedFilesAsync();
        var title = SeedTitle(_otherLibrary);
        var ids = (await _world.Extras.ListAllInItemFolderAsync(_world.Show)).Take(3).Select(row => row.Id).ToList();
        await using var app = await StartAsync();
        using var client = app.CreateClient(administrator: true);

        using var response = await client.PostAsync($"/api/v1/supporting-files/titles/{title}/bulk-update", Json(ids, """{"groupName":"Homework"}"""));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(await _world.Extras.ListGroupsAsync(_world.Show));
    }

    [Fact]
    public async Task BulkUpdate_ByAnAdministrator_SetsTheGroupOfManyFilesInOneRequest()
    {
        await _world.SeedGeneratedFilesAsync();
        var title = SeedTitle(_allowedLibrary);
        var ids = (await _world.Extras.ListAllInItemFolderAsync(_world.Show)).Take(25).Select(row => row.Id).ToList();
        await using var app = await StartAsync();
        using var client = app.CreateClient(administrator: true);

        using var response = await client.PostAsync($"/api/v1/supporting-files/titles/{title}/bulk-update", Json(ids, """{"groupName":"Homework"}"""));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(25, json.RootElement.GetProperty("changed").GetInt32());
        Assert.Equal(25, json.RootElement.GetProperty("results").GetArrayLength());
        var group = Assert.Single(await _world.Extras.ListGroupsAsync(_world.Show));
        Assert.Equal("Homework", group.Name);
    }

    [Fact]
    public async Task BulkUpdate_WithARoleThatDoesNotFitTheFile_Answers400WithAPlainReason()
    {
        await _world.SeedGeneratedFilesAsync();
        var title = SeedTitle(_allowedLibrary);
        var worksheet = (await _world.Extras.GetByPathAsync(Path.Combine(_world.SeasonFolder(1), "worksheet-001.pdf")))!;
        await using var app = await StartAsync();
        using var client = app.CreateClient(administrator: true);

        using var response = await client.PostAsync($"/api/v1/supporting-files/titles/{title}/bulk-update", Json([worksheet.Id], """{"role":"trailer"}"""));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("is not available for this kind of file", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task BulkUpdate_WithNothingPicked_Answers400()
    {
        var title = SeedTitle(_allowedLibrary);
        await using var app = await StartAsync();
        using var client = app.CreateClient(administrator: true);

        using var response = await client.PostAsync($"/api/v1/supporting-files/titles/{title}/bulk-update", Json([], """{"groupName":"Homework"}"""));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task BulkDelete_ByAnAdministrator_RemovesTheFilesAndReportsWhatHappenedOnDisk()
    {
        var title = SeedTitle(_allowedLibrary);
        var one = await _world.SeedDiskFileAsync("one.pdf", "One");
        var keep = await _world.SeedDiskFileAsync("keep.pdf", "Keep");
        await using var app = await StartAsync();
        using var client = app.CreateClient(administrator: true);

        using var response = await client.PostAsync($"/api/v1/supporting-files/titles/{title}/bulk-delete", Json([one.Id], null));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(1, json.RootElement.GetProperty("removed").GetInt32());
        Assert.Equal("deleted", json.RootElement.GetProperty("results")[0].GetProperty("disk").GetString());
        Assert.False(File.Exists(one.FilePath));
        Assert.True(File.Exists(keep.FilePath));
    }

    // ── Harness ─────────────────────────────────────────────────────────────

    private static StringContent Json(IEnumerable<Guid> ids, string? changes)
    {
        var list = string.Join(",", ids.Select(id => $"\"{id:D}\""));
        var body = changes is null ? $$"""{"ids":[{{list}}]}""" : $$"""{"ids":[{{list}}],"changes":{{changes}}}""";
        return new StringContent(body, Encoding.UTF8, "application/json");
    }

    // A work with one media file in the given library: the title the routes are called for.
    private Guid SeedTitle(Guid libraryId)
    {
        var workId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        using var connection = _world.Database.CreateConnection();
        connection.Execute("INSERT INTO works (id, media_type) VALUES (@work, 'TV');", new { work = GuidSql.ToBlob(workId) });
        var edition = GuidSql.ToBlob(Guid.NewGuid());
        connection.Execute("INSERT INTO editions (id, work_id) VALUES (@edition, @work);", new { edition, work = GuidSql.ToBlob(workId) });
        connection.Execute(
            "INSERT INTO media_assets (id, edition_id, content_hash, file_path_root, library_id) VALUES (@asset, @edition, @hash, @path, @library);",
            new
            {
                asset = GuidSql.ToBlob(assetId),
                edition,
                hash = $"hash-{assetId:N}",
                path = Path.Combine(_world.Show, "Pilot.mkv"),
                library = libraryId.ToString("D"),
            });
        return workId;
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
        builder.Services.AddSingleton<IDatabaseConnection>(_world.Database);
        builder.Services.AddSingleton(_world.List);
        builder.Services.AddSingleton(_world.Bulk);
        builder.Services.AddSingleton<IProfileRepository>(new ProfileRepository(_world.Database));
        builder.Services.AddScoped<CatalogueResourceAuthorizationService>();
        builder.Services.AddSingleton<IRequestAuthorityResolver>(new FixedAuthorityResolver(authority));
        builder.Services.AddSingleton<IAccountAccessDecisionService>(new LibraryAccess(_allowedLibrary));
        builder.Services.AddSingleton<MediaEngine.Domain.Contracts.IAuthorizationEvaluator, AllowAuthorizationEvaluator>();

        var app = builder.Build();
        app.UseDeveloperExceptionPage();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapSupportingFileEndpoints();
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
            var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, "supporting-files-test") };
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
