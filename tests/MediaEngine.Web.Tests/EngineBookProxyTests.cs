using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Web.Services.Integration;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MediaEngine.Web.Tests;

/// <summary>
/// <c>/engine-book/{assetId}/file</c> carries a book file from the Engine to the signed-in browser.
/// These run the real route, a real Kestrel host and a stand-in Engine that honours byte ranges.
/// </summary>
public sealed class EngineBookProxyTests
{
    private static readonly Guid AssetId = Guid.Parse("40000000-0000-0000-0000-000000000004");
    private static readonly byte[] Bytes = Enumerable.Range(0, 1000).Select(value => (byte)(value % 251)).ToArray();

    [Theory]
    [InlineData("/engine-book/40000000-0000-0000-0000-000000000004/file")]
    [InlineData("/Engine-Book/40000000-0000-0000-0000-000000000004/FILE")]
    public void Path_AcceptsOnlyTheFixedShape(string path)
    {
        Assert.True(EngineBookProxyPath.TryParseBrowserPath(path, out var id));
        Assert.Equal(AssetId, id);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("/engine-book/40000000-0000-0000-0000-000000000004")]
    [InlineData("/engine-book/40000000-0000-0000-0000-000000000004/")]
    [InlineData("/engine-book/40000000-0000-0000-0000-000000000004/file/")]
    [InlineData("/engine-book/40000000-0000-0000-0000-000000000004/file/extra")]
    [InlineData("/engine-book/40000000-0000-0000-0000-000000000004/files")]
    [InlineData("/engine-book/40000000-0000-0000-0000-000000000004/cover")]
    [InlineData("/engine-book/40000000-0000-0000-0000-000000000004/file?x=1")]
    [InlineData("/engine-book/40000000-0000-0000-0000-000000000004/file#frag")]
    [InlineData("/engine-book/40000000-0000-0000-0000-000000000004/%66ile")]
    [InlineData("/engine-book/40000000-0000-0000-0000-000000000004\\file")]
    [InlineData("/engine-book/40000000000000000000000000000004/file")]
    [InlineData("/engine-book/{40000000-0000-0000-0000-000000000004}/file")]
    [InlineData("/engine-book/00000000-0000-0000-0000-000000000000/file")]
    [InlineData("/engine-book/not-a-guid/file")]
    [InlineData("/engine-book/../auth/sessions/file")]
    [InlineData("/engine-book//40000000-0000-0000-0000-000000000004/file")]
    [InlineData("engine-book/40000000-0000-0000-0000-000000000004/file")]
    [InlineData("/engine-image/40000000-0000-0000-0000-000000000004/file")]
    [InlineData("/read/40000000-0000-0000-0000-000000000004/file")]
    public void Path_RejectsEverythingElse(string? path)
    {
        Assert.False(EngineBookProxyPath.TryParseBrowserPath(path, out var id));
        Assert.Equal(Guid.Empty, id);
    }

