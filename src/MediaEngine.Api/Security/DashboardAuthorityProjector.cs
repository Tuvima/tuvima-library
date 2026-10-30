using MediaEngine.Contracts.Authentication;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;

namespace MediaEngine.Api.Security;

public sealed class DashboardAuthorityProjector(
    IAccountRepository accounts,
    IProfileRepository profiles,
    IProfileAccessRepository profileAccess,
    IGrantAdminUnlockService unlocks)
{
    public async Task<DashboardAuthorityResponse> ProjectAsync(
        Guid accountId,
        Guid profileId,
        Guid sessionId,
        CancellationToken ct = default,
        bool profilePending = false)
    {
        var account = await accounts.GetByIdAsync(accountId, ct).ConfigureAwait(false)
            ?? throw new UnauthorizedAccessException("Account is unavailable.");
        // The profile being acted as must be one of the user's own household; its roles and version are what the Dashboard shows.
        var activeProfile = await accounts.GetActiveProfileAccessAsync(accountId, profileId, ct).ConfigureAwait(false)
            ?? throw new UnauthorizedAccessException("Active profile is unavailable.");
        if (!account.IsEnabled)
        {
            throw new UnauthorizedAccessException("Account is disabled.");
        }

        var authority = new RequestAuthority(
            PrincipalKind.Human,
            true,
            accountId,
            profileId,
            SessionId: sessionId,
            AccountEnabled: true,
            AccountAuthorizationVersion: account.AuthorizationVersion).WithActiveProfile(activeProfile);

        GrantAdminUnlockState? unlock = authority.IsEffectiveAdministrator
            ? await unlocks.GetStateAsync(authority, ct).ConfigureAwait(false)
            : null;
        var surfaceUnlocked = authority.IsEffectiveAdministrator &&
            (unlock is null || !unlock.ProtectionEnabled || unlock.IsUnlocked);

        var grants = await HouseholdProfileProjection.MapAsync(accountId, accounts, profiles, profileAccess, ct).ConfigureAwait(false);

        if (profilePending)
        {
            // Nobody is in use yet (spec D3): the session may list the household's profiles and choose one, and nothing else, so
            // no roles, features or actions are shown until a profile is picked.
            return new DashboardAuthorityResponse(
                accountId,
                profileId,
                true,
                true,
                account.AuthorizationVersion,
                activeProfile.AuthorizationVersion,
                false,
                false,
                null,
                0,
                grants,
                [],
                ["profile.switch"]);
        }

        var navigation = authority.IsEffectiveAdministrator
            ? AccountFeatureId.All.Select(feature => feature.Value).ToList()
            : (await profileAccess.GetAreasAsync(profileId, ct).ConfigureAwait(false))
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
            activeProfile.AuthorizationVersion,
            authority.IsEffectiveAdministrator,
            surfaceUnlocked,
            unlock?.ExpiresAt,
            unlock?.ProtectionVersion ?? 0,
            grants,
            navigation,
            actions);
    }
}
