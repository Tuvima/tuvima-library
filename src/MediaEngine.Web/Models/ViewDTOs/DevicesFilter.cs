using MediaEngine.Contracts.Authentication;

namespace MediaEngine.Web.Models.ViewDTOs;

/// <summary>
/// The household and profile an administrator narrowed the Devices page to; <see langword="null"/> means All. It only
/// narrows what the Engine already returned: the Engine decides who may see which device or sign-in.
/// </summary>
public sealed record DevicesFilter(Guid? HouseholdId = null, Guid? ProfileId = null)
{
    public static DevicesFilter All { get; } = new();

    public bool IsActive => HouseholdId is not null || ProfileId is not null;

    public bool Matches(ManagedClientDeviceDto device) =>
        (HouseholdId is null || device.HouseholdId == HouseholdId)
        && (ProfileId is null || device.ProfileId == ProfileId);

    /// <summary>A signed-in browser matches by the household of its person and by the profile open in that session.</summary>
    public bool Matches(Guid? personHouseholdId, DeviceSessionResponse session) =>
        (HouseholdId is null || personHouseholdId == HouseholdId)
        && (ProfileId is null || session.ActiveProfileId == ProfileId);
}
