using System.Net;
using MediaEngine.Web.Endpoints;
using MediaEngine.Web.Services.Integration;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

public sealed class DoorStatusCodePagesTests
{
    [Theory]
    [InlineData("/.well-known/tuvima")]
    [InlineData("/pair")]
    [InlineData("/api/v1/display/home")]
    public async Task ClosedDoor_AnswersItsOwnNotFoundNotASignInRedirect(string path)
    {
        await using var dashboard = await StartDashboardAsync();
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = dashboard.Address };

        using var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Null(response.Headers.Location);
    }

    [Fact]
    public async Task OtherMissingPages_StillGoThroughTheSignInProtectedNotFoundPage()
    {
        await using var dashboard = await StartDashboardAsync();
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = dashboard.Address };

        using var response = await client.GetAsync("/no-such-page");

        // The not-found page needs a sign-in, so an anonymous visitor is redirected there.
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
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseStatusCodePagesExceptDoors("/not-found");
        app.MapClientApiEdge();
        app.MapGet("/not-found", () => Results.Text("Not found", "text/plain"));
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
