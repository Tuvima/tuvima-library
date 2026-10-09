using System.Net;
using System.Net.Http.Json;
using MediaEngine.Api.Services.Networking;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Api.Tests;

public sealed class RemoteAccessReadinessServiceTests
{
    [Fact]
    public async Task TailscaleRequiresAdministratorAndVerifiedServeHttps()
    {
        var provider = new FakeProvider(new RemoteProviderSnapshot(
            "tailscale", "Tailscale", RemoteProviderState.Connected,
            "https://tuvima.example.ts.net", "Serve is active.", SecureHttps: true));
        var ready = Create(new FakeAuthentication(true, true, true), [provider]);

        var passed = await ready.EvaluateAsync(new RemoteNetworkSettings
        {
            ConnectionMode = NetworkConnectionModes.Tailscale,
        }, CancellationToken.None);

        Assert.True(passed.Ready);
        Assert.All(passed.Checks, check => Assert.Equal("passed", check.Status));

        var unclaimed = Create(new FakeAuthentication(false, false, true), [provider]);
        var blocked = await unclaimed.EvaluateAsync(new RemoteNetworkSettings
        {
            ConnectionMode = NetworkConnectionModes.Tailscale,
        }, CancellationToken.None);

        Assert.False(blocked.Ready);
        Assert.Contains(blocked.Checks, check => check.Key == "authentication" && check.Status == "failed");
    }

    [Fact]
    public async Task UsableAdministratorWithoutRecoveryCodesFailsWithRecoveryMessage()
    {
        var provider = new FakeProvider(new RemoteProviderSnapshot(
            "tailscale", "Tailscale", RemoteProviderState.Connected,
            "https://tuvima.example.ts.net", "Serve is active.", SecureHttps: true));
        var service = Create(new FakeAuthentication(true, false, true), [provider]);

        var result = await service.EvaluateAsync(new RemoteNetworkSettings
        {
            ConnectionMode = NetworkConnectionModes.Tailscale,
        }, CancellationToken.None);

        var check = Assert.Single(result.Checks, c => c.Key == "authentication");
        Assert.Equal("failed", check.Status);
        Assert.Equal("Save recovery codes for an administrator before opening Tuvima to the internet.", check.Detail);
    }

    [Fact]
    public async Task NoUsableAdministratorFailsWithSignInMessage()
    {
        var service = Create(new FakeAuthentication(false, false, true), []);

        var result = await service.EvaluateAsync(new RemoteNetworkSettings(), CancellationToken.None);

        var check = Assert.Single(result.Checks, c => c.Key == "authentication");
        Assert.Equal("failed", check.Status);
        Assert.Equal("No administrator can sign in yet. Add a password or passkey to an administrator account.", check.Detail);
    }

    [Fact]
    public async Task CustomProxyMustReturnSecureTuvimaChallenge()
    {
        var handler = new StubHandler(request =>
        {
            Assert.Equal("https", request.RequestUri!.Scheme);
            var nonce = request.RequestUri.Query["?nonce=".Length..];
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new { product = "Tuvima Library", nonce, secure = true }),
            });
        });
        var service = Create(new FakeAuthentication(true, true, true), [], handler);

        var result = await service.EvaluateAsync(new RemoteNetworkSettings
        {
            ConnectionMode = NetworkConnectionModes.Custom,
            PublicHostname = "https://library.example.test",
        }, CancellationToken.None);

        Assert.True(result.Ready);
        Assert.Contains(result.Checks, check => check.Key == "https-endpoint" && check.Status == "passed");
    }

    [Fact]
    public async Task DirectMappingFailsClosedInDockerBridgeTopology()
    {
        var service = Create(
            new FakeAuthentication(true, true, true),
            [],
            topology: new NetworkTopologySnapshot(
                "docker-bridge", false, "docker0 is not the LAN gateway.", "172.18.0.1", "eth0"));

        var result = await service.EvaluateAsync(new RemoteNetworkSettings
        {
            ConnectionMode = NetworkConnectionModes.DirectOnly,
            PublicHostname = "https://library.example.test",
            TlsTerminationPort = 443,
        }, CancellationToken.None);

        Assert.False(result.Ready);
        Assert.Contains(result.Checks, check => check.Key == "router-topology" && check.Status == "failed");
    }

    private static RemoteAccessReadinessService Create(
        IUsableAdministratorService authentication,
        IEnumerable<IRemoteConnectivityProvider> providers,
        HttpMessageHandler? handler = null,
        NetworkTopologySnapshot? topology = null) => new(
            authentication,
            new FakeTopology(topology ?? new NetworkTopologySnapshot("native", true, "LAN gateway available.", "192.168.1.1", "Ethernet")),
            providers,
            new HttpClient(handler ?? new StubHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)))),
            NullLogger<RemoteAccessReadinessService>.Instance);

    private sealed class FakeAuthentication(bool usable, bool recoveryCodes, bool bypassDisabled) : IUsableAdministratorService
    {
        public Task<bool> HasUsableAdministratorSignInAsync(MediaEngine.Domain.Configuration.AuthSettings policy, CancellationToken ct) =>
            Task.FromResult(usable);

        public Task<UsableAdministratorStatus> EvaluateForRemoteAsync(CancellationToken ct) =>
            Task.FromResult(new UsableAdministratorStatus(usable, recoveryCodes, bypassDisabled));
    }

    private sealed class FakeTopology(NetworkTopologySnapshot snapshot) : INetworkTopologyService
    {
        public NetworkTopologySnapshot GetSnapshot() => snapshot;
    }

    private sealed class FakeProvider(RemoteProviderSnapshot snapshot) : IRemoteConnectivityProvider
    {
        public string Key => snapshot.Key;
        public string DisplayName => snapshot.DisplayName;
        public Task<RemoteProviderSnapshot> GetStateAsync(CancellationToken ct) => Task.FromResult(snapshot);
        public Task<RemoteProviderSnapshot> TestAsync(CancellationToken ct) => Task.FromResult(snapshot);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request);
    }
}
