using System.Net;
using MediaEngine.Web.Endpoints;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

// Mirrors the production order in Program.cs: the status-code re-run sits before authentication.
// The sign-in-protected fallback applies to anything without an endpoint, so these tests use an
// anonymous "not found" page to see whether the re-run happened. The app door is always open, so the
// only 404 it answers is for a path it refuses to forward.
public sealed class DoorStatusCodePagesTests
{
    [Theory]
    [InlineData("/api/v1/analytics/playback")]
    [InlineData("/API/V1/ANALYTICS/PLAYBACK")]
    [InlineData("/application-events/a//b")]
    public async Task Door_AnswersItsOwnNotFoundForAPathItWillNotForward_WithoutTheNotFoundPage(string path)
    {
        await using var dashboard = await StartDashboardAsync();
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = dashboard.Address };
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "app-token");

        using var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Null(response.Headers.Location);
        Assert.DoesNotContain("Custom not found page", body);
    }

    // Signed-out visitors to any non-door 404 are sent to sign-in, as before this change: the not-found
    // re-run needs a signed-in visitor. Door paths never reach that re-run (see ClosedDoor above).
    [Theory]
    [InlineData("/api/v1x/display/home")]
    [InlineData("/pairing-help")]
    [InlineData("/missing-on-purpose")]
    [InlineData("/no-such-page")]
    public async Task NonDoorNotFound_StillSendsSignedOutVisitorToSignIn(string path)
    {
        await using var dashboard = await StartDashboardAsync();
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = dashboard.Address };

        using var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.NotNull(response.Headers.Location);
    }

    private static async Task<TestApplication> StartDashboardAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddAntiforgery();
        builder.Services.AddHttpClient();
        builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie();
        builder.Services.AddAuthorization(options =>
        {
            options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
        });

        var app = builder.Build();
        app.UseStatusCodePagesExceptDoors("/not-found");
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapClientApiEdge();
        app.MapGet("/not-found", () => Results.Text("Custom not found page", "text/plain")).AllowAnonymous();
        // Non-door pages that answer 404 on purpose.
        app.MapGet("/missing-on-purpose", () => Results.NotFound()).AllowAnonymous();
        app.MapGet("/pairing-help", () => Results.NotFound()).AllowAnonymous();
        app.MapGet("/api/v1x/{**rest}", () => Results.NotFound()).AllowAnonymous();
        await app.StartAsync();

        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>();
        return new(app, new Uri(addresses!.Addresses.Single()));
    }

    private sealed class TestApplication(WebApplication app, Uri address) : IAsyncDisposable
    {
        public Uri Address { get; } = address;

        public async ValueTask DisposeAsync()
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
}
