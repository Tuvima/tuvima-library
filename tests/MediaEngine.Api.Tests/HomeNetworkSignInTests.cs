using System.Net;
using System.Security.Cryptography;
using System.Text;
using MediaEngine.Api.DependencyInjection;
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
/// Home network spec D8 (review mock-ups of 2026-11-18): the Dashboard listens on the home network, so on that network anyone must
/// sign in to do more than view, and the computer itself is no more trusted than any other device. The host here uses the Engine's own
/// sign-in handler, policies and data store, in the Development environment (the only place the old localhost shortcut ever worked),
/// and lets each request claim where it comes from.
/// </summary>
public sealed class HomeNetworkSignInTests : IDisposable
{
    private const string ServiceKey = "home-network-sign-in-service-key";
    private const string RemoteHeader = "X-Test-Remote";
    private const string LanAddress = "192.168.1.50";
    private const string LoopbackAddress = "127.0.0.1";

    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"tuvima_home_network_{Guid.NewGuid():N}.db");
    private readonly string _configPath = Path.Combine(Path.GetTempPath(), $"tuvima_home_network_cfg_{Guid.NewGuid():N}");
    private readonly DatabaseConnection _database;

    public HomeNetworkSignInTests()
    {
        DapperConfiguration.Configure();
        _database = new DatabaseConnection(_databasePath);
        _database.InitializeSchema();
        _database.RunStartupChecks();
    }

    [Theory]
    [InlineData(LanAddress)]
    [InlineData(LoopbackAddress)]
    public async Task AnAdminAction_WithNoSignIn_IsRefused_FromTheHomeNetworkAndFromThisComputerAlike(string remote)
    {
        await using var app = await StartAsync();

        using var admin = await app.SendAsync("/probe/admin", remote);
        using var anyone = await app.SendAsync("/probe/signed-in", remote);

        Assert.Equal(HttpStatusCode.Unauthorized, admin.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, anyone.StatusCode);
    }

    [Fact]
    public async Task LocalhostInDevelopment_GrantsNothing_EvenWithTheOldBypassSettingLeftInTheConfigFile()
    {
        Directory.CreateDirectory(_configPath);
        await File.WriteAllTextAsync(
            Path.Combine(_configPath, "core.json"),
            """{ "auth": { "mode": "Local", "localhost_bypass": true } }""");
        await using var app = await StartAsync();

        using var admin = await app.SendAsync("/probe/admin", LoopbackAddress);

        Assert.Equal(HttpStatusCode.Unauthorized, admin.StatusCode);
    }

    [Theory]
    [InlineData(LanAddress)]
    [InlineData(LoopbackAddress)]
    public async Task TheDashboardAloneWithNoPersonSignedIn_CannotAdminister_FromAnywhere(string remote)
    {
        await using var app = await StartAsync();

        using var admin = await app.SendAsync("/probe/admin", remote, serviceKey: true);

        Assert.False(admin.IsSuccessStatusCode, "A Dashboard request with nobody signed in must not reach an administrator action.");
    }

    [Fact]
    public async Task AnAdministratorWithAPassword_CanSignInAndAdminister_FromTheHomeNetwork()
    {
        await using var app = await StartAsync();
        var identity = app.Services.GetRequiredService<IFirstPartyIdentityService>();
        var issued = await identity.BootstrapAdministratorAsync(
            "owner@example.com", "correct horse battery staple", "Owner", "phone", "Phone", "Dashboard");

        using var admin = await app.SendAsync(
            "/probe/admin", LanAddress, serviceKey: true, session: issued.PlaintextToken, ingress: ClientIngressValues.HomeNetwork);

        Assert.Equal(HttpStatusCode.OK, admin.StatusCode);
    }

    [Fact]
    public async Task TheNoPasswordAdministrator_WorksOnThisComputer_ButAPhoneOnTheHomeNetworkCannotUseItsSession()
    {
        await using var app = await StartAsync();
        var identity = app.Services.GetRequiredService<IFirstPartyIdentityService>();
        var issued = await identity.BootstrapThisComputerAdministratorAsync("owner@example.com", "Owner", "laptop", "Laptop", "Dashboard");

        using var onThisComputer = await app.SendAsync(
            "/probe/admin", LoopbackAddress, serviceKey: true, session: issued.PlaintextToken, ingress: ClientIngressValues.ThisComputer);
        using var onTheHomeNetwork = await app.SendAsync(
            "/probe/admin", LanAddress, serviceKey: true, session: issued.PlaintextToken, ingress: ClientIngressValues.HomeNetwork);
        using var saysNothing = await app.SendAsync(
            "/probe/admin", LanAddress, serviceKey: true, session: issued.PlaintextToken);

        Assert.Equal(HttpStatusCode.OK, onThisComputer.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, onTheHomeNetwork.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, saysNothing.StatusCode);
    }

    [Fact]
    public async Task NoPasswordSignIn_IsNotOfferedToAPhoneOnTheHomeNetwork_ThroughTheIdentityService()
    {
        await using var app = await StartAsync();
        var identity = app.Services.GetRequiredService<IFirstPartyIdentityService>();
        await identity.BootstrapThisComputerAdministratorAsync("owner@example.com", "Owner", "laptop", "Laptop", "Dashboard");

        Assert.Null(await identity.SignInThisComputerAccountAsync("phone", "Phone", "Dashboard", ClientIngress.HomeNetwork));
        Assert.NotNull(await identity.SignInThisComputerAccountAsync("laptop2", "Laptop", "Dashboard", ClientIngress.ThisComputer));
    }

    [Fact]
    public void NothingInTheShippedEngineOrSettingsStillGrantsAccessByLocalhost()
    {
        var root = FindRepoRoot();
        var handler = File.ReadAllText(Path.Combine(root, "src", "MediaEngine.Api", "Security", "TuvimaAuthentication.cs"));

        // The handler reads credentials only: it holds no environment check, no loopback check and no configuration.
        Assert.DoesNotContain("IsDevelopment", handler, StringComparison.Ordinal);
        Assert.DoesNotContain("IsLoopback", handler, StringComparison.Ordinal);
        Assert.DoesNotContain("HostRecovery", handler, StringComparison.Ordinal);
        Assert.DoesNotContain("development:localhost", handler, StringComparison.Ordinal);
        Assert.DoesNotContain("RemoteIpAddress", handler, StringComparison.Ordinal);

        foreach (var folder in new[] { "MediaEngine.Api", "MediaEngine.Domain", "MediaEngine.Contracts", "MediaEngine.Web" })
        {
            foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "src", folder), "*.*", SearchOption.AllDirectories)
                         .Where(path => path.EndsWith(".cs", StringComparison.Ordinal) || path.EndsWith(".razor", StringComparison.Ordinal))
                         .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                             && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)))
            {
                var text = File.ReadAllText(file);
                Assert.False(
                    text.Contains("LocalhostBypass", StringComparison.Ordinal) || text.Contains("localhost_bypass", StringComparison.Ordinal),
                    $"{Path.GetRelativePath(root, file)} still names the retired localhost bypass.");
            }
        }

        foreach (var config in new[] { "config/core.json", "docker/config/core.json" })
        {
            Assert.DoesNotContain("localhost_bypass", File.ReadAllText(Path.Combine(root, config.Replace('/', Path.DirectorySeparatorChar))), StringComparison.Ordinal);
        }
    }

    private async Task<TestApplication> StartAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Development });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        // Development switches on build-time checks of every shipped module; this host builds only what sign-in needs.
        builder.Host.UseDefaultServiceProvider(options => { options.ValidateOnBuild = false; options.ValidateScopes = false; });
        builder.Services.AddLogging();
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddHttpClient();
        builder.Services.AddMemoryCache();
        builder.Services.AddRateLimiter(_ => { });
        builder.Services.AddSingleton<IDatabaseConnection>(_database);
        builder.Services.AddSingleton<IConfigurationLoader>(new ConfigurationDirectoryLoader(_configPath));

        // The Engine's own composition for sign-in and who-may-do-what (Program.cs), over the real data store.
        builder.Services.AddTuvimaStorage();
        builder.Services.AddTuvimaSecurity();
        builder.Services.AddDataProtection();
        builder.Services.AddSingleton<ITwoStepSecretProtector, DataProtectionTwoStepSecretProtector>();
        builder.Services.AddSingleton<IPermissionRegistry, PermissionRegistry>();
        builder.Services.AddScoped<IRequestAuthorityResolver, RequestAuthorityResolver>();
        builder.Services.AddScoped<IGrantAdminUnlockService, GrantAdminUnlockService>();
        builder.Services.AddSingleton<IHouseholdLibraryAccessRepository>(new HouseholdLibraryAccessRepository(_database));
        builder.Services.AddSingleton<LibraryVisibilityRule>();
        builder.Services.AddScoped<IAccountAccessDecisionService, AccountAccessDecisionService>();
        builder.Services.AddScoped<IAuthorizationHandler, EffectiveAdministratorHandler>();
        builder.Services.AddAuthentication(TuvimaAuthDefaults.Scheme)
            .AddScheme<AuthenticationSchemeOptions, TuvimaAuthenticationHandler>(TuvimaAuthDefaults.Scheme, _ => { });
        builder.Services.AddAuthorization(options =>
        {
            options.AddPolicy(AuthPolicies.Authenticated, policy => policy.RequireAuthenticatedUser());
            options.AddPolicy(AuthPolicies.Administrator, policy =>
                policy.RequireAuthenticatedUser().AddRequirements(new EffectiveAdministratorRequirement(true)));
            options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
        });

        // The shipped modules also start background workers; only the web server is needed here.
        foreach (var worker in builder.Services
                     .Where(descriptor => descriptor.ServiceType == typeof(IHostedService)
                         && descriptor.ImplementationType?.Name != "GenericWebHostService")
                     .ToList())
        {
            builder.Services.Remove(worker);
        }

        var app = builder.Build();

        // A test connection always arrives from this computer; let each request say where it really comes from.
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
        app.MapGet("/probe/admin", () => Results.Ok("administered")).RequireAuthorization(AuthPolicies.Administrator);
        app.MapGet("/probe/signed-in", () => Results.Ok("signed in")).RequireAuthorization(AuthPolicies.Authenticated);

        // The Dashboard's service key: the sign-in handler reads a person's session only beside it.
        await app.Services.GetRequiredService<IIdentityRepository>().InsertServiceCredentialAsync(new ServiceCredential
        {
            Id = Guid.NewGuid(),
            Purpose = DashboardServiceCredentialOptions.Purpose,
            KeyId = "home-network-test",
            TokenHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(ServiceKey))),
            CreatedAt = DateTimeOffset.UtcNow,
        });

        // Setup is over, so sign-in and the administrator rules behave as on a running server.
        using (var connection = _database.CreateConnection())
        {
            Dapper.SqlMapper.Execute(connection, "UPDATE onboarding_workflows SET state = 'complete';");
        }

        await app.StartAsync();
        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>();
        return new TestApplication(app, new Uri(addresses!.Addresses.Single()));
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MediaEngine.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("The repository root was not found.");
    }

    public void Dispose()
    {
        TestTemp.ReleaseSqlitePool(_databasePath);
        try { _database.Dispose(); } catch (ObjectDisposedException) { /* Already closed by the pool sweep. */ }
        try { File.Delete(_databasePath); } catch (IOException) { /* The OS temp sweep removes a handle SQLite releases late. */ }
        // A test that wrote no configuration never made the folder, and the OS temp sweep removes one that is still held.
        try { Directory.Delete(_configPath, recursive: true); } catch (IOException) { /* Already gone or still held. */ }
    }

    private sealed class TestApplication(WebApplication app, Uri address) : IAsyncDisposable
    {
        private readonly HttpClient _client = new() { BaseAddress = address };

        public IServiceProvider Services => app.Services;

        public Task<HttpResponseMessage> SendAsync(
            string path, string remote, bool serviceKey = false, string? session = null, string? ingress = null)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.Add(RemoteHeader, remote);
            if (serviceKey)
            {
                request.Headers.Add(TuvimaAuthDefaults.ServiceHeader, ServiceKey);
            }

            if (session is not null)
            {
                request.Headers.Add(TuvimaAuthDefaults.SessionHeader, session);
            }

            if (ingress is not null)
            {
                request.Headers.Add(ClientIngressValues.ValidateHeader, ingress);
            }

            return _client.SendAsync(request);
        }

        public async ValueTask DisposeAsync()
        {
            _client.Dispose();
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
}
