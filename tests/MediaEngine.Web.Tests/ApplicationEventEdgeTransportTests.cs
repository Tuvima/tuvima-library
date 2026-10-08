using System.Net;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Web.Endpoints;
using MediaEngine.Web.Services.Integration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

public sealed class ApplicationEventEdgeTransportTests
{
    [Fact]
    public async Task Negotiation_RejectsMissingAndQueryOnlyTokens()
    {
        await using var engine = await TestApplication.StartEngineAsync(app =>
            app.MapPost(ApplicationEventClientMethods.HubPath + "/negotiate", () => Results.Ok()));
        await using var dashboard = await TestApplication.StartDashboardAsync(engine.Address);
        using var client = new HttpClient { BaseAddress = dashboard.Address };

        using var missing = await client.PostAsync(ApplicationEventClientMethods.HubPath + "/negotiate", null);
        using var queryOnly = await client.PostAsync(
            ApplicationEventClientMethods.HubPath + "/negotiate?access_token=must-not-be-accepted", null);

        Assert.Equal(HttpStatusCode.Unauthorized, missing.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, queryOnly.StatusCode);
    }

    [Fact]
    public async Task Negotiation_ForwardsExactBearerAndControlledUpstreamStatus()
    {
        string? authorization = null;
        await using var engine = await TestApplication.StartEngineAsync(app =>
            app.MapPost(ApplicationEventClientMethods.HubPath + "/negotiate", (HttpContext context) =>
            {
                authorization = context.Request.Headers.Authorization;
                return Results.StatusCode(StatusCodes.Status401Unauthorized);
            }));
        await using var dashboard = await TestApplication.StartDashboardAsync(engine.Address);
        using var client = new HttpClient { BaseAddress = dashboard.Address };
        using var request = new HttpRequestMessage(HttpMethod.Post, ApplicationEventClientMethods.HubPath + "/negotiate");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "native-access-token");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("Bearer native-access-token", authorization);
    }

    [Fact]
    public async Task WebSocket_ForwardsBearerAndTerminatesBothPumpsAfterClose()
    {
        string? authorization = null;
        await using var engine = await TestApplication.StartEngineAsync(app =>
            app.Map(ApplicationEventClientMethods.HubPath, async context =>
            {
                authorization = context.Request.Headers.Authorization;
                if (!context.WebSockets.IsWebSocketRequest)
                {
                    context.Response.StatusCode = StatusCodes.Status400BadRequest;
                    return;
                }
                using var socket = await context.WebSockets.AcceptWebSocketAsync();
                var buffer = new byte[32];
                var received = await socket.ReceiveAsync(buffer, context.RequestAborted);
                await socket.SendAsync(buffer.AsMemory(0, received.Count), received.MessageType, true, context.RequestAborted);
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", context.RequestAborted);
            }));
        await using var dashboard = await TestApplication.StartDashboardAsync(engine.Address);
        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", "Bearer native-websocket-token");
        var endpoint = new UriBuilder(dashboard.Address)
        {
            Scheme = "ws",
            Path = ApplicationEventClientMethods.HubPath,
        }.Uri;

        await socket.ConnectAsync(endpoint, default);
        var sent = Encoding.UTF8.GetBytes("event-frame");
        await socket.SendAsync(sent, WebSocketMessageType.Text, true, default);
        var receivedBuffer = new byte[32];
        var received = await socket.ReceiveAsync(receivedBuffer, default);
        var closed = await socket.ReceiveAsync(receivedBuffer, default);
        await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "acknowledged", default);

        Assert.Equal("Bearer native-websocket-token", authorization);
        Assert.Equal("event-frame", Encoding.UTF8.GetString(receivedBuffer, 0, received.Count));
        Assert.Equal(WebSocketMessageType.Close, closed.MessageType);
        Assert.Equal(WebSocketState.Closed, socket.State);
    }

    [Fact]
    public async Task WebSocket_UpstreamRejectionDoesNotUpgradeDashboardConnection()
    {
        await using var engine = await TestApplication.StartEngineAsync(app =>
            app.Map(ApplicationEventClientMethods.HubPath, context =>
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return Task.CompletedTask;
            }));
        await using var dashboard = await TestApplication.StartDashboardAsync(engine.Address);
        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", "Bearer rejected-token");
        socket.Options.CollectHttpResponseDetails = true;
        var endpoint = new UriBuilder(dashboard.Address)
        {
            Scheme = "ws",
            Path = ApplicationEventClientMethods.HubPath,
        }.Uri;

        await Assert.ThrowsAsync<WebSocketException>(() => socket.ConnectAsync(endpoint, default));
        Assert.NotEqual(WebSocketState.Open, socket.State);
        Assert.Equal(HttpStatusCode.Unauthorized, socket.HttpStatusCode);
    }

    private sealed class FixedGate(bool enabled) : INativeAppAccessGate
    {
        public bool IsEnabled => enabled;
    }

    private sealed class TestApplication(WebApplication app, Uri address) : IAsyncDisposable
    {
        public Uri Address { get; } = address;

        public static Task<TestApplication> StartEngineAsync(Action<WebApplication> configure) => StartAsync(null, configure);
        public static Task<TestApplication> StartDashboardAsync(Uri engine) => StartAsync(engine, app => app.MapClientApiEdge());

        private static async Task<TestApplication> StartAsync(Uri? engine, Action<WebApplication> configure)
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.AddAntiforgery();
            builder.Services.AddSingleton<INativeAppAccessGate>(new FixedGate(true));
            builder.Services.AddHttpClient("ClientApiProxy", client => client.BaseAddress = engine);
            builder.Services.AddHttpClient("EngineIdentity", client => client.BaseAddress = engine);
            var app = builder.Build();
            app.UseWebSockets();
            configure(app);
            await app.StartAsync();
            var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>();
            var address = new Uri(addresses!.Addresses.Single());
            return new(app, address);
        }

        public async ValueTask DisposeAsync()
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
}
