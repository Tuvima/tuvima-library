using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using MediaEngine.Web.Services.Integration;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MediaEngine.Web.Tests;

/// <summary>
/// PDF and comic reader spec WP2: <c>/engine-book/{id}/pages</c> and <c>/engine-book/{id}/pages/{n}?width=</c> carry a comic's
/// page list and page pictures from the Engine to the signed-in browser, and nothing else.
/// </summary>
public sealed class EngineComicPageProxyTests
{
    private static readonly Guid AssetId = Guid.Parse("40000000-0000-0000-0000-000000000004");
    private static readonly byte[] Picture = [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3];

    [Theory]
    [InlineData("/engine-book/40000000-0000-0000-0000-000000000004/pages", false, 0)]
    [InlineData("/engine-book/40000000-0000-0000-0000-000000000004/pages/3", true, 3)]
    [InlineData("/Engine-Book/40000000-0000-0000-0000-000000000004/PAGES/100000", true, 100000)]
    public void PagePaths_AcceptOnlyTheFixedShape(string path, bool withPage, int expectedPage)
    {
        Assert.True(EngineBookProxyPath.TryParsePagesPath(path, withPage, out var id, out var page));
        Assert.Equal(AssetId, id);
        Assert.Equal(expectedPage, page);
    }

    [Theory]
    [InlineData("/engine-book/40000000-0000-0000-0000-000000000004/pages/0", true)]
    [InlineData("/engine-book/40000000-0000-0000-0000-000000000004/pages/100001", true)]
    [InlineData("/engine-book/40000000-0000-0000-0000-000000000004/pages/-1", true)]
    [InlineData("/engine-book/40000000-0000-0000-0000-000000000004/pages/1.5", true)]
    [InlineData("/engine-book/40000000-0000-0000-0000-000000000004/pages/01234567", true)]
    [InlineData("/engine-book/40000000-0000-0000-0000-000000000004/pages/", true)]
    [InlineData("/engine-book/40000000-0000-0000-0000-000000000004/pages/1/extra", true)]
    [InlineData("/engine-book/40000000-0000-0000-0000-000000000004/pages/%31", true)]
    [InlineData("/engine-book/40000000-0000-0000-0000-000000000004/pages/1", false)]
    [InlineData("/engine-book/40000000-0000-0000-0000-000000000004/pages", true)]
    [InlineData("/engine-book/40000000-0000-0000-0000-000000000004/pages/", false)]
    [InlineData("/engine-book/00000000-0000-0000-0000-000000000000/pages", false)]
    [InlineData("/engine-book/40000000-0000-0000-0000-000000000004/page", false)]
    [InlineData("/engine-book/40000000-0000-0000-0000-000000000004/pages\\1", true)]
    [InlineData(null, false)]
    public void PagePaths_RejectEverythingElse(string? path, bool withPage)
    {
        Assert.False(EngineBookProxyPath.TryParsePagesPath(path, withPage, out _, out _));
    }

    [Fact]
    public async Task List_IsCarriedThroughForASignedInPerson()
    {
        await using var engine = await StartEngineAsync();
        await using var dashboard = await StartDashboardAsync(engine.Address);

        using var response = await dashboard.Client.SendAsync(Signed(EngineBookProxyPath.ToPagesBrowserUrl(AssetId)));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("{\"page_count\":1}", await response.Content.ReadAsStringAsync());
        Assert.Equal($"/read/{AssetId:D}/pages", Assert.Single(engine.Seen));
    }

    [Fact]
    public async Task Page_IsCarriedAtAnAllowedWidthAndMayBeKeptBrieflyByTheBrowser()
    {
        await using var engine = await StartEngineAsync();
        await using var dashboard = await StartDashboardAsync(engine.Address);

        using var response = await dashboard.Client.SendAsync(Signed(EngineBookProxyPath.ToPageBrowserUrl(AssetId, 3, 720)));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(Picture, await response.Content.ReadAsByteArrayAsync());
        Assert.Equal("image/jpeg", response.Content.Headers.ContentType?.MediaType);
        Assert.True(response.Headers.CacheControl?.Private == true && response.Headers.CacheControl.MaxAge == TimeSpan.FromHours(1));
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
        Assert.Equal($"/read/{AssetId:D}/pages/3?width=720", Assert.Single(engine.Seen));
    }

