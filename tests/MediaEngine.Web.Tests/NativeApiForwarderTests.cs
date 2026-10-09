using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Web.Endpoints;
using MediaEngine.Web.Services.Configuration;
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
    // Paired-app writes that belong to the device's own profile.
    [InlineData("PUT", "progress/6f0c2a3e-0000-0000-0000-000000000000", true, NativeApiForwardDecision.Forward)]
    [InlineData("PUT", "profile-state/saved/work/6f0c2a3e-0000-0000-0000-000000000000", true, NativeApiForwardDecision.Forward)]
    [InlineData("POST", "player/command", true, NativeApiForwardDecision.Forward)]
    [InlineData("PUT", "devices/current/capabilities", true, NativeApiForwardDecision.Forward)]
    [InlineData("POST", "stream/6f0c2a3e-0000-0000-0000-000000000000/text-tracks/6f0c2a3e-0000-0000-0000-000000000000/preferred", true, NativeApiForwardDecision.Forward)]
    [InlineData("POST", "playback/6f0c2a3e-0000-0000-0000-000000000000/encode", true, NativeApiForwardDecision.Forward)]
    [InlineData("GET", "player/audiobooks/6f0c2a3e-0000-0000-0000-000000000000/chapter-overrides", true, NativeApiForwardDecision.Forward)]
    // Administrator-level edits stay out of reach, even for a paired administrator's phone.
    [InlineData("GET", "persons/6f0c2a3e-0000-0000-0000-000000000000/editor", true, NativeApiForwardDecision.NotFound)]
    [InlineData("PUT", "persons/6f0c2a3e-0000-0000-0000-000000000000/editor", true, NativeApiForwardDecision.NotFound)]
    [InlineData("POST", "persons/6f0c2a3e-0000-0000-0000-000000000000/artwork/headshot", true, NativeApiForwardDecision.NotFound)]
    [InlineData("POST", "display/artwork/entities/work/6f0c2a3e-0000-0000-0000-000000000000/from-url", true, NativeApiForwardDecision.NotFound)]
    [InlineData("POST", "display/artwork/entities/work/6f0c2a3e-0000-0000-0000-000000000000/upload", true, NativeApiForwardDecision.NotFound)]
    [InlineData("POST", "display/artwork/entities/work/6f0c2a3e-0000-0000-0000-000000000000/links", true, NativeApiForwardDecision.NotFound)]
    [InlineData("DELETE", "display/artwork/links/6f0c2a3e-0000-0000-0000-000000000000", true, NativeApiForwardDecision.NotFound)]
    [InlineData("PUT", "details/work/6f0c2a3e-0000-0000-0000-000000000000/sequence-default", true, NativeApiForwardDecision.NotFound)]
    [InlineData("POST", "player/audiobooks/6f0c2a3e-0000-0000-0000-000000000000/chapter-overrides", true, NativeApiForwardDecision.NotFound)]
    [InlineData("DELETE", "player/audiobooks/6f0c2a3e-0000-0000-0000-000000000000/chapter-overrides/6f0c2a3e-0000-0000-0000-000000000000/3", true, NativeApiForwardDecision.NotFound)]
    [InlineData("GET", "playback/diagnostics", true, NativeApiForwardDecision.NotFound)]
    [InlineData("POST", "stream/6f0c2a3e-0000-0000-0000-000000000000/text-tracks/import", true, NativeApiForwardDecision.NotFound)]
    [InlineData("POST", "stream/6f0c2a3e-0000-0000-0000-000000000000/text-tracks/refresh", true, NativeApiForwardDecision.NotFound)]
    [InlineData("PUT", "display/home", true, NativeApiForwardDecision.NotFound)]
    // The same blocks hold however the ID is written: no dashes, braces, uppercase, or not an ID at all.
    [InlineData("PUT", "persons/6f0c2a3e000000000000000000000000/editor", true, NativeApiForwardDecision.NotFound)]
    [InlineData("PUT", "persons/{6f0c2a3e-0000-0000-0000-000000000000}/editor", true, NativeApiForwardDecision.NotFound)]
    [InlineData("GET", "persons/6F0C2A3E000000000000000000000000/editor", true, NativeApiForwardDecision.NotFound)]
    [InlineData("PUT", "persons/anything/editor", true, NativeApiForwardDecision.NotFound)]
    [InlineData("POST", "player/audiobooks/6f0c2a3e000000000000000000000000/chapter-overrides", true, NativeApiForwardDecision.NotFound)]
    [InlineData("DELETE", "player/audiobooks/{6f0c2a3e-0000-0000-0000-000000000000}/chapter-overrides/6f0c2a3e000000000000000000000001/3", true, NativeApiForwardDecision.NotFound)]
    [InlineData("GET", "player/audiobooks/6f0c2a3e000000000000000000000000/chapter-overrides", true, NativeApiForwardDecision.Forward)]
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
    public void NetworkSettingsGate_IsOffUnlessAppAccessAndRemoteAccessAreBothOn()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tuvima-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            bool Read(string json)
            {
                File.WriteAllText(Path.Combine(dir, "network.json"), json);
                // A fresh gate per case so the change-detection cache cannot hide a parse.
                return new NetworkSettingsNativeAppAccessGate(new DashboardConfigurationReader(dir), dir).IsEnabled;
            }

            var gate = new NetworkSettingsNativeAppAccessGate(new DashboardConfigurationReader(dir), dir);
            Assert.False(gate.IsEnabled); // no file
            Assert.False(Read("{}"));
            Assert.False(Read("{\"native_app_access\":{\"enabled\":true}}")); // remote access off
            Assert.False(Read("{\"remote\":{\"enabled\":true}}")); // app access off
            Assert.True(Read("{\"remote\":{\"enabled\":true},\"native_app_access\":{\"enabled\":true}}"));
            Assert.False(Read("{ not json")); // fails closed
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void NetworkSettingsGate_PicksUpAChangeWithoutRestart()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tuvima-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "network.json");
            var gate = new NetworkSettingsNativeAppAccessGate(new DashboardConfigurationReader(dir), dir);
            File.WriteAllText(path, "{\"remote\":{\"enabled\":true},\"native_app_access\":{\"enabled\":true}}");
            Assert.True(gate.IsEnabled);
            File.WriteAllText(path, "{\"remote\":{\"enabled\":true},\"native_app_access\":{\"enabled\":false}}");
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));
            Assert.False(gate.IsEnabled);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
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

    [Fact]
    public async Task PairingActions_AreThrottledPerAppAddressAndInTotal()
    {
        var engineHits = 0;
        await using var engine = await TestApplication.StartEngineAsync(engineHits: () => Interlocked.Increment(ref engineHits));
        await using var dashboard = await TestApplication.StartDashboardAsync(engine.Address, enabled: true);
        using var client = new HttpClient { BaseAddress = dashboard.Address };

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < NativeAppPairingThrottle.PerAddressPerMinute + 2; i++)
        {
            using var response = await client.PostAsync("/api/v1/oauth/device_authorization", new StringContent("{}"));
            statuses.Add(response.StatusCode);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                // Apps expect the OAuth error shape, not a bare status.
                var error = await response.Content.ReadFromJsonAsync<OAuthErrorResponse>();
                Assert.Equal("temporarily_unavailable", error!.Error);
                Assert.Equal(60, error.Interval);
            }
        }

        Assert.Equal(NativeAppPairingThrottle.PerAddressPerMinute, statuses.Count(s => s == HttpStatusCode.OK));
        Assert.Equal(2, statuses.Count(s => s == HttpStatusCode.TooManyRequests));
        Assert.Equal(NativeAppPairingThrottle.PerAddressPerMinute, engineHits);
    }

    [Fact]
    public async Task ApprovalPollingAndRefresh_AreLeftToTheEngine()
    {
        var engineHits = 0;
        await using var engine = await TestApplication.StartEngineAsync(engineHits: () => Interlocked.Increment(ref engineHits));
        await using var dashboard = await TestApplication.StartDashboardAsync(engine.Address, enabled: true);
        using var client = new HttpClient { BaseAddress = dashboard.Address };

        for (var i = 0; i < 30; i++)
        {
            using var response = await client.PostAsync("/api/v1/oauth/token", new StringContent("{}"));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        Assert.Equal(30, engineHits);
    }

    [Fact]
    public void Throttle_KeepsTheTotalBelowTheEnginesLimitAndRecoversAfterAMinute()
    {
        var clock = new ManualClock();
        var throttle = new NativeAppPairingThrottle(clock);

        var allowed = Enumerable.Range(0, 20).Count(i => throttle.TryAcquire($"10.0.0.{i}"));

        Assert.Equal(NativeAppPairingThrottle.TotalPerMinute, allowed);
        Assert.True(NativeAppPairingThrottle.TotalPerMinute < 10, "must leave room under the Engine's 10 per minute");
        clock.Advance(TimeSpan.FromSeconds(61));
        Assert.True(throttle.TryAcquire("10.0.0.99"));
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset now = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan by) => now += by;
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

            builder.Services.AddSingleton<NativeAppPairingThrottle>();

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
