using System.Net;
using System.Net.Http.Headers;
using MediaEngine.Api.DependencyInjection;
using MediaEngine.Api.Endpoints;
using MediaEngine.Api.Security;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Identity.Contracts;
using MediaEngine.Storage;
using MediaEngine.Storage.Contracts;
using MediaEngine.TestSupport;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace MediaEngine.Api.Tests;

/// <summary>
/// Away-from-home access and paired apps (spec remote-access D6): while it is off, a paired app's token and its refresh-token exchange
/// are refused from clearly outside (forwarded, via the Dashboard's own reading, or from a public address), the same tokens keep
/// working at home and from a place that is only "not known to be home" (a Docker bridge address), and nothing is revoked. The host
/// uses the Engine's own sign-in handler and data store; each request says where it comes from.
/// </summary>
public sealed class DeviceTokenOutsideAccessTests : IDisposable
{
    private const string ClientId = "tuvima-phone";
    private const string RemoteHeader = "X-Test-Remote";
    private const string LanAddress = "192.168.1.50";
    private const string DockerGateway = "172.17.0.1";
    private const string PublicAddress = "8.8.8.8";
    private const string TailscaleAddress = "100.101.102.103";

    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"tuvima_device_outside_{Guid.NewGuid():N}.db");
    private readonly string _configPath = Path.Combine(Path.GetTempPath(), $"tuvima_device_outside_cfg_{Guid.NewGuid():N}");
    private readonly DatabaseConnection _database;

    public DeviceTokenOutsideAccessTests()
    {
        DapperConfiguration.Configure();
        _database = new DatabaseConnection(_databasePath);
        _database.InitializeSchema();
        _database.RunStartupChecks();
    }

    [Fact]
    public async Task APairedApp_WorksAtHome_WhileOutsideAccessIsOff()
    {
        await using var host = await StartAsync(outsideAccessOn: false);
        var paired = await host.PairAsync();

        using var atHome = await host.ProbeAsync(paired.AccessToken, LanAddress);

        Assert.Equal(HttpStatusCode.OK, atHome.StatusCode);
    }

    [Theory]
    [InlineData(PublicAddress, null, false)]
    [InlineData(LanAddress, "203.0.113.9", false)] // a forwarding header: a tunnel or proxy is relaying someone
    [InlineData(LanAddress, null, true)] // the Dashboard's own reading says clearly outside
    public async Task APairedApp_IsRefusedFromClearlyOutside_WhileOutsideAccessIsOff(string remote, string? forwardedFor, bool dashboardSaysOutside)
    {
        await using var host = await StartAsync(outsideAccessOn: false);
        var paired = await host.PairAsync();

        using var refused = await host.ProbeAsync(paired.AccessToken, remote, forwardedFor, dashboardSaysOutside ? ClientIngress.Outside : null);

        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
    }

    [Theory]
    [InlineData(DockerGateway)]
    [InlineData(TailscaleAddress)]
    public async Task APairedApp_IsNotTurnedAwayForAPlaceThatIsOnlyNotKnownToBeHome_WhileOutsideAccessIsOff(string remote)
    {
        await using var host = await StartAsync(outsideAccessOn: false);
        var paired = await host.PairAsync();

        using var response = await host.ProbeAsync(paired.AccessToken, remote, dashboardIngress: ClientIngress.Remote);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task APairedApp_WorksFromOutside_WhileOutsideAccessIsOn()
    {
        await using var host = await StartAsync(outsideAccessOn: true);
        var paired = await host.PairAsync();

        using var response = await host.ProbeAsync(paired.AccessToken, PublicAddress, "203.0.113.9", ClientIngress.Outside);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task TheRefreshTokenExchange_IsRefusedFromOutside_WhileOff_RevokesNothing_AndWorksAtHome()
    {
        await using var host = await StartAsync(outsideAccessOn: false);
        var paired = await host.PairAsync();

        using var fromOutside = await host.RefreshAsync(paired.RefreshToken, PublicAddress);
        using var forwarded = await host.RefreshAsync(paired.RefreshToken, LanAddress, forwardedFor: "203.0.113.9");
        using var atHome = await host.RefreshAsync(paired.RefreshToken, LanAddress);

        Assert.Equal(HttpStatusCode.BadRequest, fromOutside.StatusCode);
        Assert.Contains("access_denied", await fromOutside.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.BadRequest, forwarded.StatusCode);
        Assert.Equal(HttpStatusCode.OK, atHome.StatusCode); // the refused attempts did not spend or revoke the refresh token
    }

    [Fact]
    public async Task SwitchingOutsideAccessOff_RevokesNoPairedApp_TheTokenWorksAgainAtHome()
    {
        await using var host = await StartAsync(outsideAccessOn: false);
        var paired = await host.PairAsync();

        using var refused = await host.ProbeAsync(paired.AccessToken, PublicAddress);
        using var atHome = await host.ProbeAsync(paired.AccessToken, LanAddress);

        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        Assert.Equal(HttpStatusCode.OK, atHome.StatusCode);
        var devices = await host.Services.GetRequiredService<ClientAuthorizationService>().GetDevicesAsync(paired.ProfileId);
        Assert.All(devices, device => Assert.True(device.IsActive));
    }

    private async Task<Host> StartAsync(bool outsideAccessOn)
    {
        Directory.CreateDirectory(_configPath);
        await File.WriteAllTextAsync(
            Path.Combine(_configPath, "network.json"),
            outsideAccessOn ? """{ "remote": { "connection_mode": "tailscale" } }""" : """{ "remote": { "connection_mode": "local-only" } }""");

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Development });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Host.UseDefaultServiceProvider(options => { options.ValidateOnBuild = false; options.ValidateScopes = false; });
        builder.Services.AddLogging();
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddHttpClient();
        builder.Services.AddMemoryCache();
        builder.Services.AddRateLimiter(_ => { });
        builder.Services.AddSingleton<IDatabaseConnection>(_database);
        builder.Services.AddSingleton<IConfigurationLoader>(new ConfigurationDirectoryLoader(_configPath));
        builder.Services.AddTuvimaStorage();
        builder.Services.AddTuvimaSecurity();
        builder.Services.AddDataProtection();
        builder.Services.AddSingleton<ITwoStepSecretProtector, DataProtectionTwoStepSecretProtector>();
        builder.Services.AddSingleton<IPermissionRegistry, PermissionRegistry>();
        builder.Services.AddScoped<IRequestAuthorityResolver, RequestAuthorityResolver>();
        builder.Services.AddAuthentication(TuvimaAuthDefaults.Scheme)
            .AddScheme<AuthenticationSchemeOptions, TuvimaAuthenticationHandler>(TuvimaAuthDefaults.Scheme, _ => { });
        builder.Services.AddAuthorization(options =>
        {
            options.AddPolicy(AuthPolicies.Authenticated, policy => policy.RequireAuthenticatedUser());
            options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
        });
        foreach (var worker in builder.Services
                     .Where(descriptor => descriptor.ServiceType == typeof(IHostedService)
                         && descriptor.ImplementationType?.Name != "GenericWebHostService")
                     .ToList())
        {
            builder.Services.Remove(worker);
        }

        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            if (context.Request.Headers.TryGetValue(RemoteHeader, out var remote) && IPAddress.TryParse(remote.ToString(), out var address))
            {
                context.Connection.RemoteIpAddress = address;
            }

            await next();
        });
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapGet("/probe/signed-in", () => Results.Ok("signed in")).RequireAuthorization(AuthPolicies.Authenticated);
        app.MapClientAuthorizationEndpoints();

        using (var connection = _database.CreateConnection())
        {
            Dapper.SqlMapper.Execute(connection, "UPDATE onboarding_workflows SET state = 'complete';");
        }

        await app.StartAsync();
        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>();
        return new Host(app, new Uri(addresses!.Addresses.Single()));
    }

    public void Dispose()
    {
        TestTemp.ReleaseSqlitePool(_databasePath);
        try { _database.Dispose(); } catch (ObjectDisposedException) { /* Already closed by the pool sweep. */ }
        try { File.Delete(_databasePath); } catch (IOException) { /* The OS temp sweep removes a handle SQLite releases late. */ }
        try { Directory.Delete(_configPath, recursive: true); } catch (IOException) { /* Already gone or still held. */ }
    }

    private sealed record PairedApp(string AccessToken, string RefreshToken, Guid ProfileId);

    private sealed class Host(WebApplication app, Uri address) : IAsyncDisposable
    {
        private readonly HttpClient _client = new() { BaseAddress = address };

        public IServiceProvider Services => app.Services;

        public async Task<PairedApp> PairAsync()
        {
            var identity = Services.GetRequiredService<IFirstPartyIdentityService>();
            var owner = await identity.BootstrapAdministratorAsync(
                "owner@example.com", "correct horse battery staple", "Owner", "phone", "Phone", "Dashboard", ingress: ClientIngress.HomeNetwork);

            var applications = Services.GetRequiredService<IApplicationRepository>();
            await applications.ReplacePermissionsAsync(
                BuiltInApplicationIds.NativeClient,
                new HashSet<ApplicationPermissionId> { ApplicationPermissionIds.LibraryRead },
                DateTimeOffset.UtcNow);
            await applications.ReplaceClientBindingsAsync(
                BuiltInApplicationIds.NativeClient,
                new HashSet<string>(StringComparer.Ordinal) { ClientId },
                DateTimeOffset.UtcNow);

            var authorization = Services.GetRequiredService<ClientAuthorizationService>();
            var started = await authorization.BeginAsync(new DeviceAuthorizationRequest
            {
                ClientId = ClientId,
                ClientName = "Test app",
                ClientVersion = "1.0.0",
                DeviceName = "Phone",
                DeviceClass = "mobile",
                Scope = "library.read",
                Capabilities = new ClientCapabilitiesDto { Containers = ["mp4"], VideoCodecs = ["h264"], AudioCodecs = ["aac"] },
            }, "https://library.example");
            Assert.True(await authorization.DecideAsync(
                new PairingDecisionRequest { UserCode = started.UserCode, Approved = true, Scopes = ["library.read"] },
                owner.Account.Id, owner.Profile.Id, owner.Profile.Id));
            var issued = await authorization.ExchangeAsync(new OAuthTokenRequest
            {
                GrantType = ClientAuthorizationService.DeviceGrantType,
                ClientId = ClientId,
                DeviceCode = started.DeviceCode,
            });
            var tokens = Assert.IsType<OAuthTokenResponse>(issued.Success);
            return new PairedApp(tokens.AccessToken, tokens.RefreshToken, owner.Profile.Id);
        }

        public Task<HttpResponseMessage> ProbeAsync(string accessToken, string remote, string? forwardedFor = null, string? dashboardIngress = null)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, "/probe/signed-in");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            Describe(request, remote, forwardedFor, dashboardIngress);
            return _client.SendAsync(request);
        }

        public Task<HttpResponseMessage> RefreshAsync(string refreshToken, string remote, string? forwardedFor = null)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/oauth/token")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = ClientAuthorizationService.RefreshGrantType,
                    ["client_id"] = ClientId,
                    ["refresh_token"] = refreshToken,
                }),
            };
            Describe(request, remote, forwardedFor, dashboardIngress: null);
            return _client.SendAsync(request);
        }

        private static void Describe(HttpRequestMessage request, string remote, string? forwardedFor, string? dashboardIngress)
        {
            request.Headers.Add(RemoteHeader, remote);
            if (forwardedFor is not null)
            {
                request.Headers.Add("X-Forwarded-For", forwardedFor);
            }

            if (dashboardIngress is not null)
            {
                request.Headers.Add(ClientIngressValues.ValidateHeader, dashboardIngress);
            }
        }

        public async ValueTask DisposeAsync()
        {
            _client.Dispose();
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
}