    [Theory]
    [InlineData("?width=99999")]
    [InlineData("?width=-5")]
    [InlineData("?width=abc")]
    [InlineData("?width=100&width=200")]
    [InlineData("?width=100&path=/auth/sessions")]
    [InlineData("?other=1")]
    public async Task Page_WithAnyOtherQuery_IsNotFoundAndNeverReachesTheEngine(string query)
    {
        await using var engine = await StartEngineAsync();
        await using var dashboard = await StartDashboardAsync(engine.Address);

        using var response = await dashboard.Client.SendAsync(Signed($"/engine-book/{AssetId:D}/pages/1{query}"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(engine.Seen);
    }

    [Fact]
    public async Task List_WithAQuery_IsNotFoundAndNeverReachesTheEngine()
    {
        await using var engine = await StartEngineAsync();
        await using var dashboard = await StartDashboardAsync(engine.Address);

        using var response = await dashboard.Client.SendAsync(Signed($"/engine-book/{AssetId:D}/pages?x=1"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(engine.Seen);
    }

    [Fact]
    public async Task AnonymousRequests_AreRefusedBeforeTheEngineIsCalled()
    {
        await using var engine = await StartEngineAsync();
        await using var dashboard = await StartDashboardAsync(engine.Address);

        using var list = await dashboard.Client.GetAsync(EngineBookProxyPath.ToPagesBrowserUrl(AssetId));
        using var page = await dashboard.Client.GetAsync(EngineBookProxyPath.ToPageBrowserUrl(AssetId, 1));

        Assert.NotEqual(HttpStatusCode.OK, list.StatusCode);
        Assert.NotEqual(HttpStatusCode.OK, page.StatusCode);
        Assert.Empty(engine.Seen);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, 404)]
    [InlineData(HttpStatusCode.NotFound, 404)]
    [InlineData(HttpStatusCode.UnprocessableEntity, 422)]
    [InlineData(HttpStatusCode.TooManyRequests, 429)]
    [InlineData(HttpStatusCode.InternalServerError, 502)]
    public async Task EngineRefusals_ReadTheSameWayAsForTheBookFile(HttpStatusCode engineStatus, int expected)
    {
        await using var engine = await StartEngineAsync(engineStatus);
        await using var dashboard = await StartDashboardAsync(engine.Address);

        using var response = await dashboard.Client.SendAsync(Signed(EngineBookProxyPath.ToPageBrowserUrl(AssetId, 1)));

        Assert.Equal(expected, (int)response.StatusCode);
    }

    private static HttpRequestMessage Signed(string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("X-Test-Signed-In", "1");
        return request;
    }

    private static async Task<Dashboard> StartDashboardAsync(Uri engine)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddAuthentication("test")
            .AddScheme<AuthenticationSchemeOptions, SignedInHandler>("test", _ => { });
        builder.Services.AddAuthorization();
        builder.Services.AddHttpClient("EngineApi", client => client.BaseAddress = engine);
        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapEngineBookProxy();
        await app.StartAsync();
        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>();
        return new Dashboard(app, new HttpClient { BaseAddress = new Uri(addresses!.Addresses.Single()) });
    }

    private static async Task<Engine> StartEngineAsync(HttpStatusCode? forced = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        var app = builder.Build();
        var seen = new List<string>();
        IResult Answer(HttpContext context, Func<IResult> ok)
        {
            lock (seen)
            {
                seen.Add(context.Request.Path + context.Request.QueryString);
            }

            return forced is { } status ? Results.StatusCode((int)status) : ok();
        }

        app.MapGet("/read/{assetId:guid}/pages", (Guid assetId, HttpContext context) =>
            Answer(context, () => Results.Text("{\"page_count\":1}", "application/json")));
        app.MapGet("/read/{assetId:guid}/pages/{page:int}", (Guid assetId, int page, HttpContext context) =>
            Answer(context, () => Results.File(Picture, "image/jpeg")));
        await app.StartAsync();
        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>();
        return new Engine(app, new Uri(addresses!.Addresses.Single()), seen);
    }

    private sealed class SignedInHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.ContainsKey("X-Test-Signed-In"))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var claims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, "reader"),
                new(DashboardEngineAuthenticationHandler.SessionTokenClaim, "session"),
            };
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(
                new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name)), Scheme.Name)));
        }
    }

    private sealed class Engine(WebApplication app, Uri address, List<string> seen) : IAsyncDisposable
    {
        public Uri Address { get; } = address;

        public IReadOnlyList<string> Seen
        {
            get
            {
                lock (seen)
                {
                    return seen.ToList();
                }
            }
        }

        public ValueTask DisposeAsync() => app.DisposeAsync();
    }

    private sealed class Dashboard(WebApplication app, HttpClient client) : IAsyncDisposable
    {
        public HttpClient Client { get; } = client;

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await app.DisposeAsync();
        }
    }
}
