using System.Net;
using System.Net.Http.Headers;
using MediaEngine.Web.Endpoints;
using MediaEngine.Web.Services.Integration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

public sealed class NativeApiForwarderTests
{
    [Theory]
    [InlineData("GET", "display/home", true, NativeApiForwardDecision.Forward)]
    [InlineData("GET", "devices/current", true, NativeApiForwardDecision.Forward)]
    [InlineData("DELETE", "devices/6f0c2a3e-0000-0000-0000-000000000000", true, NativeApiForwardDecision.Forward)]
    [InlineData("GET", "playback/6f0c2a3e-0000-0000-0000-000000000000/manifest", true, NativeApiForwardDecision.Forward)]
    [InlineData("GET", "stream/6f0c2a3e-0000-0000-0000-000000000000", true, NativeApiForwardDecision.Forward)]
    [InlineData("GET", "persons/6f0c2a3e-0000-0000-0000-000000000000/headshot", true, NativeApiForwardDecision.Forward)]
    [InlineData("GET", "library/portraits/x.jpg", true, NativeApiForwardDecision.Forward)]
    [InlineData("POST", "oauth/device_authorization", false, NativeApiForwardDecision.Forward)]
    [InlineData("POST", "oauth/token", false, NativeApiForwardDecision.Forward)]
    // No token: every action except starting pairing is refused locally.
    [InlineData("GET", "display/home", false, NativeApiForwardDecision.Unauthorized)]
    [InlineData("GET", "devices", false, NativeApiForwardDecision.Unauthorized)]
    // Not part of the app surface.
    [InlineData("GET", "oauth/token", false, NativeApiForwardDecision.NotFound)]
    [InlineData("GET", "pairing/review/ABCD", true, NativeApiForwardDecision.NotFound)]
    [InlineData("POST", "pairing/decision", true, NativeApiForwardDecision.NotFound)]
    [InlineData("GET", "analytics/playback", true, NativeApiForwardDecision.NotFound)]
    [InlineData("GET", "playback/sessions", true, NativeApiForwardDecision.NotFound)]
    [InlineData("GET", "playback/history", true, NativeApiForwardDecision.NotFound)]
    [InlineData("GET", "settings/network", true, NativeApiForwardDecision.NotFound)]
    [InlineData("GET", "health/ready", true, NativeApiForwardDecision.NotFound)]
    [InlineData("GET", "", true, NativeApiForwardDecision.NotFound)]
    // Attempts to climb out of /api/v1 or smuggle other routes.
    [InlineData("GET", "display/../../health/ready", true, NativeApiForwardDecision.NotFound)]
    [InlineData("GET", "display/./home", true, NativeApiForwardDecision.NotFound)]
    [InlineData("GET", "display\\..\\settings", true, NativeApiForwardDecision.NotFound)]
    [InlineData("GET", "display/%2e%2e/health", true, NativeApiForwardDecision.NotFound)]
    [InlineData("GET", "display//home", true, NativeApiForwardDecision.NotFound)]
    [InlineData("GET", "displayx/home", true, NativeApiForwardDecision.NotFound)]
    public void Policy_AllowsOnlyThePairedDeviceSurface(string method, string path, bool bearer, NativeApiForwardDecision expected) =>
        Assert.Equal(expected, NativeApiForwardPolicy.Evaluate(method, path, bearer));

    [Fact]
    public async Task SwitchOff_RefusesEveryAppDoorWithoutContactingTheEngine()
    {
        var engineHits = 0;
        await using var engine = await TestApplication.StartEngineAsync(engineHits: () => Interlocked.Increment(ref engineHits));
        await using var dashboard = await TestApplication.StartDashboardAsync(engine.Address, enabled: false);
        using var client = new HttpClient { BaseAddress = dashboard.Address };
        using var request = Bearer(HttpMethod.Get, "/api/v1/display/home");

        using var api = await client.SendAsync(request);
        using var token = await client.PostAsync("/api/v1/oauth/token", null);
        using var discovery = await client.GetAsync("/.well-known/tuvima");

        Assert.Equal(HttpStatusCode.NotFound, api.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, token.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, discovery.StatusCode);
        Assert.Equal(0, engineHits);
    }

