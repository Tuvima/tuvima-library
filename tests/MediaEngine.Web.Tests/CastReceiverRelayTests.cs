using System.Net;
using System.Net.Http.Headers;
using MediaEngine.Web.Endpoints;
using MediaEngine.Web.Services.Configuration;
using MediaEngine.Web.Services.Integration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>
/// The Dashboard's relay for a cast receiver (a TV): it exists only while app access is on, answers only a request from this
/// computer or the home network, passes only the range headers to the Engine, and gives back only the media and CORS headers.
/// </summary>
public sealed class CastReceiverRelayTests
{
    private const string Grant = "AbCdEf0123456789_-AbCdEf0123456789_-";

    [Fact]
    public async Task ARangeRequestIsRelayedWithOnlyTheRangeHeaders_AndOnlyTheMediaHeadersComeBack()
    {
        string? seenPath = null;
        IReadOnlyDictionary<string, string>? headers = null;
        await using var engine = await Host.StartEngineAsync(context =>
        {
            seenPath = context.Request.Path.Value;
            headers = context.Request.Headers.ToDictionary(pair => pair.Key, pair => pair.Value.ToString(), StringComparer.OrdinalIgnoreCase);
            context.Response.StatusCode = StatusCodes.Status206PartialContent;
            context.Response.ContentType = "video/mp4";
            context.Response.Headers.ContentRange = "bytes 0-3/10";
            context.Response.Headers.AcceptRanges = "bytes";
            context.Response.Headers.CacheControl = "private, no-store";
            context.Response.Headers["Access-Control-Allow-Origin"] = "*";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            context.Response.Headers.SetCookie = "engine=1";
            context.Response.Headers["X-Engine-Secret"] = "leak";
            context.Response.Headers["Server"] = "engine";
            return context.Response.Body.WriteAsync(new byte[] { 1, 2, 3, 4 }).AsTask();
        });
        await using var dashboard = await Host.StartDashboardAsync(engine.Address, enabled: true);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/cast/v1/{Grant}/media");
        request.Headers.Range = new RangeHeaderValue(0, 3);
        request.Headers.TryAddWithoutValidation("If-Range", "\"abc\"");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "must-not-travel");
        request.Headers.TryAddWithoutValidation("Cookie", "session=must-not-travel");
        request.Headers.TryAddWithoutValidation("X-Custom", "must-not-travel");

