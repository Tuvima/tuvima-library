using MediaEngine.Contracts.Authentication;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;

namespace MediaEngine.Api.Security;

public sealed class DashboardAuthorityProjector(
    IAccountRepository accounts,
    IProfileRepository profiles,
    IGrantAdminUnlockService unlocks,
    TimeProvider clock)
{
    public async Task<DashboardAuthorityResponse> ProjectAsync(
        Guid accountId,
        Guid profileId,
        Guid sessionId,
        CancellationToken ct = default)
    {
        var account = await accounts.GetByIdAsync(accountId, ct).ConfigureAwait(false)
            ?? throw new UnauthorizedAccessException("Account is unavailable.");
        var activeGrant = await accounts.GetGrantAsync(accountId, profileId, ct).ConfigureAwait(false)
            ?? throw new UnauthorizedAccessException("Active profile grant is unavailable.");
        if (!account.IsEnabled || !activeGrant.IsEnabled)
        {
            throw new UnauthorizedAccessException("Account or active profile grant is disabled.");
        }

        var activeProfile = await profiles.GetByIdAsync(profileId, ct).ConfigureAwait(false);
        var authority = new RequestAuthority(
            PrincipalKind.Human,
            true,
            accountId,
            profileId,
            SessionId: sessionId,
            AccountEnabled: true,
            GrantEnabled: true,
            AccountAuthorizationVersion: account.AuthorizationVersion,
            GrantAuthorizationVersion: activeGrant.AuthorizationVersion,
            AccountIsAdministrator: account.IsAdministrator,
            GrantAdminEnabled: activeGrant.AdminEnabled,
            ActiveProfileIsRestricted: activeProfile?.Role == MediaEngine.Domain.Enums.ProfileRole.RestrictedProfile,
            AccountHouseholdId: account.HouseholdId,
            AccountIsHouseholdAdmin: account.HouseholdAdmin);

        GrantAdminUnlockState? unlock = authority.IsEffectiveAdministrator
            ? await unlocks.GetStateAsync(authority, ct).ConfigureAwait(false)
            : null;
        var surfaceUnlocked = authority.IsEffectiveAdministrator &&
            (unlock is null || !unlock.ProtectionEnabled || unlock.IsUnlocked);

        var grants = new List<AccountProfileGrantDto>();
        foreach (var grant in await accounts.GetGrantsAsync(accountId, ct).ConfigureAwait(false))
        {
            if (!grant.IsEnabled)
            {
                continue;
            }

            var profile = await profiles.GetByIdAsync(grant.ProfileId, ct).ConfigureAwait(false);
            if (profile is null)
            {
                continue;
            }

            var protection = await accounts.GetAdminProtectionAsync(accountId, grant.ProfileId, ct)
                .ConfigureAwait(false);
            grants.Add(new AccountProfileGrantDto(
                accountId,
                grant.ProfileId,
                profile.DisplayName,
                profile.AvatarImagePath,
                grant.IsDefault,
                grant.IsEnabled,
                grant.AdminEnabled,
                new GrantAdminProtectionDto(
                    protection?.IsEnabled == true,
                    protection?.UnlockMode ?? AdminUnlockMode.FixedDuration.ToString(),
                    protection?.UnlockMinutes,
                    protection?.ProtectionVersion ?? 0,
                    protection?.LockedUntil > clock.GetUtcNow(),
                    protection?.LockedUntil),
                grant.AuthorizationVersion,
                grant.GrantedAt));
        }

        var navigation = authority.IsEffectiveAdministrator
            ? AccountFeatureId.All.Select(feature => feature.Value).ToList()
            : (await accounts.GetFeatureGrantsAsync(accountId, ct).ConfigureAwait(false))
                .Select(feature => feature.Value)
                .Order(StringComparer.Ordinal)
                .ToList();
        var actions = new List<string> { "account.self_service", "profile.switch" };

        if (authority.IsEffectiveAdministrator)
        {
            navigation.Add("settings.administration");
            if (surfaceUnlocked)
            {
                actions.Add("access.manage");
                actions.Add("applications.manage");
                if (unlock?.ProtectionEnabled == true)
                {
                    actions.Add("administrator.lock");
                }
            }
            else
            {
                actions.Add("administrator.unlock");
            }
        }

        // A household administrator (who is not also the server administrator) manages only their own household.
        if (authority.IsEffectiveHouseholdAdministrator && !authority.IsEffectiveAdministrator)
        {
            actions.Add("household.manage");
        }

        return new DashboardAuthorityResponse(
            accountId,
            profileId,
            true,
            true,
            account.AuthorizationVersion,
            activeGrant.AuthorizationVersion,
            authority.IsEffectiveAdministrator,
            surfaceUnlocked,
            unlock?.ExpiresAt,
            unlock?.ProtectionVersion ?? 0,
            grants,
            navigation,
            actions);
    }
}
