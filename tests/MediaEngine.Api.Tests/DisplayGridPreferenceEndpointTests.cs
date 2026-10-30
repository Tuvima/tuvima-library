using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Dapper;
using MediaEngine.Api.Endpoints;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services.Display;
using MediaEngine.Contracts.Display;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Storage;
using MediaEngine.Storage.Contracts;
using Microsoft.AspNetCore.Authentication;
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
/// The real grid-preference routes over a real data store, with a signed-in person chosen per request by a test header.
/// The profile always comes from the caller's own claim; nothing in a request names another profile.
/// </summary>
public sealed class DisplayGridPreferenceEndpointTests : IAsyncLifetime
{
    private const string ProfileHeader = "X-Test-Profile";
    private const string KidsHeader = "X-Test-Kids";

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"tuvima-grid-routes-{Guid.NewGuid():N}.db");
    private DatabaseConnection _database = null!;
    private WebApplication _app = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        DapperConfiguration.Configure();
        _database = new DatabaseConnection(_path);
        _database.InitializeSchema();
        _database.RunStartupChecks();

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddAuthentication("test")
            .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>("test", _ => { });
        builder.Services.AddAuthorization(options =>
            options.AddPolicy(AuthPolicies.Authenticated, policy => policy.RequireAuthenticatedUser()));
        builder.Services.AddSingleton<IDatabaseConnection>(_database);
        builder.Services.AddSingleton<IProfileGridPreferenceRepository, ProfileGridPreferenceRepository>();
        builder.Services.AddSingleton<IProfileHeroSettingsRepository, ProfileHeroSettingsRepository>();
        builder.Services.AddSingleton<IHeroPluginSlides>(sp => new OnePluginSlides(sp.GetRequiredService<IProfileHeroSettingsRepository>()));
        builder.Services.AddSingleton<IRequestAuthorityResolver, HeaderAuthorityResolver>();
        builder.Services.AddSingleton<IAuthorizationEvaluator, AllowEvaluator>();
        _app = builder.Build();
        _app.UseAuthentication();
        _app.UseAuthorization();
        _app.MapDisplayGridPreferenceEndpoints();
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
        try { File.Delete(_path); }
        catch (IOException) { /* a temp database that is still locked is left for the OS to clean up */ }
    }

    [Fact]
    public async Task ASavedSort_IsRememberedForTheProfileThatSavedIt()
    {
        var alice = SeedProfile();

        using var saved = await Put(alice, "watch", "Title");
        Assert.Equal(HttpStatusCode.NoContent, saved.StatusCode);

        var preferences = await Get(alice);
        Assert.Equal("title", Assert.Single(preferences.Sorts).Value);
        Assert.Equal("title", preferences.Sorts["watch"]);
    }

    [Fact]
    public async Task APersonWithNothingSaved_GetsAnEmptyMap()
    {
        Assert.Empty((await Get(SeedProfile())).Sorts);
    }

    [Fact]
    public async Task AnUnknownGrid_IsRefusedAndNothingIsSaved()
    {
        var alice = SeedProfile();

        using var response = await Put(alice, "videos", "title");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty((await Get(alice)).Sorts);
    }

    [Fact]
    public async Task AnUnknownSort_IsRefusedAndNothingIsSaved()
    {
        var alice = SeedProfile();

        using var response = await Put(alice, "watch", "chaos");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty((await Get(alice)).Sorts);
    }

    [Fact]
    public async Task OneProfile_CannotReadOrChangeAnotherProfilesSorts()
    {
        var alice = SeedProfile();
        var bob = SeedProfile();

        (await Put(alice, "read", "year")).Dispose();

        Assert.Empty((await Get(bob)).Sorts);
        (await Put(bob, "read", "added")).Dispose();
        Assert.Equal("year", (await Get(alice)).Sorts["read"]);
        Assert.Equal("added", (await Get(bob)).Sorts["read"]);
    }

    [Fact]
    public async Task AKidsProfile_CanSaveItsOwnSort()
    {
        var kid = SeedProfile();

        using var saved = await Put(kid, "my-list", "played", kids: true);

        Assert.Equal(HttpStatusCode.NoContent, saved.StatusCode);
        Assert.Equal("played", (await Get(kid, kids: true)).Sorts["my-list"]);
    }

    [Fact]
    public async Task ACallWithNoActiveProfile_IsAnsweredAsUnauthenticated()
    {
        using var read = await Send(HttpMethod.Get, "/api/v1/display/grid-preferences", profile: null, body: null);
        using var write = await Send(HttpMethod.Put, "/api/v1/display/grid-preferences/watch", profile: null,
            body: new DisplayGridSortPreferenceRequest("title"));

        Assert.Equal(HttpStatusCode.Unauthorized, read.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, write.StatusCode);
    }

    [Fact]
    public async Task HeroSettings_StartAsTheDefaults_AndAreRememberedPerProfile()
    {
        var alice = SeedProfile();
        var bob = SeedProfile();

        Assert.Equal(new DisplayHeroSettingsDto(true, 5, true, true, true, false), await GetHero(alice) with { Plugins = [] });

        using var saved = await PutHero(alice, new DisplayHeroSettingsDto(true, 2, true, false, true, true));

        Assert.Equal(HttpStatusCode.NoContent, saved.StatusCode);
        Assert.Equal(new DisplayHeroSettingsDto(true, 2, true, false, true, true), await GetHero(alice) with { Plugins = [] });
        Assert.Equal(5, (await GetHero(bob)).SlideCount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    public async Task AHeroSlideCountOutsideOneToFive_IsRefused(int slides)
    {
        var alice = SeedProfile();

        using var response = await PutHero(alice, new DisplayHeroSettingsDto(true, slides, true, true, true, false));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(5, (await GetHero(alice)).SlideCount);
    }

    [Fact]
    public async Task HeroSettings_ListThePluginsThatOfferSlides_AndTheProfileCanSwitchOne()
    {
        var alice = SeedProfile();

        Assert.True(Assert.Single((await GetHero(alice)).Plugins).Enabled);

        using var off = await Send(HttpMethod.Put, "/api/v1/display/hero-settings/plugins/tuvima.mood", alice, body: null, rawBody: new DisplayHeroPluginSwitchRequest(false));
        Assert.Equal(HttpStatusCode.NoContent, off.StatusCode);
        Assert.False(Assert.Single((await GetHero(alice)).Plugins).Enabled);

        using var unknown = await Send(HttpMethod.Put, "/api/v1/display/hero-settings/plugins/not.a.plugin", alice, body: null, rawBody: new DisplayHeroPluginSwitchRequest(false));
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [Fact]
    public async Task HeroSettings_NeedAnActiveProfile()
    {
        using var read = await Send(HttpMethod.Get, "/api/v1/display/hero-settings", profile: null, body: null);
        using var write = await Send(HttpMethod.Put, "/api/v1/display/hero-settings", profile: null, body: null,
            rawBody: new DisplayHeroSettingsDto(true, 3, true, true, true, false));

        Assert.Equal(HttpStatusCode.Unauthorized, read.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, write.StatusCode);
    }

    private async Task<DisplayHeroSettingsDto> GetHero(Guid profile)
    {
        using var response = await Send(HttpMethod.Get, "/api/v1/display/hero-settings", profile, body: null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<DisplayHeroSettingsDto>())!;
    }

    private Task<HttpResponseMessage> PutHero(Guid profile, DisplayHeroSettingsDto settings) =>
        Send(HttpMethod.Put, "/api/v1/display/hero-settings", profile, body: null, rawBody: settings);

    private async Task<DisplayGridPreferencesDto> Get(Guid profile, bool kids = false)
    {
        using var response = await Send(HttpMethod.Get, "/api/v1/display/grid-preferences", profile, body: null, kids);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<DisplayGridPreferencesDto>())!;
    }

    private Task<HttpResponseMessage> Put(Guid profile, string grid, string sort, bool kids = false) =>
        Send(HttpMethod.Put, $"/api/v1/display/grid-preferences/{grid}", profile, new DisplayGridSortPreferenceRequest(sort), kids);

    private async Task<HttpResponseMessage> Send(
        HttpMethod method, string url, Guid? profile, DisplayGridSortPreferenceRequest? body, bool kids = false, object? rawBody = null)
    {
        using var request = new HttpRequestMessage(method, url);
        if (profile is { } id)
        {
            request.Headers.Add(ProfileHeader, id.ToString("D"));
        }

        if (kids)
        {
            request.Headers.Add(KidsHeader, "true");
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }
        else if (rawBody is not null)
        {
            request.Content = JsonContent.Create(rawBody, rawBody.GetType());
        }

        return await _client.SendAsync(request);
    }

    private Guid SeedProfile()
    {
        var id = Guid.NewGuid();
        using var connection = _database.CreateConnection();
        connection.Execute(
            "INSERT INTO profiles(id, display_name, role, created_at) VALUES (@id, @name, 'StandardUser', @createdAt);",
            new { id, name = "Person", createdAt = DateTimeOffset.UtcNow.ToString("O") });
        return id;
    }

    private sealed class TestAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, "grid-routes-test") };
            if (Request.Headers.TryGetValue(ProfileHeader, out var profile))
            {
                claims.Add(new Claim(TuvimaClaimTypes.ActiveProfileId, profile.ToString()));
            }

            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(
                new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name)), Scheme.Name)));
        }
    }

    // One plugin that offers slides; its on/off answer comes from the real switch table, so the test sees what the route wrote.
    private sealed class OnePluginSlides(IProfileHeroSettingsRepository settings) : IHeroPluginSlides
    {
        public Task<IReadOnlyList<HeroPluginNomination>> NominationsAsync(Guid? profileId, PluginAudience? requester, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<HeroPluginNomination>>([]);

        public async Task<IReadOnlyList<(string PluginId, string Name, bool Enabled)>> PluginsAsync(Guid profileId, CancellationToken ct) =>
            [("tuvima.mood", "Mood Match", !(await settings.GetSwitchedOffPluginsAsync(profileId, ct)).Contains("tuvima.mood"))];
    }

    private sealed class HeaderAuthorityResolver : IRequestAuthorityResolver
    {
        public ValueTask<RequestAuthority> ResolveAsync(HttpContext context, CancellationToken ct = default) =>
            ValueTask.FromResult(new RequestAuthority(
                PrincipalKind.Human, true, Guid.NewGuid(), Guid.NewGuid(), null, Guid.NewGuid(), null,
                AccountEnabled: true, ActiveProfileInUserHousehold: true,
                ActiveProfileIsRestricted: context.Request.Headers.ContainsKey(KidsHeader)));
    }

    private sealed class AllowEvaluator : IAuthorizationEvaluator
    {
        public ValueTask<AuthorizationDecision> EvaluateAsync(RequestAuthority authority,
            AuthorizationRequirement requirement, ResourceAuthorizationContext? resource,
            CancellationToken ct = default) => ValueTask.FromResult(AuthorizationDecision.Allow());
    }
}