        using var response = await dashboard.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, await response.Content.ReadAsByteArrayAsync());
        Assert.Equal($"/cast/v1/{Grant}/media", seenPath);
        Assert.Equal("bytes=0-3", headers!["Range"]);
        Assert.Equal("\"abc\"", headers["If-Range"]);
        Assert.False(headers.ContainsKey("Authorization"));
        Assert.False(headers.ContainsKey("Cookie"));
        Assert.False(headers.ContainsKey("X-Custom"));

        Assert.Equal("video/mp4", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("bytes", response.Headers.AcceptRanges.Single());
        Assert.Equal("*", response.Headers.GetValues("Access-Control-Allow-Origin").Single());
        Assert.Equal("no-referrer", response.Headers.GetValues("Referrer-Policy").Single());
        Assert.False(response.Headers.Contains("Set-Cookie"));
        Assert.False(response.Headers.Contains("X-Engine-Secret"));
    }

    [Fact]
    public async Task AnOptionsPreflightIsRelayedWithoutAnyCredentials()
    {
        string? origin = null;
        await using var engine = await Host.StartEngineAsync(context =>
        {
            origin = context.Request.Headers.Origin;
            context.Response.StatusCode = StatusCodes.Status204NoContent;
            context.Response.Headers["Access-Control-Allow-Origin"] = "*";
            context.Response.Headers["Access-Control-Allow-Methods"] = "GET, HEAD, OPTIONS";
            context.Response.Headers["Access-Control-Allow-Credentials"] = "true";
            return Task.CompletedTask;
        });
        await using var dashboard = await Host.StartDashboardAsync(engine.Address, enabled: true);
        using var request = new HttpRequestMessage(HttpMethod.Options, $"/cast/v1/{Grant}/media");
        request.Headers.TryAddWithoutValidation("Origin", "https://tv.example");

        using var response = await dashboard.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("https://tv.example", origin);
        Assert.Equal("GET, HEAD, OPTIONS", response.Headers.GetValues("Access-Control-Allow-Methods").Single());
        // Credentials are never allowed back to a receiver, whatever an upstream answer says.
        Assert.False(response.Headers.Contains("Access-Control-Allow-Credentials"));
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    public async Task WritesAreNotRelayed(string method)
    {
        var hits = 0;
        await using var engine = await Host.StartEngineAsync(_ => Interlocked.Increment(ref hits));
        await using var dashboard = await Host.StartDashboardAsync(engine.Address, enabled: true);

        using var response = await dashboard.Client.SendAsync(new HttpRequestMessage(new HttpMethod(method), $"/cast/v1/{Grant}/media"));

        Assert.True(response.StatusCode is HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotFound);
        Assert.Equal(0, hits);
    }

    [Theory]
    [InlineData("media/../../etc", "dots")]
    [InlineData("media%2f..%2f", "encoded")]
    public async Task UnsafePathsNeverReachTheEngine(string rest, string _)
    {
        var hits = 0;
        await using var engine = await Host.StartEngineAsync(_ => Interlocked.Increment(ref hits));
        await using var dashboard = await Host.StartDashboardAsync(engine.Address, enabled: true);

        using var response = await dashboard.Client.GetAsync($"/cast/v1/{Grant}/{rest}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(0, hits);
    }

    [Theory]
    [InlineData("short")]
    [InlineData("has.dots.in.it.0123456789")]
    public async Task AMalformedGrantNeverReachesTheEngine(string grant)
    {
        var hits = 0;
        await using var engine = await Host.StartEngineAsync(_ => Interlocked.Increment(ref hits));
        await using var dashboard = await Host.StartDashboardAsync(engine.Address, enabled: true);

        using var response = await dashboard.Client.GetAsync($"/cast/v1/{grant}/media");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(0, hits);
    }

    [Fact]
    public async Task NothingIsRelayedWhileAppAccessIsOffOrUnregistered()
    {
        var hits = 0;
        await using var engine = await Host.StartEngineAsync(_ => Interlocked.Increment(ref hits));
        await using var off = await Host.StartDashboardAsync(engine.Address, enabled: false);
        await using var missing = await Host.StartDashboardAsync(engine.Address, enabled: null);

        using var offResponse = await off.Client.GetAsync($"/cast/v1/{Grant}/media");
        using var missingResponse = await missing.Client.GetAsync($"/cast/v1/{Grant}/media");

        Assert.Equal(HttpStatusCode.NotFound, offResponse.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missingResponse.StatusCode);
        Assert.Equal(0, hits);
    }

    [Fact]
    public async Task WithoutAnIngressClassifierTheRelayFailsClosed()
    {
        var hits = 0;
        await using var engine = await Host.StartEngineAsync(_ => Interlocked.Increment(ref hits));
        await using var dashboard = await Host.StartDashboardAsync(engine.Address, enabled: true, classifier: false);

        using var response = await dashboard.Client.GetAsync($"/cast/v1/{Grant}/media");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(0, hits);
    }

    [Fact]
    public void OnlyTheListedHeadersTravelInEitherDirection()
    {
        Assert.Equal(["Range", "If-Range"], CastReceiverRelayEndpoints.ForwardedRequestHeaders);
        Assert.DoesNotContain(CastReceiverRelayEndpoints.ReturnedResponseHeaders, name =>
            name.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Access-Control-Allow-Credentials", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Authorization", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TheCastPathIsExemptFromTheFriendlyNotFoundPage()
    {
        Assert.True(DoorStatusCodePagesExtensions.IsDoorPath(new PathString($"/cast/v1/{Grant}/media")));
    }

    private sealed class Host(WebApplication app, Uri address) : IAsyncDisposable
    {
        public Uri Address { get; } = address;
        public HttpClient Client { get; } = new() { BaseAddress = address };

        public static Task<Host> StartEngineAsync(Func<HttpContext, Task> handle) =>
            StartAsync(null, null, true, application => application.Run(new RequestDelegate(handle)));

        public static Task<Host> StartEngineAsync(Action<HttpContext> handle) =>
            StartEngineAsync(context => { handle(context); return Task.CompletedTask; });

        public static Task<Host> StartDashboardAsync(Uri engine, bool? enabled, bool classifier = true) =>
            StartAsync(engine, enabled, classifier, application => application.MapCastReceiverRelay());

        private static async Task<Host> StartAsync(Uri? engine, bool? enabled, bool classifier, Action<WebApplication> configure)
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            if (enabled is { } value)
            {
                builder.Services.AddSingleton<INativeAppAccessGate>(new FixedGate(value));
            }

            if (classifier)
            {
                builder.Services.AddSingleton(new IngressClassifier(null, null));
            }

            if (engine is not null)
            {
                builder.Services.AddClientApiProxyClient(engine);
            }

            var app = builder.Build();
            configure(app);
            await app.StartAsync();
            var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>();
            return new(app, new Uri(addresses!.Addresses.Single()));
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    private sealed class FixedGate(bool enabled) : INativeAppAccessGate
    {
        public bool IsEnabled => enabled;
    }
}