    [Fact]
    public void UrlsAreBuiltFromACanonicalId()
    {
        Assert.Equal("/engine-book/40000000-0000-0000-0000-000000000004/file", EngineBookProxyPath.ToBrowserUrl(AssetId));
        Assert.Equal("/read/40000000-0000-0000-0000-000000000004/file", EngineBookProxyPath.ToEnginePath(AssetId));
        Assert.True(EngineBookProxyPath.TryParseBrowserPath(EngineBookProxyPath.ToBrowserUrl(AssetId), out var id));
        Assert.Equal(AssetId, id);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, 200)]
    [InlineData(HttpStatusCode.PartialContent, 206)]
    [InlineData(HttpStatusCode.NotModified, 304)]
    [InlineData(HttpStatusCode.RequestedRangeNotSatisfiable, 416)]
    [InlineData(HttpStatusCode.Unauthorized, 401)]
    [InlineData(HttpStatusCode.TooManyRequests, 429)]
    [InlineData(HttpStatusCode.Forbidden, 404)]
    [InlineData(HttpStatusCode.NotFound, 404)]
    [InlineData(HttpStatusCode.InternalServerError, 502)]
    [InlineData(HttpStatusCode.BadRequest, 502)]
    [InlineData(HttpStatusCode.Redirect, 502)]
    public void EngineAnswers_AreMappedToWhatABrowserMayLearn(HttpStatusCode engine, int browser)
    {
        Assert.Equal(browser, EngineBookProxyEndpoint.MapStatus(engine));
    }

    [Fact]
    public void Program_MapsTheBookProxyAndNothingBroaderForBooks()
    {
        var program = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "MediaEngine.Web", "Program.cs"));

        Assert.Contains("app.MapEngineBookProxy();", program, StringComparison.Ordinal);
        Assert.DoesNotContain("/read/", program, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ByteRange_ThroughTheProxy_Returns206WithExactBytes()
    {
        await using var engine = await StartEngineAsync();
        await using var dashboard = await StartDashboardAsync(engine.Address);

        using var response = await dashboard.SendAsync(Request(new RangeHeaderValue(0, 99)));

        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal(new ContentRangeHeaderValue(0, 99, Bytes.Length), response.Content.Headers.ContentRange);
        Assert.Equal(Bytes[..100], await response.Content.ReadAsByteArrayAsync());
        Assert.Contains("bytes", response.Headers.AcceptRanges);
        Assert.Equal("application/epub+zip", response.Content.Headers.ContentType?.MediaType);
        Assert.NotNull(response.Headers.ETag);
        Assert.NotNull(response.Content.Headers.LastModified);
        Assert.Equal("bytes=0-99", Assert.Single(engine.Requests).Range);
    }

    [Fact]
    public async Task Response_IsPrivateNoCacheSandboxedAndLeaksNothingElseFromTheEngine()
    {
        await using var engine = await StartEngineAsync(onResponse: context =>
        {
            context.Response.Headers["Set-Cookie"] = "engine=secret";
            context.Response.Headers["X-Engine-Internal"] = "node-7";
            context.Response.Headers["Cache-Control"] = "public, max-age=31536000";
        });
        await using var dashboard = await StartDashboardAsync(engine.Address);

        using var response = await dashboard.SendAsync(Request(null));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("private, no-cache", response.Headers.CacheControl?.ToString());
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
        Assert.Equal("default-src 'none'; sandbox", Assert.Single(response.Headers.GetValues("Content-Security-Policy")));
        Assert.False(response.Headers.Contains("Set-Cookie"));
        Assert.False(response.Headers.Contains("X-Engine-Internal"));
        Assert.Equal(Bytes, await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task OnlyRangeAndValidatorsAreForwarded_NeverCookiesOrCredentials()
    {
        await using var engine = await StartEngineAsync();
        await using var dashboard = await StartDashboardAsync(engine.Address);
        var request = Request(new RangeHeaderValue(10, 19));
        request.Headers.IfRange = new RangeConditionHeaderValue(new EntityTagHeaderValue("\"abc\""));
        request.Headers.IfNoneMatch.Add(new EntityTagHeaderValue("\"abc\""));
        request.Headers.TryAddWithoutValidation("Cookie", "session=browser-secret");
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer browser-token");
        request.Headers.TryAddWithoutValidation("X-Api-Key", "browser-supplied");

        using var response = await dashboard.SendAsync(request);

        var seen = Assert.Single(engine.Requests);
        Assert.Equal("bytes=10-19", seen.Range);
        Assert.Equal("\"abc\"", seen.IfRange);
        Assert.Equal("\"abc\"", seen.IfNoneMatch);
        Assert.Null(seen.Cookie);
        Assert.Null(seen.Authorization);
        Assert.Null(seen.ApiKey);
    }

    [Fact]
    public async Task IfRangeAndNotModified_PassThroughToTheEngineDecision()
    {
        await using var engine = await StartEngineAsync();
        await using var dashboard = await StartDashboardAsync(engine.Address);
        using var first = await dashboard.SendAsync(Request(null));
        var etag = first.Headers.ETag!;

        var current = Request(new RangeHeaderValue(0, 9));
        current.Headers.IfRange = new RangeConditionHeaderValue(etag);
        using var partial = await dashboard.SendAsync(current);

        var stale = Request(new RangeHeaderValue(0, 9));
        stale.Headers.IfRange = new RangeConditionHeaderValue(new EntityTagHeaderValue("\"old\""));
        using var whole = await dashboard.SendAsync(stale);

        var conditional = Request(null);
        conditional.Headers.IfNoneMatch.Add(etag);
        using var notModified = await dashboard.SendAsync(conditional);

        var beyond = Request(new RangeHeaderValue(5000, 6000));
        using var unsatisfiable = await dashboard.SendAsync(beyond);

        Assert.Equal(HttpStatusCode.PartialContent, partial.StatusCode);
        Assert.Equal(HttpStatusCode.OK, whole.StatusCode);
        Assert.Equal(HttpStatusCode.NotModified, notModified.StatusCode);
        Assert.Empty(await notModified.Content.ReadAsByteArrayAsync());
        Assert.Equal(HttpStatusCode.RequestedRangeNotSatisfiable, unsatisfiable.StatusCode);
        Assert.Equal(Bytes.Length, unsatisfiable.Content.Headers.ContentRange?.Length);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.NotFound, HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError, HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.TooManyRequests, HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized)]
    public async Task RefusedOrFailedEngineAnswers_NeverRevealMoreThanTheMappedStatus(
        HttpStatusCode engineStatus, HttpStatusCode expected)
    {
        await using var engine = await StartEngineAsync(forcedStatus: engineStatus);
        await using var dashboard = await StartDashboardAsync(engine.Address);

        using var response = await dashboard.SendAsync(Request(new RangeHeaderValue(0, 99)));

        Assert.Equal(expected, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
        Assert.Null(response.Content.Headers.ContentRange);
    }

    [Fact]
    public async Task AnUnreachableEngine_IsABadGatewayNotACrash()
    {
        await using var dashboard = await StartDashboardAsync(new Uri("http://127.0.0.1:1"));

        using var response = await dashboard.SendAsync(Request(null));

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
    }

    [Fact]
    public async Task ASignedOutBrowser_ReachesNothing_AndTheEngineIsNeverCalled()
    {
        await using var engine = await StartEngineAsync();
        await using var dashboard = await StartDashboardAsync(engine.Address);
        var request = Request(new RangeHeaderValue(0, 99));
        request.Headers.Remove(SignedInHeader);

        using var response = await dashboard.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(engine.Requests);
    }

    [Theory]
    [InlineData("/engine-book/40000000-0000-0000-0000-000000000004/cover")]
    [InlineData("/engine-book/40000000-0000-0000-0000-000000000004/file/extra")]
    [InlineData("/engine-book/40000000-0000-0000-0000-000000000004/../../auth/sessions")]
    [InlineData("/engine-book/40000000000000000000000000000004/file")]
    [InlineData("/engine-book/%7B40000000-0000-0000-0000-000000000004%7D/file")]
    [InlineData("/engine-book/not-a-guid/file")]
    public async Task AnyOtherPath_Is404_AndTheEngineIsNeverCalled(string path)
    {
        await using var engine = await StartEngineAsync();
        await using var dashboard = await StartDashboardAsync(engine.Address);
        var request = Request(null);
        request.RequestUri = new Uri(path, UriKind.Relative);

        using var response = await dashboard.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(engine.Requests);
    }

    [Fact]
    public async Task QueryText_IsNeverForwardedToTheEngine()
    {
        await using var engine = await StartEngineAsync();
        await using var dashboard = await StartDashboardAsync(engine.Address);
        var request = Request(null);
        request.RequestUri = new Uri($"/engine-book/{AssetId:D}/file?path=/auth/sessions&assetId={Guid.NewGuid():D}", UriKind.Relative);

        using var response = await dashboard.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal($"/read/{AssetId:D}/file", Assert.Single(engine.Requests).PathAndQuery);
    }

    [Fact]
    public async Task OnlyGetIsAnswered()
    {
        await using var engine = await StartEngineAsync();
        await using var dashboard = await StartDashboardAsync(engine.Address);
        var request = Request(null);
        request.Method = HttpMethod.Post;

        using var response = await dashboard.SendAsync(request);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        Assert.Empty(engine.Requests);
    }

    [Fact]
    public async Task RealClientChain_SendsTheSessionFromTheCookieClaimAndTheServiceCredential()
    {
        await using var engine = await StartEngineAsync();
        await using var dashboard = await StartDashboardAsync(engine.Address, realHandlers: true);

        using var response = await dashboard.SendAsync(Request(new RangeHeaderValue(0, 99)));

        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        var seen = Assert.Single(engine.Requests);
        Assert.Equal(SessionTokenFromClaim, seen.Session);
        Assert.Equal(ServiceSecret, seen.ServiceKey);
    }

    [Fact]
    public async Task RealClientChain_NeverForwardsBrowserSuppliedSessionOrServiceKey()
    {
        await using var engine = await StartEngineAsync();
        await using var dashboard = await StartDashboardAsync(engine.Address, realHandlers: true);
        var request = Request(null);
        request.Headers.TryAddWithoutValidation("X-Tuvima-Session", "browser-session");
        request.Headers.TryAddWithoutValidation("X-Tuvima-Service-Key", "browser-service-key");
        request.Headers.TryAddWithoutValidation(ViewProfileAssertionHandler.ProfileHeader, Guid.NewGuid().ToString("D"));

        using var response = await dashboard.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var seen = Assert.Single(engine.Requests);
        Assert.Equal(SessionTokenFromClaim, seen.Session);
        Assert.Equal(ServiceSecret, seen.ServiceKey);
    }

    [Fact]
    public async Task ASignedInCookieWithoutAnEngineSession_IsTurnedAwayBeforeTheEngineIsCalled()
    {
        await using var engine = await StartEngineAsync();
        await using var dashboard = await StartDashboardAsync(engine.Address, realHandlers: true);
        var request = Request(null);
        request.Headers.Remove(SignedInHeader);
        request.Headers.TryAddWithoutValidation(SignedInHeader, NoSessionClaim);

        using var response = await dashboard.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
        Assert.Empty(engine.Requests);
    }

    [Fact]
    public async Task AnUnavailableServiceCredential_FailsClosedWithoutReachingTheEngine()
    {
        await using var engine = await StartEngineAsync();
        await using var dashboard = await StartDashboardAsync(engine.Address, realHandlers: true, writeCredential: false);

        using var response = await dashboard.SendAsync(Request(null));

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
        Assert.Empty(engine.Requests);
    }

    private const string SignedInHeader = "X-Test-Signed-In";
    private const string NoSessionClaim = "no-session";
    private const string SessionTokenFromClaim = "session-token-from-claim";
    private const string ServiceSecret = "dashboard-service-secret";

    private static HttpRequestMessage Request(RangeHeaderValue? range)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, EngineBookProxyPath.ToBrowserUrl(AssetId));
        request.Headers.TryAddWithoutValidation(SignedInHeader, "1");
        request.Headers.Range = range;
        return request;
    }

    /// <param name="realHandlers">
    /// Wires the production Engine client chain (service credential from a protected bundle on disk,
    /// session from the signed-in cookie's claim, obsolete View assertions stripped) instead of a bare client.
    /// </param>
    /// <param name="writeCredential">With <paramref name="realHandlers"/>: false leaves the credential bundle missing.</param>
    private static async Task<Dashboard> StartDashboardAsync(Uri engine, bool realHandlers = false, bool writeCredential = true)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddAuthentication("test")
            .AddScheme<AuthenticationSchemeOptions, SignedInHandler>("test", _ => { });
        builder.Services.AddAuthorization();
        string? tempDirectory = null;
        if (realHandlers)
        {
            tempDirectory = Path.Combine(Path.GetTempPath(), $"tuvima-book-proxy-{Guid.NewGuid():N}");
            var configDirectory = Path.Combine(tempDirectory, "config");
            var protection = DataProtectionProvider.Create(Directory.CreateDirectory(Path.Combine(tempDirectory, "keys")));
            if (writeCredential)
            {
                Directory.CreateDirectory(Path.Combine(configDirectory, ".secrets"));
                var bundle = new DashboardServiceCredentialBundle(
                    Guid.NewGuid().ToString("N"),
                    protection.CreateProtector("Tuvima.DashboardEngineCredential.v1").Protect(ServiceSecret),
                    DateTimeOffset.UtcNow);
                File.WriteAllText(
                    Path.Combine(configDirectory, ".secrets", "dashboard-engine.credential.json"),
                    JsonSerializer.Serialize(bundle));
            }

            builder.Services.AddHttpContextAccessor();
            builder.Services.AddSingleton(protection);
            builder.Services.AddSingleton(new DashboardServiceCredentialProviderOptions(configDirectory));
            builder.Services.AddSingleton<DashboardServiceCredentialProvider>();
            builder.Services.AddScoped<DashboardSessionAccessor>();
            builder.Services.AddScoped<ActiveProfileAccessor>();
            builder.Services.AddScoped<IActiveProfileAccessor>(services => services.GetRequiredService<ActiveProfileAccessor>());
            builder.Services.AddTransient<DashboardEngineAuthenticationHandler>();
            builder.Services.AddTransient<ViewProfileAssertionHandler>(services => new ViewProfileAssertionHandler(
                services.GetRequiredService<IActiveProfileAccessor>()));
            builder.Services.AddHttpClient("EngineApi", client => client.BaseAddress = engine)
                .AddHttpMessageHandler<DashboardEngineAuthenticationHandler>()
                .AddHttpMessageHandler<ViewProfileAssertionHandler>();
        }
        else
        {
            builder.Services.AddHttpClient("EngineApi", client => client.BaseAddress = engine);
        }

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapEngineBookProxy();
        await app.StartAsync();
        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>();
        return new Dashboard(app, new Uri(addresses!.Addresses.Single()), tempDirectory);
    }

    private static async Task<Engine> StartEngineAsync(
        HttpStatusCode? forcedStatus = null,
        Action<HttpContext>? onResponse = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        var app = builder.Build();
        var seen = new List<SeenRequest>();
        var lastModified = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
        var tag = new Microsoft.Net.Http.Headers.EntityTagHeaderValue("\"engine-v1\"");
        app.MapGet("/read/{assetId:guid}/file", (Guid assetId, HttpContext context) =>
        {
            lock (seen)
            {
                seen.Add(new SeenRequest(
                    context.Request.Path + context.Request.QueryString,
                    context.Request.Headers.Range.ToString().NullIfEmpty(),
                    context.Request.Headers.IfRange.ToString().NullIfEmpty(),
                    context.Request.Headers.IfNoneMatch.ToString().NullIfEmpty(),
                    context.Request.Headers.Cookie.ToString().NullIfEmpty(),
                    context.Request.Headers.Authorization.ToString().NullIfEmpty(),
                    context.Request.Headers["X-Api-Key"].ToString().NullIfEmpty(),
                    context.Request.Headers["X-Tuvima-Service-Key"].ToString().NullIfEmpty(),
                    context.Request.Headers["X-Tuvima-Session"].ToString().NullIfEmpty()));
            }

            if (forcedStatus is { } status)
            {
                return Results.StatusCode((int)status);
            }

            onResponse?.Invoke(context);
            return Results.File(
                Bytes,
                "application/epub+zip",
                fileDownloadName: null,
                lastModified: lastModified,
                entityTag: tag,
                enableRangeProcessing: true);
        });
        await app.StartAsync();
        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>();
        return new Engine(app, new Uri(addresses!.Addresses.Single()), seen);
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MediaEngine.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }

    private sealed record SeenRequest(
        string PathAndQuery,
        string? Range,
        string? IfRange,
        string? IfNoneMatch,
        string? Cookie,
        string? Authorization,
        string? ApiKey,
        string? ServiceKey,
        string? Session);

    private sealed class SignedInHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue(SignedInHeader, out var mode))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            // "no-session" is a signed-in person whose cookie carries no Engine session token.
            var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, "reader") };
            if (mode != NoSessionClaim)
            {
                claims.Add(new Claim(DashboardEngineAuthenticationHandler.SessionTokenClaim, SessionTokenFromClaim));
            }

            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(
                new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name)), Scheme.Name)));
        }
    }

    private sealed class Engine(WebApplication app, Uri address, List<SeenRequest> seen) : IAsyncDisposable
    {
        public Uri Address { get; } = address;

        public IReadOnlyList<SeenRequest> Requests
        {
            get
            {
                lock (seen)
                {
                    return seen.ToArray();
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    private sealed class Dashboard(WebApplication app, Uri address, string? tempDirectory) : IAsyncDisposable
    {
        private readonly HttpClient _client = new() { BaseAddress = address };

        public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request) => _client.SendAsync(request);

        public async ValueTask DisposeAsync()
        {
            _client.Dispose();
            await app.StopAsync();
            await app.DisposeAsync();
            if (tempDirectory is not null && Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }
}

internal static class EngineBookProxyTestStrings
{
    public static string? NullIfEmpty(this string value) => value.Length == 0 ? null : value;
}
