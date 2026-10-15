using System.Net;
using MediaEngine.Web.Services.Integration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

public sealed class NativeAppCorsTests
{
    [Theory]
    [InlineData("null", true)]
    [InlineData("file://", true)]
    [InlineData("app://tuvima", true)]
    [InlineData("tizen://app", true)]
    [InlineData("webos://tv", true)]
    [InlineData("FILE://", true)]
    [InlineData("app://", true)]
    [InlineData("https://evil.example", false)]
    [InlineData("http://192.168.1.20:5016", false)]
    [InlineData("http://localhost:5016", false)]
    [InlineData("app://user@host", false)]
    [InlineData("app://host/path", false)]
    [InlineData("app:/host", false)]
    [InlineData("application://x", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsAppOrigin_AdmitsOnlyNullAndAppSchemes(string? origin, bool expected) =>
        Assert.Equal(expected, NativeAppCors.IsAppOrigin(origin));

    [Theory]
    [InlineData("GET", "/api/v1/display/home", true)]
    [InlineData("GET", "/api/v1/playback/6f0c2a3e-0000-0000-0000-000000000000/manifest", true)]
    [InlineData("POST", "/api/v1/oauth/device_authorization", true)]
    [InlineData("POST", "/api/v1/oauth/token", true)]
    [InlineData("POST", "/api/v1/player/heartbeat", true)]
    [InlineData("PUT", "/api/v1/devices/current/capabilities", true)]
    [InlineData("POST", "/api/v1/devices/current/switch-profile", true)]
    [InlineData("DELETE", "/api/v1/devices/6f0c2a3e-0000-0000-0000-000000000000", true)]
    [InlineData("GET", "/.well-known/tuvima", true)]
    // Not the app surface, or not the allowed path or method.
    [InlineData("POST", "/.well-known/tuvima", false)]
    [InlineData("GET", "/.well-known/other", false)]
    [InlineData("GET", "/api/v1/settings/network", false)]
    [InlineData("GET", "/api/v1/analytics/playback", false)]
    [InlineData("POST", "/api/v1/display/home", false)]
    [InlineData("GET", "/api/v1", false)]
    [InlineData("GET", "/api/v2/display/home", false)]
    [InlineData("GET", "/pair", false)]
    [InlineData("GET", "/health/ready", false)]
    [InlineData("GET", "/", false)]
    [InlineData("GET", "/api/v1/display/%2e%2e/health", false)]
    public void IsAllowedRoute_FollowsTheAppDoorAllowList(string method, string path, bool expected) =>
        Assert.Equal(expected, NativeAppCors.IsAllowedRoute(method, path));

    [Theory]
    [InlineData("null")]
    [InlineData("file://")]
    [InlineData("app://tuvima")]
    [InlineData("tizen://tv")]
    [InlineData("webos://tv")]
    public async Task Preflight_IsApproved_ForAppOriginsWithTheNarrowSet(string origin)
    {
        await using var host = await TestHost.StartAsync(enabled: true);

        using var response = await host.PreflightAsync(origin, "/api/v1/display/home", "GET", "authorization");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(origin, Single(response, "Access-Control-Allow-Origin"));
        Assert.Equal("GET, POST, PUT, DELETE, OPTIONS", Single(response, "Access-Control-Allow-Methods"));
        Assert.Equal("Authorization, Content-Type", Single(response, "Access-Control-Allow-Headers"));
        Assert.Equal("600", Single(response, "Access-Control-Max-Age"));
        Assert.False(response.Headers.Contains("Access-Control-Allow-Credentials"));
        Assert.Contains("Origin", response.Headers.Vary);
        Assert.Equal(0, host.TerminalHits);
    }

    [Fact]
    public async Task Preflight_ForPairingPostWithJson_IsApproved()
    {
        await using var host = await TestHost.StartAsync(enabled: true);

        using var response = await host.PreflightAsync("null", "/api/v1/oauth/token", "POST", "Content-Type");

        Assert.Equal("null", Single(response, "Access-Control-Allow-Origin"));
    }

    [Theory]
    [InlineData("https://evil.example")]
    [InlineData("http://localhost:5016")]
    [InlineData("http://192.168.1.20:5016")]
    public async Task Preflight_GetsNoCorsHeaders_ForWebOrigins(string origin)
    {
        await using var host = await TestHost.StartAsync(enabled: true);

        using var response = await host.PreflightAsync(origin, "/api/v1/display/home", "GET", "authorization");

        AssertNoCors(response);
        // Not handled here: it falls through to the rest of the pipeline.
        Assert.Equal(1, host.TerminalHits);
    }

    [Fact]
    public async Task Preflight_GetsNoCorsHeaders_WhileAppAccessIsOff()
    {
        await using var host = await TestHost.StartAsync(enabled: false);

        using var response = await host.PreflightAsync("null", "/api/v1/display/home", "GET", "authorization");

        AssertNoCors(response);
        Assert.Equal(1, host.TerminalHits);
    }

    [Fact]
    public async Task Preflight_FailsClosed_WhenNoGateIsRegistered()
    {
        await using var host = await TestHost.StartAsync(enabled: null);

        using var response = await host.PreflightAsync("null", "/api/v1/display/home", "GET", "authorization");

        AssertNoCors(response);
    }

    [Theory]
    [InlineData("/api/v1/settings/network", "GET", "authorization")]
    [InlineData("/health/ready", "GET", "authorization")]
    [InlineData("/pair", "GET", "authorization")]
    [InlineData("/api/v1/display/home", "PATCH", "authorization")]
    [InlineData("/api/v1/display/home", "POST", "authorization")]
    [InlineData("/api/v1/view/timeline", "DELETE", "authorization")]
    [InlineData("/api/v1/display/home", "GET", "x-tuvima-service-key")]
    [InlineData("/api/v1/display/home", "GET", "authorization, cookie")]
    [InlineData("/api/v1/display/home", "GET", "x-custom")]
    public async Task Preflight_IsNotApproved_ForOtherPathsMethodsOrHeaders(string path, string method, string headers)
    {
        await using var host = await TestHost.StartAsync(enabled: true);

        using var response = await host.PreflightAsync("null", path, method, headers);

        AssertNoCors(response);
    }

    [Fact]
    public async Task ActualRequest_FromAnAppOrigin_GetsTheOriginAndRetryAfterButNeverCredentials()
    {
        await using var host = await TestHost.StartAsync(enabled: true);

        using var response = await host.GetAsync("tizen://tv", "/api/v1/display/home");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("tizen://tv", Single(response, "Access-Control-Allow-Origin"));
        Assert.Equal("Retry-After", Single(response, "Access-Control-Expose-Headers"));
        Assert.False(response.Headers.Contains("Access-Control-Allow-Credentials"));
        Assert.Contains("Origin", response.Headers.Vary);
        Assert.Equal(1, host.TerminalHits);
    }

    [Fact]
    public async Task ActualRequest_Discovery_FromNullOrigin_IsAllowed()
    {
        await using var host = await TestHost.StartAsync(enabled: true);

        using var response = await host.GetAsync("null", "/.well-known/tuvima");

        Assert.Equal("null", Single(response, "Access-Control-Allow-Origin"));
    }

    [Theory]
    [InlineData("https://evil.example", "/api/v1/display/home")]
    [InlineData(null, "/api/v1/display/home")]
    [InlineData("null", "/health/ready")]
    [InlineData("null", "/pair")]
    public async Task ActualRequest_OtherwiseGetsNoCorsHeaders(string? origin, string path)
    {
        await using var host = await TestHost.StartAsync(enabled: true);

        using var response = await host.GetAsync(origin, path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertNoCors(response);
    }

    private static string Single(HttpResponseMessage response, string name) =>
        Assert.Single(response.Headers.GetValues(name));

    private static void AssertNoCors(HttpResponseMessage response)
    {
        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
        Assert.False(response.Headers.Contains("Access-Control-Allow-Methods"));
        Assert.False(response.Headers.Contains("Access-Control-Allow-Headers"));
        Assert.False(response.Headers.Contains("Access-Control-Allow-Credentials"));
        Assert.False(response.Headers.Contains("Access-Control-Expose-Headers"));
    }

    private sealed class FixedGate(bool enabled) : INativeAppAccessGate
    {
        public bool IsEnabled => enabled;
    }

    private sealed class TestHost(WebApplication app, Uri address, TestHost.Counter counter) : IAsyncDisposable
    {
        public sealed class Counter
        {
            public int Hits;
        }

        public int TerminalHits => counter.Hits;

        public static async Task<TestHost> StartAsync(bool? enabled)
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            if (enabled is { } value)
            {
                builder.Services.AddSingleton<INativeAppAccessGate>(new FixedGate(value));
            }

            var app = builder.Build();
            var counter = new Counter();
            app.UseNativeAppCors();
            app.Run(context =>
            {
                Interlocked.Increment(ref counter.Hits);
                context.Response.StatusCode = StatusCodes.Status200OK;
                return Task.CompletedTask;
            });
            await app.StartAsync();
            var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>();
            return new TestHost(app, new Uri(addresses!.Addresses.Single()), counter);
        }

        public Task<HttpResponseMessage> PreflightAsync(string origin, string path, string method, string headers)
        {
            var request = new HttpRequestMessage(HttpMethod.Options, new Uri(address, path));
            request.Headers.TryAddWithoutValidation("Origin", origin);
            request.Headers.TryAddWithoutValidation("Access-Control-Request-Method", method);
            request.Headers.TryAddWithoutValidation("Access-Control-Request-Headers", headers);
            return Send(request);
        }

        public Task<HttpResponseMessage> GetAsync(string? origin, string path)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, new Uri(address, path));
            if (origin is not null)
            {
                request.Headers.TryAddWithoutValidation("Origin", origin);
            }

            return Send(request);
        }

        private static async Task<HttpResponseMessage> Send(HttpRequestMessage request)
        {
            using var client = new HttpClient();
            using (request)
            {
                return await client.SendAsync(request);
            }
        }

        public async ValueTask DisposeAsync()
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
}
