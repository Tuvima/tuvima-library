using MediaEngine.Api.DependencyInjection;
using MediaEngine.Api.Endpoints;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services.Networking;
using MediaEngine.Contracts.Settings;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Configuration;
using MediaEngine.Storage;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace MediaEngine.Api.Tests;

/// <summary>The one-click "Fix it for me" for a proxy Library does not trust yet (spec remote-access WP5, D12): safe, repeatable, administrators only.</summary>
public sealed class RemoteFixServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "tuvima-remote-fix-" + Guid.NewGuid().ToString("N"));
    private readonly ConfigurationDirectoryLoader _configuration;

    public RemoteFixServiceTests()
    {
        Directory.CreateDirectory(_directory);
        _configuration = new ConfigurationDirectoryLoader(_directory);
    }

    public void Dispose()
    {
        _configuration.Dispose();
        Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void TrustingTheProxy_TurnsOnTheProxyPort_AndSaysARestartIsNeeded()
    {
        _configuration.SaveNetwork(new NetworkSettings { Remote = { ConnectionMode = NetworkConnectionModes.Custom, PublicHostname = "https://library.example.com" } });

        var result = new RemoteFixService(_configuration).TrustProxy();

        Assert.Equal(RemoteCheckFixActions.TrustProxy, result.Action);
        Assert.True(result.Changed);
        Assert.True(result.RestartRequired);
        Assert.Equal(RemoteFixService.PresetProxyPort, result.ProxyPort);
        Assert.Equal(RemoteFixService.PresetProxyPort, _configuration.LoadNetwork().Remote.ProxyPort);
    }

    [Fact]
    public void TrustingTheProxyTwice_ChangesNothingTheSecondTime()
    {
        var service = new RemoteFixService(_configuration);
        service.TrustProxy();

        var second = service.TrustProxy();

        Assert.False(second.Changed);
        Assert.False(second.RestartRequired);
        Assert.Equal(RemoteFixService.PresetProxyPort, second.ProxyPort);
    }

    [Fact]
    public void AProxyPortThatWasAlreadyChosen_IsKept()
    {
        _configuration.SaveNetwork(new NetworkSettings { Remote = { ConnectionMode = NetworkConnectionModes.Custom, PublicHostname = "https://library.example.com", ProxyPort = 8443 } });

        var result = new RemoteFixService(_configuration).TrustProxy();

        Assert.False(result.Changed);
        Assert.Equal(8443, _configuration.LoadNetwork().Remote.ProxyPort);
    }

    [Fact]
    public void WhenTheDashboardUsesThePresetPort_TheProxyPortStepsAside()
    {
        _configuration.SaveNetwork(new NetworkSettings { Local = { Port = RemoteFixService.PresetProxyPort } });

        var result = new RemoteFixService(_configuration).TrustProxy();

        Assert.Equal(RemoteFixService.PresetProxyPort + 1, result.ProxyPort);
    }

    [Fact]
    public void TrustingTheProxy_NeverTouchesTheWayInOrTheTrustedLists()
    {
        _configuration.SaveNetwork(new NetworkSettings
        {
            Remote = { ConnectionMode = NetworkConnectionModes.Custom, PublicHostname = "https://library.example.com", TrustedProxies = ["10.0.0.5"] },
        });

        new RemoteFixService(_configuration).TrustProxy();

        var saved = _configuration.LoadNetwork().Remote;
        Assert.Equal(NetworkConnectionModes.Custom, saved.ConnectionMode);
        Assert.Equal("https://library.example.com", saved.PublicHostname);
        Assert.Equal(["10.0.0.5"], saved.TrustedProxies);
    }

    [Fact]
    public async Task TheRoute_IsAPostThatNeedsAnAdministratorOrAnApplicationWithNetworkConfigWrite()
    {
        await using var app = BuildRouteApplication();
        var routes = ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints).OfType<RouteEndpoint>().ToArray();

        var route = Assert.Single(routes, r => r.RoutePattern.RawText == "/network/remote-fix/trust-proxy");
        Assert.Contains(HttpMethods.Post, route.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods);
        var requirements = route.Metadata.OfType<AuthorizationPolicy>().SelectMany(policy => policy.Requirements);
        Assert.Contains(requirements, requirement => requirement is AdministratorOrApplicationRequirement { Permission: var permission }
            && permission == ApplicationPermissionIds.NetworkConfigWrite);
        Assert.Contains(requirements, requirement => requirement is Microsoft.AspNetCore.Authorization.Infrastructure.DenyAnonymousAuthorizationRequirement);
    }

    private static WebApplication BuildRouteApplication()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production });
        builder.Services.AddAuthorization();
        builder.Services.AddTuvimaNetworking();
        // Only so the route table can be read: nothing here is resolved.
        builder.Services.AddSingleton<MediaEngine.Domain.Contracts.IConfigurationLoader>(_ =>
            throw new InvalidOperationException("Reading the route table must not resolve configuration."));
        builder.Services.AddSingleton<MediaEngine.Api.Services.Security.RecentSignInGuard>(_ =>
            throw new InvalidOperationException("Reading the route table must not resolve the sign-in guard."));
        builder.Services.AddSingleton<MediaEngine.Api.Services.Security.SecureAccountGate>();
        var app = builder.Build();
        app.MapNetworkEndpoints();
        return app;
    }
}
