using MediaEngine.Api.Services.Networking;
using MediaEngine.Domain.Configuration;

namespace MediaEngine.Api.Tests;

public sealed class NetworkStatusStateTests
{
    [Fact]
    public void ActiveLeaseBecomesExpiredWhenItsDeadlinePasses()
    {
        var now = DateTimeOffset.Parse("2026-08-27T12:00:00Z");
        var mapping = new RouterMappingResult(
            RouterMappingState.Active,
            "PCP",
            "Mapped.",
            443,
            now.AddSeconds(-1));

        Assert.Equal(RouterMappingState.Expired, NetworkStatusService.EffectiveMappingState(mapping, now));
    }

    [Fact]
    public void MissingMappingIsNotAttemptedAndFutureLeaseRemainsActive()
    {
        var now = DateTimeOffset.Parse("2026-08-27T12:00:00Z");
        Assert.Equal(RouterMappingState.NotAttempted, NetworkStatusService.EffectiveMappingState(null, now));
        Assert.Equal(
            RouterMappingState.Active,
            NetworkStatusService.EffectiveMappingState(
                new RouterMappingResult(RouterMappingState.Active, "UPnP", "Mapped.", ExpiresAt: now.AddMinutes(1)),
                now));
    }

    [Theory]
    [InlineData("local-only", "https://tuvima.example.com")]
    [InlineData(" Local-Only ", "https://tuvima.example.com")]
    public void OutsideAddress_IsNothingWhileNoWayInFromTheInternetIsChosen(string mode, string hostname)
    {
        var settings = new NetworkSettings { Remote = { ConnectionMode = mode, PublicHostname = hostname } };

        Assert.Null(NetworkStatusService.OutsideAddressFor(settings, tailscale: null));
    }

    [Fact]
    public void OutsideAddress_IsThePublicHostnameAsAnOriginForAReverseProxy()
    {
        var settings = new NetworkSettings
        {
            Remote = { ConnectionMode = NetworkConnectionModes.Custom, PublicHostname = "https://tuvima.example.com/" },
        };

        Assert.Equal("https://tuvima.example.com", NetworkStatusService.OutsideAddressFor(settings, tailscale: null));
    }

    [Fact]
    public void OutsideAddress_IsTheTailnetAddressInTailscaleMode_NotThePublicHostname()
    {
        var settings = new NetworkSettings
        {
            Remote = { ConnectionMode = NetworkConnectionModes.Tailscale, PublicHostname = "https://ignored.example.com" },
        };
        var tailscale = new RemoteProviderSnapshot(
            "tailscale", "Tailscale", RemoteProviderState.Connected, "https://host.tailnet.ts.net/", "Connected.", SecureHttps: true);

        Assert.Equal("https://host.tailnet.ts.net", NetworkStatusService.OutsideAddressFor(settings, tailscale));
        Assert.Null(NetworkStatusService.OutsideAddressFor(settings, tailscale: null));
    }
}
