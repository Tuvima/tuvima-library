using MediaEngine.Web.Models.ViewDTOs;

namespace MediaEngine.Web.Components.Profiles;

/// <summary>
/// Who sees what in "Manage profiles" on the picker. The Engine enforces the same limits (only a household or server
/// administrator can change people), so these rules decide what is offered, never what is allowed.
/// </summary>
public static class ProfileManageRules
{
    public const int MaxProfiles = 8;

    /// <summary>Manage profiles and Add profile are for administrators, and never inside a Kids (restricted) profile.</summary>
    public static bool CanManage(bool activeProfileIsKids, bool isAdministrator) => isAdministrator && !activeProfileIsKids;

    /// <summary>A household holds up to eight people.</summary>
    public static bool CanAdd(bool canManage, int profileCount) => canManage && profileCount < MaxProfiles;

    /// <summary>
    /// Delete is offered for anyone except the last profile, the original Owner profile and the profile that is open right now
    /// (switch to someone else first).
    /// </summary>
    public static bool CanDelete(ProfileViewModel profile, Guid? activeProfileId, int profileCount) =>
        profileCount > 1 && !profile.IsSeed && profile.Id != activeProfileId;
}
