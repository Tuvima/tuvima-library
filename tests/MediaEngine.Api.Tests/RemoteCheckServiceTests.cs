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

/// <summary>
/// The "away from home" check (spec remote-access WP1): one read that turns what the Engine already detects into plain findings
/// and one suggested way in.
/// </summary>
public sealed class RemoteCheckServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "tuvima-remote-check-" + Guid.NewGuid().ToString("N"));
    private readonly ConfigurationDirectoryLoader _configuration;
    private readonly NetworkRuntimeState _runtime = new();
    private readonly SpyTailscale _tailscale = new(NotInstalledSnapshot());
    private readonly SpyProbe _probe = new();
    private readonly SpyReadiness _readiness = new(new RemoteSignInReadiness([], false));
    private readonly SpyTopology _topology = new(new NetworkTopologySnapshot("native", true, "Router discovery can use the host's LAN gateway.", "192.168.1.1", "eth0"));
    private readonly DateTimeOffset _now = DateTimeOffset.Parse("2026-11-20T12:00:00Z");

    public RemoteCheckServiceTests()
    {
        Directory.CreateDirectory(_directory);
        _configuration = new ConfigurationDirectoryLoader(_directory);
    }

    public void Dispose()
    {
        _configuration.Dispose();
        Directory.Delete(_directory, recursive: true);
    }

    private Task<RemoteCheckResultDto> Check(bool skip = false)
    {
        var environment = new FixedEnvironment();
        var status = new NetworkStatusService(_configuration, environment, _topology, _runtime, TimeProvider.System);
        return new RemoteCheckService(_configuration, status, _topology, _runtime, [_tailscale], _probe, _readiness, new FixedTime(_now))
            .CheckAsync(skip, CancellationToken.None);
    }

    [Fact]
    public async Task TailscaleMissing_SaysNotInstalled_AndStillSuggestsTailscale()
    {
        var result = await Check();

        var finding = Assert.Single(result.Findings, f => f.Key.StartsWith("tailscale_", StringComparison.Ordinal));
        Assert.Equal(RemoteCheckFindingKeys.TailscaleNotInstalled, finding.Key);
        Assert.NotNull(finding.NextStep);
        Assert.Equal(RemoteCheckSuggestions.Tailscale, result.Suggestion);
        Assert.Contains("easiest", result.SuggestionReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TailscaleInstalledButSignedOut_SaysNotSignedIn()
    {
        _tailscale.Snapshot = new RemoteProviderSnapshot("tailscale", "Tailscale", RemoteProviderState.Unconfigured, null, "Tailscale is needslogin.");

        var result = await Check();

        Assert.Contains(result.Findings, f => f.Key == RemoteCheckFindingKeys.TailscaleNotSignedIn && f.Status == RemoteCheckStatuses.Warn);
        Assert.DoesNotContain(result.Findings, f => f.Key == RemoteCheckFindingKeys.TailscaleNotInstalled);
        Assert.Equal(RemoteCheckSuggestions.Tailscale, result.Suggestion);
    }

    [Fact]
    public async Task TailscaleSignedInWithSecureAddress_IsReady_AndSuggested()
    {
        _tailscale.Snapshot = new RemoteProviderSnapshot(
            "tailscale", "Tailscale", RemoteProviderState.Connected, "https://host.tailnet.ts.net", "Connected.", SecureHttps: true);

        var result = await Check();

        var finding = Assert.Single(result.Findings, f => f.Key.StartsWith("tailscale_", StringComparison.Ordinal));
        Assert.Equal(RemoteCheckFindingKeys.TailscaleReady, finding.Key);
        Assert.Equal(RemoteCheckStatuses.Ok, finding.Status);
        Assert.Contains("host.tailnet.ts.net", finding.Detail, StringComparison.Ordinal);
        Assert.Equal(RemoteCheckSuggestions.Tailscale, result.Suggestion);
        Assert.Contains("already installed and signed in", result.SuggestionReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TailscaleSignedInWithoutHttps_SaysHttpsIsOff()
    {
        _tailscale.Snapshot = new RemoteProviderSnapshot(
            "tailscale", "Tailscale", RemoteProviderState.Degraded, "https://host.tailnet.ts.net", "Serve HTTPS is not active.");

        var result = await Check();

        Assert.Contains(result.Findings, f => f.Key == RemoteCheckFindingKeys.TailscaleHttpsOff && f.Status == RemoteCheckStatuses.Warn);
        Assert.Equal(RemoteCheckSuggestions.Tailscale, result.Suggestion);
    }

    [Fact]
    public async Task SharedAddressProvider_WarnsAndNeverOffersTheRouterDoorAsReady()
    {
        // A router "door" that maps to a private address means the provider shares one public address between homes.
        _runtime.RecordRouterMapping(new RouterMappingResult(
            RouterMappingState.Active, "UPnP", "Mapped.", 443, DateTimeOffset.UtcNow.AddHours(1), PublicAddress: "100.72.4.9"));

        var result = await Check();

        Assert.Contains(result.Findings, f => f.Key == RemoteCheckFindingKeys.SharedAddress && f.Status == RemoteCheckStatuses.Warn);
        Assert.DoesNotContain(result.Findings, f => f.Key == RemoteCheckFindingKeys.RouterDoorReady);
        Assert.Equal(RemoteCheckSuggestions.Tailscale, result.Suggestion);
        Assert.Contains("shares one address", result.SuggestionReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RouterDoorOpenedByTheRouter_IsReadyAndListedAfterTheTailscaleFinding()
    {
        _runtime.RecordRouterMapping(new RouterMappingResult(
            RouterMappingState.Active, "PCP", "Mapped.", 443, DateTimeOffset.UtcNow.AddHours(1), PublicAddress: "203.0.113.7"));

        var result = await Check();

        var keys = result.Findings.Select(f => f.Key).ToList();
        Assert.Contains(RemoteCheckFindingKeys.RouterDoorReady, keys);
        Assert.True(keys.IndexOf(RemoteCheckFindingKeys.TailscaleNotInstalled) < keys.IndexOf(RemoteCheckFindingKeys.RouterDoorReady));
    }

    [Fact]
    public async Task RouterRefusingTheDoor_IsReportedAsUnavailable()
    {
        _runtime.RecordRouterMapping(new RouterMappingResult(RouterMappingState.RouterRefused, "UPnP", "Refused."));

        var result = await Check();

        Assert.Contains(result.Findings, f => f.Key == RemoteCheckFindingKeys.RouterDoorUnavailable && f.Status == RemoteCheckStatuses.Warn);
        Assert.DoesNotContain(result.Findings, f => f.Key == RemoteCheckFindingKeys.RouterDoorReady);
    }

    [Fact]
    public async Task ARouterThatCannotBeSeen_IsReportedAsUnavailableWithoutAWarning()
    {
        _topology.Snapshot = new NetworkTopologySnapshot("docker-bridge", false, "Container network.", null, null);

        var result = await Check();

        Assert.Contains(result.Findings, f => f.Key == RemoteCheckFindingKeys.RouterDoorUnavailable && f.Status == RemoteCheckStatuses.Info);
    }

    [Fact]
    public async Task NoAdministratorPassword_BlocksWithAFailAndNamesNobodyTwice()
    {
        _readiness.Readiness = new RemoteSignInReadiness([], NoAdministratorHasPassword: true);

        var result = await Check();

        Assert.Contains(result.Findings, f => f.Key == RemoteCheckFindingKeys.AdminWithoutPassword && f.Status == RemoteCheckStatuses.Fail);
        Assert.DoesNotContain(result.Findings, f => f.Key == RemoteCheckFindingKeys.ProfilesWithoutPassword);
    }

    [Fact]
    public async Task PeopleWithoutAPassword_AreListedByNameAsHomeOnly()
    {
        _readiness.Readiness = new RemoteSignInReadiness(["Ana", "Ben", "Cy", "Di"], false);

        var result = await Check();

        var finding = Assert.Single(result.Findings, f => f.Key == RemoteCheckFindingKeys.ProfilesWithoutPassword);
        Assert.Equal(RemoteCheckStatuses.Warn, finding.Status);
        Assert.Contains("Ana, Ben, Cy and 1 more have no password", finding.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain(result.Findings, f => f.Key == RemoteCheckFindingKeys.AdminWithoutPassword);
    }

    [Fact]
    public async Task EveryoneHavingAPassword_ProducesNoPasswordFindings()
    {
        var result = await Check();

        Assert.DoesNotContain(result.Findings, f => f.Key is RemoteCheckFindingKeys.ProfilesWithoutPassword or RemoteCheckFindingKeys.AdminWithoutPassword);
    }

    [Fact]
    public async Task AConfiguredPublicHostname_SuggestsTheOwnAddress_EvenWithTailscaleSignedIn()
    {
        _tailscale.Snapshot = new RemoteProviderSnapshot("tailscale", "Tailscale", RemoteProviderState.Connected, null, "Connected.", SecureHttps: true);
        SaveOwnAddress(NetworkConnectionModes.Custom, proxyPort: 8443);
        _probe.Result = Reachable(trusted: true, expires: _now.AddDays(60));

        var result = await Check();

        Assert.Equal(RemoteCheckSuggestions.OwnAddress, result.Suggestion);
        Assert.DoesNotContain(result.Findings, f => f.Key is RemoteCheckFindingKeys.CertificateInvalid or RemoteCheckFindingKeys.CertificateExpiring
            or RemoteCheckFindingKeys.PublicAddressUnreachable or RemoteCheckFindingKeys.NameNotPointingHere or RemoteCheckFindingKeys.ProxyNotTrusted);
    }

    [Fact]
    public async Task ACustomAddressWithNoProxyPort_OffersToTrustTheProxy()
    {
        SaveOwnAddress(NetworkConnectionModes.Custom, proxyPort: null);
        _probe.Result = Reachable(trusted: true, expires: _now.AddDays(60));

        var result = await Check();

        var finding = Assert.Single(result.Findings, f => f.Key == RemoteCheckFindingKeys.ProxyNotTrusted);
        Assert.Equal(RemoteCheckFixActions.TrustProxy, finding.FixAction);
    }

    [Fact]
    public async Task ANameThatDoesNotResolve_IsReportedAsNotPointingHere()
    {
        SaveOwnAddress(NetworkConnectionModes.Custom, proxyPort: 8443);
        _probe.Result = new RemoteAddressProbeResult(false, [], false, null, null);

        var result = await Check();

        Assert.Contains(result.Findings, f => f.Key == RemoteCheckFindingKeys.NameNotPointingHere && f.Status == RemoteCheckStatuses.Fail);
        Assert.DoesNotContain(result.Findings, f => f.Key == RemoteCheckFindingKeys.PublicAddressUnreachable);
    }

    [Fact]
    public async Task ANameNobodyAnswersOn_IsReportedAsUnreachable()
    {
        SaveOwnAddress(NetworkConnectionModes.Custom, proxyPort: 8443);
        _probe.Result = new RemoteAddressProbeResult(true, ["203.0.113.7"], false, null, null);

        var result = await Check();

        Assert.Contains(result.Findings, f => f.Key == RemoteCheckFindingKeys.PublicAddressUnreachable && f.Status == RemoteCheckStatuses.Fail);
    }

    [Fact]
    public async Task AnUntrustedCertificate_IsReportedAsInvalid()
    {
        SaveOwnAddress(NetworkConnectionModes.Custom, proxyPort: 8443);
        _probe.Result = Reachable(trusted: false, expires: _now.AddDays(60));

        var result = await Check();

        Assert.Contains(result.Findings, f => f.Key == RemoteCheckFindingKeys.CertificateInvalid && f.Status == RemoteCheckStatuses.Fail);
        Assert.DoesNotContain(result.Findings, f => f.Key == RemoteCheckFindingKeys.CertificateExpiring);
    }

    [Fact]
    public async Task ACertificateRunningOutWithinTwoWeeks_WarnsItIsExpiring()
    {
        SaveOwnAddress(NetworkConnectionModes.Custom, proxyPort: 8443);
        _probe.Result = Reachable(trusted: true, expires: _now.AddDays(9));

        var result = await Check();

        Assert.Contains(result.Findings, f => f.Key == RemoteCheckFindingKeys.CertificateExpiring && f.Status == RemoteCheckStatuses.Warn);
    }

    [Fact]
    public async Task AHostnameNotMatchingTheRoutersPublicAddress_IsReportedAsPointingElsewhere()
    {
        SaveOwnAddress(NetworkConnectionModes.DirectOnly, proxyPort: null);
        _runtime.RecordRouterMapping(new RouterMappingResult(
            RouterMappingState.Active, "PCP", "Mapped.", 443, DateTimeOffset.UtcNow.AddHours(1), PublicAddress: "203.0.113.7"));
        _probe.Result = new RemoteAddressProbeResult(true, ["198.51.100.4"], true, true, _now.AddDays(60));

        var result = await Check();

        Assert.Contains(result.Findings, f => f.Key == RemoteCheckFindingKeys.NameNotPointingHere);
    }

    [Fact]
    public async Task NotNow_IsOnlySuggestedWhenRemoteIsLocalOnlyAndTheCallerAskedToSkip()
    {
        Assert.Equal(RemoteCheckSuggestions.Tailscale, (await Check(skip: false)).Suggestion);
        Assert.Equal(RemoteCheckSuggestions.NotNow, (await Check(skip: true)).Suggestion);

        SaveOwnAddress(NetworkConnectionModes.Custom, proxyPort: 8443);
        _probe.Result = Reachable(trusted: true, expires: _now.AddDays(60));
        Assert.NotEqual(RemoteCheckSuggestions.NotNow, (await Check(skip: true)).Suggestion);
    }

    [Fact]
    public async Task CheckingWithNothingConfigured_NeverCallsTheAddressProbe()
    {
        await Check();

        Assert.Equal(0, _probe.Calls);
    }

    [Fact]
    public void TheWireNames_AreSnakeCase()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new RemoteCheckResultDto
        {
            Findings = [new RemoteCheckFindingDto { Key = RemoteCheckFindingKeys.ProxyNotTrusted, FixAction = RemoteCheckFixActions.TrustProxy }],
        });

        foreach (var name in new[] { "checked_at", "suggestion_reason", "findings", "next_step", "fix_action" })
        {
            Assert.Contains($"\"{name}\"", json, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task TheRouteRequiresAnAdministratorOrAnApplicationWithNetworkStatusRead()
    {
        await using var app = BuildRouteApplication();
        var routes = ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints).OfType<RouteEndpoint>().ToArray();

        var route = Assert.Single(routes, r => r.RoutePattern.RawText == "/network/remote-check");
        Assert.Contains(HttpMethods.Get, route.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods);
        var requirements = route.Metadata.OfType<AuthorizationPolicy>().SelectMany(policy => policy.Requirements);
        Assert.Contains(requirements, requirement => requirement is AdministratorOrApplicationRequirement { Permission: var permission }
            && permission == ApplicationPermissionIds.NetworkStatusRead);
        Assert.Contains(requirements, requirement => requirement is Microsoft.AspNetCore.Authorization.Infrastructure.DenyAnonymousAuthorizationRequirement);
    }

    private void SaveOwnAddress(string mode, int? proxyPort) =>
        _configuration.SaveNetwork(new NetworkSettings
        {
            Remote =
            {
                ConnectionMode = mode,
                PublicHostname = "https://tuvima.example.com",
                ProxyPort = proxyPort,
            },
        });

    private static RemoteAddressProbeResult Reachable(bool trusted, DateTimeOffset expires) =>
        new(true, ["203.0.113.7"], true, trusted, expires);

    private static RemoteProviderSnapshot NotInstalledSnapshot() =>
        new("tailscale", "Tailscale", RemoteProviderState.Unconfigured, null, "Tailscale is not installed.", NotInstalled: true);

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

    private sealed class FixedEnvironment : INetworkEnvironmentService
    {
        public IReadOnlyList<MediaEngine.Contracts.Settings.NetworkAddressDto> GetUsableAddresses(bool includeIpv6) =>
            [new() { InterfaceId = "eth0", InterfaceName = "Ethernet", Address = "192.168.1.20", AddressFamily = "ipv4" }];
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class SpyTailscale(RemoteProviderSnapshot snapshot) : IRemoteConnectivityProvider
    {
        public RemoteProviderSnapshot Snapshot { get; set; } = snapshot;
        public string Key => "tailscale";
        public string DisplayName => "Tailscale";
        public Task<RemoteProviderSnapshot> GetStateAsync(CancellationToken ct) => Task.FromResult(Snapshot);
        public Task<RemoteProviderSnapshot> TestAsync(CancellationToken ct) => Task.FromResult(Snapshot);
    }

    private sealed class SpyTopology(NetworkTopologySnapshot snapshot) : INetworkTopologyService
    {
        public NetworkTopologySnapshot Snapshot { get; set; } = snapshot;
        public NetworkTopologySnapshot GetSnapshot() => Snapshot;
    }

    private sealed class SpyProbe : IRemoteAddressProbe
    {
        public int Calls { get; private set; }
        public RemoteAddressProbeResult Result { get; set; } = new(false, [], false, null, null);

        public Task<RemoteAddressProbeResult> ProbeAsync(Uri address, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(Result);
        }
    }

    private sealed class SpyReadiness(RemoteSignInReadiness readiness) : IRemoteSignInReadinessReader
    {
        public RemoteSignInReadiness Readiness { get; set; } = readiness;
        public Task<RemoteSignInReadiness> ReadAsync(CancellationToken ct) => Task.FromResult(Readiness);
    }
}