    [Fact]
    public async Task MissingGate_FailsClosed()
    {
        await using var engine = await TestApplication.StartEngineAsync();
        await using var dashboard = await TestApplication.StartDashboardAsync(engine.Address, enabled: null);
        using var client = new HttpClient { BaseAddress = dashboard.Address };
        using var response = await client.SendAsync(Bearer(HttpMethod.Get, "/api/v1/display/home"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ConfigurationGate_IsOffUnlessExplicitlyEnabled()
    {
        static bool Read(string? value) => new ConfigurationNativeAppAccessGate(new ConfigurationBuilder()
            .AddInMemoryCollection(value is null ? [] : new Dictionary<string, string?> { ["NativeAppAccess:Enabled"] = value })
            .Build()).IsEnabled;

        Assert.False(Read(null));
        Assert.False(Read("false"));
        Assert.True(Read("true"));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task SwitchOn_ForwardsOnlyAllowListedPathsAndNeverLeaksDashboardCredentials()
    {
        var seen = new List<(string Path, string? Authorization, string? Cookie, string? ServiceKey)>();
        await using var engine = await TestApplication.StartEngineAsync(record: context =>
        {
            lock (seen)
            {
                seen.Add((context.Request.Path, context.Request.Headers.Authorization, context.Request.Headers.Cookie,
                    context.Request.Headers["X-Tuvima-Service-Key"]));
            }
        });
        await using var dashboard = await TestApplication.StartDashboardAsync(engine.Address, enabled: true);
        using var client = new HttpClient { BaseAddress = dashboard.Address };

        using var allowed = Bearer(HttpMethod.Get, "/api/v1/display/home");
        allowed.Headers.TryAddWithoutValidation("Cookie", "dashboard-session=secret");
        allowed.Headers.TryAddWithoutValidation("X-Tuvima-Service-Key", "dashboard-service-key");
        using var allowedResponse = await client.SendAsync(allowed);

        using var unpaired = await client.GetAsync("/api/v1/display/home");
        using var admin = await client.SendAsync(Bearer(HttpMethod.Get, "/api/v1/analytics/playback"));
        using var traversal = await client.SendAsync(Bearer(HttpMethod.Get, "/api/v1/display/%2e%2e/%2e%2e/health/ready"));
        using var pairingStart = await client.PostAsync("/api/v1/oauth/device_authorization", new StringContent("{}"));

        Assert.Equal(HttpStatusCode.OK, allowedResponse.StatusCode);
        Assert.False(allowedResponse.Headers.Contains("Set-Cookie"));
        Assert.Equal(HttpStatusCode.Unauthorized, unpaired.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, admin.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, traversal.StatusCode);
        Assert.Equal(HttpStatusCode.OK, pairingStart.StatusCode);

        lock (seen)
        {
            Assert.Equal(["/api/v1/display/home", "/api/v1/oauth/device_authorization"], seen.Select(s => s.Path).ToArray());
            Assert.Equal("Bearer app-token", seen[0].Authorization);
            Assert.All(seen, s =>
            {
                Assert.True(string.IsNullOrEmpty(s.Cookie));
                Assert.True(string.IsNullOrEmpty(s.ServiceKey));
            });
        }
    }

    [Theory]
    [InlineData("/application-events/%5C..%5C..%5Csystem%5Cstatus")]
    [InlineData("/application-events/..%5Chealth")]
    [InlineData("/application-events/%2e%2e/health")]
    [InlineData("/application-events/a//b")]
    public async Task EventsDoor_RefusesPathsThatEscapeTheHub(string path)
    {
        var engineHits = 0;
        await using var engine = await TestApplication.StartEngineAsync(engineHits: () => Interlocked.Increment(ref engineHits));
        await using var dashboard = await TestApplication.StartDashboardAsync(engine.Address, enabled: true);
        using var client = new HttpClient { BaseAddress = dashboard.Address };

        using var response = await client.SendAsync(Bearer(HttpMethod.Post, path));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(0, engineHits);
    }

    [Fact]
    public async Task EventsDoor_StillForwardsNormalNegotiation()
    {
        var seenPath = "";
        await using var engine = await TestApplication.StartEngineAsync(record: c => seenPath = c.Request.Path);
        await using var dashboard = await TestApplication.StartDashboardAsync(engine.Address, enabled: true);
        using var client = new HttpClient { BaseAddress = dashboard.Address };

        using var response = await client.SendAsync(Bearer(HttpMethod.Post, MediaEngine.Contracts.Authentication.ApplicationEventClientMethods.HubPath + "/negotiate"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(MediaEngine.Contracts.Authentication.ApplicationEventClientMethods.HubPath + "/negotiate", seenPath);
    }

    [Fact]
    public async Task SwitchOff_HidesThePairingPage()
    {
        await using var engine = await TestApplication.StartEngineAsync();
        await using var dashboard = await TestApplication.StartDashboardAsync(engine.Address, enabled: false);
        using var client = new HttpClient { BaseAddress = dashboard.Address };

        using var response = await client.GetAsync("/pair?user_code=ABCD");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ProxyClient_NeverReplaysEngineCookiesOrFollowsRedirects()
    {
        var cookies = new List<string>();
        var calls = 0;
        await using var engine = await TestApplication.StartEngineAsync(record: c =>
        {
            lock (cookies) { cookies.Add(c.Request.Headers.Cookie.ToString()); }
            if (Interlocked.Increment(ref calls) == 3)
            {
                c.Response.StatusCode = StatusCodes.Status302Found;
                c.Response.Headers.Location = "/health/ready";
            }
        });
        await using var dashboard = await TestApplication.StartDashboardAsync(engine.Address, enabled: true);
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = dashboard.Address };

        using var first = await client.SendAsync(Bearer(HttpMethod.Get, "/api/v1/display/home"));
        using var second = await client.SendAsync(Bearer(HttpMethod.Get, "/api/v1/display/home"));
        using var redirect = await client.SendAsync(Bearer(HttpMethod.Get, "/api/v1/display/home"));

        // The engine set a cookie on every reply; a second app must never have it sent back.
        Assert.All(cookies, cookie => Assert.True(string.IsNullOrEmpty(cookie)));
        Assert.Equal(HttpStatusCode.Redirect, redirect.StatusCode);
        Assert.Equal(3, calls);
    }

    private static HttpRequestMessage Bearer(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "app-token");
        return request;
    }

    private sealed class FixedGate(bool enabled) : INativeAppAccessGate
    {
        public bool IsEnabled => enabled;
    }

    private sealed class TestApplication(WebApplication app, Uri address) : IAsyncDisposable
    {
        public Uri Address { get; } = address;

        public static Task<TestApplication> StartEngineAsync(Action<HttpContext>? record = null, Action? engineHits = null) =>
            StartAsync(null, null, app => app.Run(context =>
            {
                engineHits?.Invoke();
                record?.Invoke(context);
                // A hostile Engine response must not be able to plant cookies on an app.
                context.Response.Headers.SetCookie = "engine=1";
                return Task.CompletedTask;
            }));

        public static Task<TestApplication> StartDashboardAsync(Uri engine, bool? enabled) =>
            StartAsync(engine, enabled, app => app.MapClientApiEdge());

        private static async Task<TestApplication> StartAsync(Uri? engine, bool? enabled, Action<WebApplication> configure)
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.AddAntiforgery();
            if (enabled is { } value)
            {
                builder.Services.AddSingleton<INativeAppAccessGate>(new FixedGate(value));
            }

            builder.Services.AddClientApiProxyClient(engine!);
            builder.Services.AddHttpClient("EngineIdentity", client => client.BaseAddress = engine);
            var app = builder.Build();
            app.UseWebSockets();
            configure(app);
            await app.StartAsync();
            var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>();
            return new(app, new Uri(addresses!.Addresses.Single()));
        }

        public async ValueTask DisposeAsync()
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
}
