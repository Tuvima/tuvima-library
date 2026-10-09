using System.Net;
using MediaEngine.Web.Endpoints;
using MediaEngine.Web.Services.Integration;
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
// anonymous "not found" page to see whether the re-run happened.
public sealed class DoorStatusCodePagesTests
{
    private const string NotFoundPageBody = "Custom not found page";

    [Theory]
    [InlineData("/.well-known/tuvima")]
    [InlineData("/api/v1/display/home")]
    [InlineData("/API/V1/DISPLAY/HOME")]
    [InlineData("/application-events/hub")]
    public async Task ClosedDoor_AnswersItsOwnNotFoundWithoutTheNotFoundPage(string path)
    {
        await using var dashboard = await StartDashboardAsync();
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = dashboard.Address };

        using var response = await client.GetAsync(path);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Null(response.Headers.Location);
        Assert.DoesNotContain(NotFoundPageBody, body);
    }

    [Theory]
    [InlineData("/api/v1x/display/home")]
    [InlineData("/pairing-help")]
    [InlineData("/missing-on-purpose")]
    public async Task OtherMissingPages_StillUseTheNotFoundPage(string path)
    {
        await using var dashboard = await StartDashboardAsync();
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = dashboard.Address };

        using var response = await client.GetAsync(path);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains(NotFoundPageBody, body);
    }

    // No endpoint at all: the sign-in fallback applies first, exactly as it did before this change.
    [Fact]
    public async Task UnmatchedPage_StillSendsAnonymousVisitorToSignIn()
    {
        await using var dashboard = await StartDashboardAsync();
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = dashboard.Address };

        using var response = await client.GetAsync("/no-such-page");

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
        builder.Services.AddSingleton<INativeAppAccessGate>(new ClosedGate());

        var app = builder.Build();
        app.UseStatusCodePagesExceptDoors("/not-found");
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapClientApiEdge();
        app.MapGet("/not-found", () => Results.Text(NotFoundPageBody, "text/plain")).AllowAnonymous();
        // Non-door pages that answer 404 on purpose; their not-found re-run must still happen.
        app.MapGet("/missing-on-purpose", () => Results.NotFound()).AllowAnonymous();
        app.MapGet("/pairing-help", () => Results.NotFound()).AllowAnonymous();
        app.MapGet("/api/v1x/{**rest}", () => Results.NotFound()).AllowAnonymous();
        await app.StartAsync();

        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>();
        return new(app, new Uri(addresses!.Addresses.Single()));
    }

    private sealed class ClosedGate : INativeAppAccessGate
    {
        public bool IsEnabled => false;
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
