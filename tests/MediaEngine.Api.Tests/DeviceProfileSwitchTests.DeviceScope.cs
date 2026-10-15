using System.Security.Claims;
using MediaEngine.Api.Security;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Entities;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Api.Tests;

/// <summary>
/// <c>GET /api/v1/devices</c> and <c>DELETE /api/v1/devices/{id}</c> for a paired app are scoped to the app's own device. A profile
/// switch moves a device to another person, so a TV switched to Dad's profile must not be able to list or sign out Dad's phone.
/// A signed-in person (the Dashboard) keeps the profile-wide behaviour.
/// </summary>
public sealed partial class DeviceProfileSwitchTests
{
    [Fact]
    public async Task AnApp_ListsOnlyItsOwnDevice_WhileASignedInPersonListsTheProfile()
    {
        var tv = await PairAnotherDeviceAsync();
        using var app = BuildHttpApplication();

        var phoneList = await ListDevicesAsync(app, await AppPrincipalAsync(_access));
        var tvList = await ListDevicesAsync(app, await AppPrincipalAsync(tv.AccessToken));
        var personList = await ListDevicesAsync(app, HumanPrincipal(_mary.Id));

        Assert.Equal(StatusCodes.Status200OK, phoneList.Status);
        Assert.Equal([_phoneId], ListedIds(phoneList.Body));
        Assert.Equal([tv.DeviceId], ListedIds(tvList.Body));
        Assert.Equivalent(new[] { _phoneId, tv.DeviceId }, ListedIds(personList.Body));
    }

    [Fact]
    public async Task ATvSwitchedToAnotherPersonsProfile_CannotListOrRevokeThatPersonsOtherDevices()
    {
        var tv = await PairAnotherDeviceAsync();
        var tvAccess = await SwitchDeviceAsync(tv.DeviceId, tv.AccessToken, _jim.Id);
        var phoneAccess = await SwitchDeviceAsync(_phoneId, _access, _jim.Id);
        using var app = BuildHttpApplication();
        var tvPrincipal = await AppPrincipalAsync(tvAccess);

        var list = await ListDevicesAsync(app, tvPrincipal);
        var revokeOther = await RevokeDeviceAsync(app, tvPrincipal, _phoneId);
        var revokeUnknown = await RevokeDeviceAsync(app, tvPrincipal, Guid.NewGuid());

        Assert.Equal([tv.DeviceId], ListedIds(list.Body));
        Assert.DoesNotContain(_phoneId.ToString("D"), list.Body);
        Assert.Equal(StatusCodes.Status404NotFound, revokeOther.Status);
        Assert.Equal(revokeUnknown.Status, revokeOther.Status);
        Assert.Equal(revokeUnknown.Body, revokeOther.Body);
        Assert.Null((await _devices.GetDeviceAsync(_phoneId))!.RevokedAt);
        Assert.NotNull(await _authorization.ValidateAccessTokenAsync(phoneAccess));
    }

    [Fact]
    public async Task AnApp_CanSignItselfOut_AndNothingElseChanges()
    {
        var tv = await PairAnotherDeviceAsync();
        using var app = BuildHttpApplication();

        var revoke = await RevokeDeviceAsync(app, await AppPrincipalAsync(tv.AccessToken), tv.DeviceId);

        Assert.Equal(StatusCodes.Status204NoContent, revoke.Status);
        Assert.NotNull((await _devices.GetDeviceAsync(tv.DeviceId))!.RevokedAt);
        Assert.Null(await _authorization.ValidateAccessTokenAsync(tv.AccessToken));
        Assert.Null((await _devices.GetDeviceAsync(_phoneId))!.RevokedAt);
        Assert.NotNull(await _authorization.ValidateAccessTokenAsync(_access));
    }

