using System.Security.Claims;
using MediaEngine.Api.Endpoints;
using MediaEngine.Api.Security;
using MediaEngine.Domain.Authorization;

namespace MediaEngine.Api.Tests;

/// <summary>A signed-in browser carries a session device id but is not a paired app, so it must use what the browser reported (issue 1080).</summary>
public sealed class PlaybackBrowserSessionAbilityTests
{
    private static ClaimsPrincipal Principal(PrincipalKind kind, Guid deviceId) =>
        new(new ClaimsIdentity(
        [
            new Claim(TuvimaClaimTypes.PrincipalKind, kind.ToString()),
            new Claim(TuvimaClaimTypes.DeviceId, deviceId.ToString("D")),
        ], "test"));

    [Fact]
    public void PairedDeviceId_ForSignedInBrowserSession_IsNull()
    {
        Assert.Null(PlaybackEndpoints.PairedDeviceId(Principal(PrincipalKind.Human, Guid.NewGuid())));
    }

    [Fact]
    public void PairedDeviceId_ForPairedApp_IsItsDeviceRecord()
    {
        var id = Guid.NewGuid();
        Assert.Equal(id, PlaybackEndpoints.PairedDeviceId(Principal(PrincipalKind.DelegatedUserClient, id)));
    }
}
