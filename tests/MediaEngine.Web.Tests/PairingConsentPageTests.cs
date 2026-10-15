using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
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

/// <summary>
/// The pairing consent screen. Photos and personal videos are one separate item that is unticked until the person ticks it, and the
/// three photo scopes are sent to the Engine only when it is ticked.
/// </summary>
public sealed class PairingConsentPageTests
{
    private static readonly string[] PhotoScopes = ["view.personal.read", "view.originals.read", "view.galleries.read"];

    [Fact]
    public async Task ThePairingScreen_ListsPhotosAndVideosAsOneUntickedItemInPlainWords()
    {
        await using var host = await PairingHost.StartAsync(["library.read", "playback.read", .. PhotoScopes]);
        using var client = host.NewClient();

        var html = await client.GetStringAsync("/pair?user_code=ABCD-EFGH");

        // One item, opt-in: not checked, and not three raw scope checkboxes.
        var photos = Regex.Match(html, "<input type=checkbox name=\"photos\" value=\"allow\"[^>]*>");
        Assert.True(photos.Success);
        Assert.DoesNotContain("checked", photos.Value, StringComparison.Ordinal);
        Assert.Contains("Allow access to my photos and videos", html, StringComparison.Ordinal);
        Assert.Contains("browse your own photos and videos, view and download originals, and your galleries (including galleries shared with you)", html, StringComparison.Ordinal);
        foreach (var scope in PhotoScopes)
        {
            Assert.Contains(scope, html, StringComparison.Ordinal);
            Assert.DoesNotContain($"name=scope value=\"{scope}\"", html, StringComparison.Ordinal);
        }

        // The ordinary scopes keep their existing behaviour: listed and ticked.
        Assert.Contains("name=scope value=\"library.read\" checked", html, StringComparison.Ordinal);
        Assert.Contains("name=scope value=\"playback.read\" checked", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ThePairingScreen_OffersNoPhotoItemWhenTheAppDidNotAskForPhotos()
    {
        await using var host = await PairingHost.StartAsync(["library.read"]);
        using var client = host.NewClient();

        var html = await client.GetStringAsync("/pair?user_code=ABCD-EFGH");

        Assert.DoesNotContain("photos and videos", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("name=\"photos\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ApprovingWithThePhotoItemUntickedSendsNoPhotoScope()
    {
        await using var host = await PairingHost.StartAsync(["library.read", "playback.read", .. PhotoScopes]);
        using var client = host.NewClient();

        var scopes = await host.DecideAsync(client, [("scope", "library.read"), ("scope", "playback.read")]);

        Assert.Equal(["library.read", "playback.read"], scopes);
    }

    [Fact]
    public async Task APhotoScopePostedAsAnOrdinaryScopeIsDropped()
    {
        await using var host = await PairingHost.StartAsync(["library.read", .. PhotoScopes]);
        using var client = host.NewClient();

        // Only the explicit photo tick can grant photo access.
        var scopes = await host.DecideAsync(client, [("scope", "library.read"), ("scope", "view.personal.read"), ("scope", "view.originals.read")]);

        Assert.Equal(["library.read"], scopes);
    }

    [Fact]
    public async Task ApprovingWithThePhotoItemTickedSendsAllThreePhotoScopes()
    {
        await using var host = await PairingHost.StartAsync(["library.read", "playback.read", .. PhotoScopes]);
        using var client = host.NewClient();

        var scopes = await host.DecideAsync(client, [("scope", "library.read"), ("photos", "allow")]);

        Assert.Equal(["library.read", .. PhotoScopes], scopes);
    }

    [Fact]
    public async Task DenyingSendsADenialTheEngineCanNeverReadAsAGrant()
    {
        await using var host = await PairingHost.StartAsync(["library.read", .. PhotoScopes]);
        using var client = host.NewClient();

        await host.DecideAsync(client, [], decision: "deny");

        Assert.False(host.LastApproved);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("on")]
    [InlineData("ALLOW")]
    [InlineData("true")]
    public void OnlyTheExactTickGrantsPhotoAccess(string? photosField)
    {
        var scopes = ClientApiEdgeEndpoints.ApprovedScopes(["library.read"], photosField);

        Assert.Equal(["library.read"], scopes);
    }

    [Fact]
    public void TheExactTickAddsThePhotoScopesOnceAfterTheOrdinaryOnes()
    {
        var scopes = ClientApiEdgeEndpoints.ApprovedScopes(["library.read", "view.galleries.read", " ", null], "allow");

        Assert.Equal(["library.read", .. PhotoScopes], scopes);
    }

    /// <summary>A Dashboard with the app door open in front of a stub Engine that answers the review and records the decision.</summary>
    private sealed class PairingHost : IAsyncDisposable
    {
        private readonly WebApplication _dashboard;
        private readonly WebApplication _engine;
        private readonly Uri _address;
        private (string[] Scopes, bool Approved) _received = ([], false);

        private PairingHost(WebApplication dashboard, WebApplication engine, Uri address)
        {
            _dashboard = dashboard;
            _engine = engine;
            _address = address;
        }

        public bool LastApproved => _received.Approved;

        public HttpClient NewClient() => new(new HttpClientHandler { UseCookies = true }) { BaseAddress = _address };

        public static async Task<PairingHost> StartAsync(string[] requested)
        {
            PairingHost? host = null;
            var engineBuilder = WebApplication.CreateBuilder();
            engineBuilder.WebHost.UseUrls("http://127.0.0.1:0");
            var engine = engineBuilder.Build();
            engine.Run(async context =>
            {
                if (context.Request.Path.StartsWithSegments("/api/v1/pairing/review"))
                {
                    await context.Response.WriteAsJsonAsync(new PairingReviewResponse
                    {
                        RequestId = Guid.NewGuid(),
                        ClientId = "tuvima-mobile",
                        ClientName = "Tuvima",
                        ClientVersion = "1.0",
                        DeviceName = "Mary's phone",
                        DeviceClass = "mobile",
                        RequestedScopes = requested,
                        ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
                    });
                    return;
                }

                var body = await context.Request.ReadFromJsonAsync<PairingDecisionRequest>();
                host!._received = (body!.Scopes.ToArray(), body.Approved);
                context.Response.StatusCode = StatusCodes.Status200OK;
            });
            await engine.StartAsync();
            var engineAddress = AddressOf(engine);

            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.AddAntiforgery();
            builder.Services.AddSingleton<INativeAppAccessGate>(new OpenGate());
            builder.Services.AddSingleton<NativeAppPairingThrottle>();
            builder.Services.AddClientApiProxyClient(engineAddress);
            builder.Services.AddHttpClient("EngineIdentity", client => client.BaseAddress = engineAddress);
            var dashboard = builder.Build();
            dashboard.MapClientApiEdge();
            await dashboard.StartAsync();

            host = new PairingHost(dashboard, engine, AddressOf(dashboard));
            return host;
        }

        /// <summary>Opens the screen, posts the decision with the screen's own anti-forgery token, and returns the scopes the Engine received.</summary>
        public async Task<string[]> DecideAsync(HttpClient client, (string Name, string Value)[] fields, string decision = "approve")
        {
            var html = await client.GetStringAsync("/pair?user_code=ABCD-EFGH");
            var token = Regex.Match(html, "name=\"__RequestVerificationToken\" value=\"([^\"]+)\"").Groups[1].Value;
            var form = new List<KeyValuePair<string, string>>
            {
                new("__RequestVerificationToken", WebUtility.HtmlDecode(token)),
                new("user_code", "ABCD-EFGH"),
                new("decision", decision),
            };
            form.AddRange(fields.Select(field => new KeyValuePair<string, string>(field.Name, field.Value)));
            using var response = await client.PostAsync("/pair", new FormUrlEncodedContent(form));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return _received.Scopes;
        }

        public async ValueTask DisposeAsync()
        {
            await _dashboard.StopAsync();
            await _dashboard.DisposeAsync();
            await _engine.StopAsync();
            await _engine.DisposeAsync();
        }

        private static Uri AddressOf(WebApplication app) =>
            new(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
    }

    private sealed class OpenGate : INativeAppAccessGate
    {
        public bool IsEnabled => true;
    }
}