    [Fact]
    public async Task ASignedInPerson_StillRevokesAnyDeviceOfTheirProfile_ButNotAnotherProfilesDevice()
    {
        var tv = await PairAnotherDeviceAsync();
        await SwitchDeviceAsync(_phoneId, _access, _jim.Id);
        using var app = BuildHttpApplication();

        var acrossProfiles = await RevokeDeviceAsync(app, HumanPrincipal(_mary.Id), _phoneId);
        var sameProfile = await RevokeDeviceAsync(app, HumanPrincipal(_mary.Id), tv.DeviceId);

        Assert.Equal(StatusCodes.Status404NotFound, acrossProfiles.Status);
        Assert.Null((await _devices.GetDeviceAsync(_phoneId))!.RevokedAt);
        Assert.Equal(StatusCodes.Status204NoContent, sameProfile.Status);
        Assert.NotNull((await _devices.GetDeviceAsync(tv.DeviceId))!.RevokedAt);
    }

    // The new access token of a device after it has been switched to another person (no PIN is set in these cases).
    private async Task<string> SwitchDeviceAsync(Guid deviceId, string accessToken, Guid targetProfile)
    {
        var identity = Assert.IsType<ClientAccessIdentity>(await _authorization.ValidateAccessTokenAsync(accessToken));
        var result = await _service.SwitchAsync(
            new RequestAuthority(PrincipalKind.DelegatedUserClient, true, _homeOwner.Id, identity.Device.ProfileId,
                ApplicationId: BuiltInApplicationIds.NativeClient, DeviceId: deviceId),
            identity.Token.Id, targetProfile, null);
        Assert.Equal(DeviceProfileSwitchOutcome.Switched, result.Outcome);
        return result.Tokens!.AccessToken;
    }

    private async Task<ClaimsPrincipal> AppPrincipalAsync(string accessToken) =>
        ContextFor(Assert.IsType<ClientAccessIdentity>(await _authorization.ValidateAccessTokenAsync(accessToken))).User;

    private ClaimsPrincipal HumanPrincipal(Guid profileId) => new(new ClaimsIdentity(
    [
        new Claim(TuvimaClaimTypes.PrincipalKind, PrincipalKind.Human.ToString()),
        new Claim(TuvimaClaimTypes.AccountId, _homeOwner.Id.ToString("D")),
        new Claim(TuvimaClaimTypes.ProfileId, profileId.ToString("D")),
        new Claim(TuvimaClaimTypes.ActiveProfileId, profileId.ToString("D")),
        new Claim(TuvimaClaimTypes.SessionId, Guid.NewGuid().ToString("D")),
    ], "test"));

    private static Guid[] ListedIds(string body) => System.Text.Json.JsonDocument.Parse(body).RootElement
        .EnumerateArray().Select(device => device.GetProperty("id").GetGuid()).ToArray();

    private static RouteEndpoint DeviceEndpoint(WebApplication app, string name) => Assert.Single(
        EndpointsFor(app), endpoint => endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName == name);

    private static async Task<(int Status, string Body)> ListDevicesAsync(WebApplication app, ClaimsPrincipal user)
    {
        var body = new MemoryStream();
        var context = new DefaultHttpContext { RequestServices = app.Services, User = user, Response = { Body = body } };
        await DeviceEndpoint(app, "ListClientDevices").RequestDelegate!(context);
        body.Position = 0;
        return (context.Response.StatusCode, await new StreamReader(body).ReadToEndAsync());
    }

    private static async Task<(int Status, string Body)> RevokeDeviceAsync(WebApplication app, ClaimsPrincipal user, Guid deviceId)
    {
        var body = new MemoryStream();
        var context = new DefaultHttpContext
        {
            RequestServices = app.Services,
            User = user,
            Request = { Method = "DELETE", RouteValues = new RouteValueDictionary { ["deviceId"] = deviceId.ToString("D") } },
            Response = { Body = body },
        };
        await DeviceEndpoint(app, "RevokeClientDevice").RequestDelegate!(context);
        body.Position = 0;
        return (context.Response.StatusCode, await new StreamReader(body).ReadToEndAsync());
    }
}
