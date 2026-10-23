using MediaEngine.Api.Security;
using MediaEngine.Contracts.Authentication;

namespace MediaEngine.Api.Tests;

/// <summary>A paired app switches only to people inside its user's own household, whatever the other household's sign-ins say.</summary>
public sealed partial class DeviceProfileSwitchTests
{
    [Fact]
    public async Task APersonOutsideTheUsersHousehold_IsNeitherListedNorSwitchedTo()
    {
        Assert.False(await _accounts.HasProfileAccessAsync(_homeOwner.Id, _bea.Id));
        var list = Assert.IsType<DeviceProfileListDto>((await _service.ListAsync(PhoneAuthority(_mary.Id))).Profiles);
        var result = await SwitchAsAsync(_mary.Id, _bea.Id, null);

        Assert.DoesNotContain(list.Profiles, profile => profile.ProfileId == _bea.Id);
        Assert.Equal(DeviceProfileSwitchOutcome.ProfileNotAllowed, result.Outcome);
        Assert.Null(result.Tokens);
        Assert.Equal(_mary.Id, (await _devices.GetDeviceAsync(_phoneId))!.ProfileId);
        Assert.Equal(0, AuditCount());
    }

    [Fact]
    public async Task APersonInsideTheHousehold_StillSwitches_AfterTheHouseholdRule()
    {
        var result = await SwitchAsAsync(_mary.Id, _jim.Id, null);

        Assert.Equal(DeviceProfileSwitchOutcome.Switched, result.Outcome);
        Assert.Equal(_jim.Id, (await _devices.GetDeviceAsync(_phoneId))!.ProfileId);
    }
}
