using System.Net;
using System.Net.Http.Json;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Web.Endpoints;
using MediaEngine.Web.Services.Integration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Web.Tests;

public sealed class OutsideAddressDiscoveryTests
{
    [Fact]
    public async Task Discovery_IncludesTheOutsideAddress_WhenTheEngineReportsOne()
    {
        await using var engine = await TestHost.StartEngineAsync(_ => Results.Json(new { outside_address = "https://tuvima.example.com" }));
        await using var dashboard = await TestHost.StartDashboardAsync(engine.Address);
        using var client = new HttpClient { BaseAddress = dashboard.Address };

        var discovery = await client.GetFromJsonAsync<TuvimaDiscoveryResponse>("/.well-known/tuvima");

        Assert.Equal("https://tuvima.example.com", discovery!.OutsideAddress);
        Assert.Equal($"{dashboard.Address.Scheme}://{dashboard.Address.Authority}/api/v1", discovery.ApiBaseUrl);
    }

    [Fact]
    public async Task Discovery_OmitsTheOutsideAddress_WhenTheEngineReportsNone()
    {
        // The Engine answers with no address while the connection mode is local-only.
        await using var engine = await TestHost.StartEngineAsync(_ => Results.Json(new { outside_address = (string?)null }));
        await using var dashboard = await TestHost.StartDashboardAsync(engine.Address);
        using var client = new HttpClient { BaseAddress = dashboard.Address };

        var discovery = await client.GetFromJsonAsync<TuvimaDiscoveryResponse>("/.well-known/tuvima");

        Assert.Null(discovery!.OutsideAddress);
    }

    [Fact]
    public async Task Discovery_StillAnswers200WithoutTheField_WhenTheEngineRefuses()
    {
        await using var engine = await TestHost.StartEngineAsync(_ => Results.StatusCode(StatusCodes.Status503ServiceUnavailable));
        await using var dashboard = await TestHost.StartDashboardAsync(engine.Address);
        using var client = new HttpClient { BaseAddress = dashboard.Address };

        using var response = await client.GetAsync("/.well-known/tuvima");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null((await response.Content.ReadFromJsonAsync<TuvimaDiscoveryResponse>())!.OutsideAddress);
    }

    [Fact]
    public async Task Discovery_StillAnswers200WithoutTheField_WhenTheEngineCannotBeReached()
    {
        Uri engineAddress;
        await using (var engine = await TestHost.StartEngineAsync(_ => Results.Ok()))
        {
            engineAddress = engine.Address;
        }

        await using var dashboard = await TestHost.StartDashboardAsync(engineAddress);
        using var client = new HttpClient { BaseAddress = dashboard.Address };

        using var response = await client.GetAsync("/.well-known/tuvima");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null((await response.Content.ReadFromJsonAsync<TuvimaDiscoveryResponse>())!.OutsideAddress);
    }

    [Fact]
    public async Task Discovery_StillAnswers200WithoutTheField_WhenNoSourceIsRegistered()
    {
        await using var dashboard = await TestHost.StartDashboardAsync(new Uri("http://127.0.0.1:1"), registerSource: false);
        using var client = new HttpClient { BaseAddress = dashboard.Address };

        using var response = await client.GetAsync("/.well-known/tuvima");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Discovery_AsksTheEngineOncePerWindow_SoAnonymousTrafficCannotHammerIt()
    {
        var asked = 0;
        await using var engine = await TestHost.StartEngineAsync(_ =>
        {
            Interlocked.Increment(ref asked);
            return Results.Json(new { outside_address = "https://tuvima.example.com" });
        });
        await using var dashboard = await TestHost.StartDashboardAsync(engine.Address);
        using var client = new HttpClient { BaseAddress = dashboard.Address };

        for (var i = 0; i < 20; i++)
        {
            using var response = await client.GetAsync("/.well-known/tuvima");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        Assert.Equal(1, asked);
    }

    [Fact]
    public async Task Source_AsksTheEngineAgain_OnceTheAnswerHasExpired()
    {
        var asked = 0;
        var clock = new ManualClock();
        await using var engine = await TestHost.StartEngineAsync(_ =>
            Results.Json(new { outside_address = $"https://tuvima{Interlocked.Increment(ref asked)}.example.com" }));
        var source = new EngineOutsideAddressSource(new FixedClientFactory(engine.Address), NullLogger<EngineOutsideAddressSource>.Instance, clock);

        var first = await source.GetAsync();
        clock.Advance(EngineOutsideAddressSource.AnswerLifetime - TimeSpan.FromSeconds(1));
        var stillFresh = await source.GetAsync();
        clock.Advance(TimeSpan.FromSeconds(2));
        var refreshed = await source.GetAsync();

        Assert.Equal("https://tuvima1.example.com", first);
        Assert.Equal(first, stillFresh);
        Assert.Equal("https://tuvima2.example.com", refreshed);
    }

    [Fact]
    public async Task Source_RetriesSoonerAfterAFailure_ThanAfterAnAnswer()
    {
        var healthy = false;
        var clock = new ManualClock();
        await using var engine = await TestHost.StartEngineAsync(_ => healthy
            ? Results.Json(new { outside_address = "https://tuvima.example.com" })
            : Results.StatusCode(StatusCodes.Status500InternalServerError));
        var source = new EngineOutsideAddressSource(new FixedClientFactory(engine.Address), NullLogger<EngineOutsideAddressSource>.Instance, clock);

        var failed = await source.GetAsync();
        healthy = true;
        clock.Advance(EngineOutsideAddressSource.FailureLifetime + TimeSpan.FromSeconds(1));
        var recovered = await source.GetAsync();

        Assert.Null(failed);
        Assert.Equal("https://tuvima.example.com", recovered);
        Assert.True(EngineOutsideAddressSource.FailureLifetime < EngineOutsideAddressSource.AnswerLifetime);
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset now = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan by) => now += by;
    }

    private sealed class FixedClientFactory(Uri engine) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new() { BaseAddress = engine };
    }

    private sealed class TestHost(WebApplication app, Uri address) : IAsyncDisposable
    {
        public Uri Address { get; } = address;

        public static Task<TestHost> StartEngineAsync(Func<HttpContext, IResult> outsideAddress) =>
            StartAsync(null, registerSource: false, app => app.MapGet("/network/outside-address", outsideAddress));

        public static Task<TestHost> StartDashboardAsync(Uri engine, bool registerSource = true) =>
            StartAsync(engine, registerSource, app => app.MapClientApiEdge());

        private static async Task<TestHost> StartAsync(Uri? engine, bool registerSource, Action<WebApplication> configure)
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.AddAntiforgery();
            builder.Services.AddSingleton<NativeAppPairingThrottle>();
            if (engine is not null)
            {
                builder.Services.AddClientApiProxyClient(engine);
                builder.Services.AddHttpClient("EngineIdentity", client => client.BaseAddress = engine);
                if (registerSource)
                {
                    builder.Services.AddSingleton<IOutsideAddressSource, EngineOutsideAddressSource>();
                }
            }

            var app = builder.Build();
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
