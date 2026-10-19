using System.Net;
using MediaEngine.Web.Endpoints;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

// A signed-out icon fetch used to fall through to the sign-in page, and on this computer every visit there started a new
// Engine session; enough of them ended the session an open screen was using.
public sealed class FaviconEndpointTests
{
    [Fact]
    public async Task FaviconIco_IsTheIconForASignedOutVisitor_NotARedirectToSignIn()
    {
        var webRoot = Directory.CreateTempSubdirectory("tuvima-favicon-");
        try
        {
            var icon = new byte[] { 0x89, 0x50, 0x4E, 0x47 };
            await File.WriteAllBytesAsync(Path.Combine(webRoot.FullName, "favicon.png"), icon);
            await using var dashboard = await StartDashboardAsync(webRoot.FullName);
            using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { BaseAddress = dashboard.Address };

            using var response = await client.GetAsync(FaviconEndpoint.Path);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);
            Assert.Equal(icon, await response.Content.ReadAsByteArrayAsync());
        }
        finally
        {
            webRoot.Delete(recursive: true);
        }
    }

    [Fact]
    public void Dashboard_MapsTheIconEndpoint()
    {
        var source = File.ReadAllText(RepoPath("src/MediaEngine.Web/Program.cs"));

        Assert.Contains("app.MapFaviconIco();", source, StringComparison.Ordinal);
    }

    private static async Task<TestApplication> StartDashboardAsync(string webRoot)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { WebRootPath = webRoot });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(options => options.LoginPath = "/auth/login");
        builder.Services.AddAuthorization(options =>
        {
            options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
        });

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapFaviconIco();
        await app.StartAsync();

        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>();
        return new(app, new Uri(addresses!.Addresses.Single()));
    }

    private static string RepoPath(string relative)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, relative.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException(relative);
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
