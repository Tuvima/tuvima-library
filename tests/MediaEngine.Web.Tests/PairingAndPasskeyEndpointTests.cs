using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using MediaEngine.Web.Endpoints;
using MediaEngine.Web.Services.Configuration;
using MediaEngine.Web.Services.Integration;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

public sealed class PairingAndPasskeyEndpointTests : IDisposable
{
    private const string PairingBody =
        "{\"device_code\":\"abc\",\"verification_uri\":\"http://127.0.0.1:61495/pair\",\"verification_uri_complete\":\"http://127.0.0.1:61495/pair?user_code=ABCD\"}";

    private readonly string _configDirectory = Path.Combine(Path.GetTempPath(), $"tuvima-pair-{Guid.NewGuid():N}");

    public PairingAndPasskeyEndpointTests() => Directory.CreateDirectory(_configDirectory);

    public void Dispose()
    {
        if (Directory.Exists(_configDirectory))
        {
            Directory.Delete(_configDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task PairingResponse_IsRewrittenToThePublicAddress_WhenOneIsSet()
    {
        SetPublicAddress("https://tuvima.example.com");
        await using var engine = await StartAsync(app => app.Run(context =>
        {
            context.Response.ContentType = "application/json";
            return context.Response.WriteAsync(PairingBody);
        }));
        await using var dashboard = await StartAsync(app => app.MapClientApiEdge(), engine.Address, withAuthEndpoints: false);

        using var response = await new HttpClient().PostAsync(new Uri(dashboard.Address, "/api/v1/oauth/device_authorization"), new StringContent("{}"));
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;

        Assert.Equal("https://tuvima.example.com/pair", (string?)body["verification_uri"]);
        Assert.Equal("https://tuvima.example.com/pair?user_code=ABCD", (string?)body["verification_uri_complete"]);
    }

    [Fact]
    public async Task PairingResponse_WithNoPublicAddress_UsesTheDashboardsOwnOrigin()
    {
        await using var engine = await StartAsync(app => app.Run(context =>
        {
            context.Response.ContentType = "application/json";
            return context.Response.WriteAsync(PairingBody);
        }));
        await using var dashboard = await StartAsync(app => app.MapClientApiEdge(), engine.Address, withAuthEndpoints: false);

        using var response = await new HttpClient().PostAsync(new Uri(dashboard.Address, "/api/v1/oauth/device_authorization"), new StringContent("{}"));
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;

        Assert.Equal(new Uri(dashboard.Address, "/pair").ToString(), (string?)body["verification_uri"]);
    }

    [Fact]
    public async Task PairingRewrite_IsSkippedOnAnErrorResponse()
    {
        SetPublicAddress("https://tuvima.example.com");
        const string error = "{\"error\":\"invalid_request\",\"verification_uri\":\"http://127.0.0.1:61495/pair\"}";
        await using var engine = await StartAsync(app => app.Run(context =>
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            context.Response.ContentType = "application/json";
            return context.Response.WriteAsync(error);
        }));
        await using var dashboard = await StartAsync(app => app.MapClientApiEdge(), engine.Address, withAuthEndpoints: false);

        using var response = await new HttpClient().PostAsync(new Uri(dashboard.Address, "/api/v1/oauth/device_authorization"), new StringContent("{}"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(error, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task PairingResponse_ThatIsNotTheExpectedShape_PassesThroughUnchanged()
    {
        SetPublicAddress("https://tuvima.example.com");
        await using var engine = await StartAsync(app => app.Run(context =>
        {
            context.Response.ContentType = "application/json";
            return context.Response.WriteAsync("{\"verification_uri\": 5");
        }));
        await using var dashboard = await StartAsync(app => app.MapClientApiEdge(), engine.Address, withAuthEndpoints: false);

        using var response = await new HttpClient().PostAsync(new Uri(dashboard.Address, "/api/v1/oauth/device_authorization"), new StringContent("{}"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("{\"verification_uri\": 5", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task PasskeyLoginOptions_AreRefusedOffThePublicOrigin()
    {
        SetPublicAddress("https://tuvima.example.com");
        await using var dashboard = await StartAsync(_ => { }, engine: null, withAuthEndpoints: true);

        using var response = await new HttpClient().PostAsJsonAsync(new Uri(dashboard.Address, "/auth/passkeys/login/options"), new { email = "a@example.com" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExpiredLoginPage_ShowsThePasskeyButtonOnlyAtThePublicOrigin(bool atPublicOrigin)
    {
        SetPublicAddress("https://tuvima.example.com");
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAntiforgery();
        services.AddSingleton(new DashboardConfigurationReader(_configDirectory));
        await using var provider = services.BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = provider };
        context.Request.Method = HttpMethods.Post;
        context.Request.Scheme = atPublicOrigin ? "https" : "http";
        context.Request.Host = atPublicOrigin ? new HostString("tuvima.example.com") : new HostString("192.168.1.20", 5016);
        context.Request.ContentType = "application/x-www-form-urlencoded";
        context.Request.Body = new MemoryStream();
        context.Response.Body = new MemoryStream();

        var result = await DashboardAuthenticationEndpoints.RefreshInvalidLoginFormAsync(
            context, provider.GetRequiredService<IAntiforgery>(), []);
        await result!.ExecuteAsync(context);
        context.Response.Body.Position = 0;
        var html = Encoding.UTF8.GetString(((MemoryStream)context.Response.Body).ToArray());

        Assert.Contains("name=\"password\"", html);
        Assert.Equal(atPublicOrigin, html.Contains("id=\"passkey-login\"", StringComparison.Ordinal));
    }

    private void SetPublicAddress(string address) =>
        File.WriteAllText(
            Path.Combine(_configDirectory, "network.json"),
            $"{{\"remote\":{{\"public_hostname\":\"{address}\"}}}}");

    private async Task<TestServer> StartAsync(Action<WebApplication> configure, Uri? engine = null, bool withAuthEndpoints = false)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddAntiforgery();
        builder.Services.AddSingleton(new DashboardConfigurationReader(_configDirectory));
        if (engine is not null)
        {
            builder.Services.AddSingleton<INativeAppAccessGate>(new EnabledGate());
            builder.Services.AddSingleton<NativeAppPairingThrottle>();
            builder.Services.AddClientApiProxyClient(engine);
            builder.Services.AddHttpClient("EngineIdentity", client => client.BaseAddress = engine);
        }

        if (withAuthEndpoints)
        {
            builder.Services.AddAuthentication().AddCookie();
            builder.Services.AddAuthorization();
            builder.Services.AddSingleton(new MediaEngine.Domain.Configuration.PasswordResetDeliverySettings());
            builder.Services.AddSingleton<PasswordResetEmailSender>();
            builder.Services.AddHttpClient();
            builder.Services.AddHttpClient("EngineIdentity", client => client.BaseAddress = new Uri("http://127.0.0.1:1"));
            builder.Services.AddSingleton(new SignInAttemptLimiter(new IngressClassifier(proxyPort: null, trustedLocalNetworks: null)));
            builder.Services.AddScoped(sp => new DashboardIdentityClient(sp.GetRequiredService<IHttpClientFactory>()));
        }

        var app = builder.Build();
        if (withAuthEndpoints)
        {
            app.UseAuthentication();
            app.UseAuthorization();
        }

        configure(app);
        if (withAuthEndpoints)
        {
            app.MapDashboardAuthenticationEndpoints([]);
        }

        await app.StartAsync();
        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>();
        return new TestServer(app, new Uri(addresses!.Addresses.Single()));
    }

    private sealed class EnabledGate : INativeAppAccessGate
    {
        public bool IsEnabled => true;
    }

    private sealed class TestServer(WebApplication app, Uri address) : IAsyncDisposable
    {
        public Uri Address { get; } = address;

        public async ValueTask DisposeAsync()
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
}
