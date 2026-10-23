using MediaEngine.Contracts.Authentication;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Enums;

namespace MediaEngine.Api.Security;

/// <summary>
/// The profiles a user may act as (its own profile and the rest of its household), as the wire shows them to the Dashboard's profile
/// picker. Everything here is the profile's: its role, its administrator PIN question and its authorization version.
/// </summary>
internal static class HouseholdProfileProjection
{
    public static async Task<IReadOnlyList<AccountProfileGrantDto>> MapAsync(
        Guid userId,
        IAccountRepository accounts,
        IProfileRepository profiles,
        IProfileAccessRepository profileAccess,
        CancellationToken ct)
    {
        var result = new List<AccountProfileGrantDto>();
        foreach (var entry in await accounts.GetHouseholdProfilesAsync(userId, ct).ConfigureAwait(false))
        {
            var profile = await profiles.GetByIdAsync(entry.ProfileId, ct).ConfigureAwait(false);
            if (profile is null)
            {
                continue;
            }

            var protection = await profileAccess.GetAdminProtectionAsync(entry.ProfileId, ct).ConfigureAwait(false);
            result.Add(new AccountProfileGrantDto(userId, entry.ProfileId, profile.DisplayName,
                profile.AvatarImagePath, entry.IsOwn, true, entry.IsServerAdministrator,
                new ProfileAdminProtectionResponse(protection?.IsEnabled == true,
                    protection?.UnlockMode ?? AdminUnlockMode.FixedDuration.ToString(),
                    protection?.UnlockMinutes, protection?.ProtectionVersion ?? 0),
                entry.AuthorizationVersion, entry.CreatedAt,
                profile.Role == ProfileRole.RestrictedProfile));
        }

        return result;
    }
}
