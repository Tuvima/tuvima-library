using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Services.Vault;
using Microsoft.AspNetCore.Components.Authorization;

namespace MediaEngine.Web.Components.Setup;

/// <summary>
/// Who is offered the Vault in first-run setup: the first administrator, on a desktop or tablet, whose own profile is not restricted and has no Vault access yet.
/// Everyone else is never asked and the Engine is never queried for them.
/// </summary>
internal static class SetupVaultEligibility
{
    /// <param name="alreadyOffered">
    /// True when the Vault step was already answered (turned on, or Not now) and the page is finding the person again after a reload:
    /// the Vault being on for them is then expected, not a reason to leave the step out. Every other rule is unchanged.
    /// </param>
    public static async Task<ProfileViewModel?> FindProfileAsync(
        IEngineApiClient apiClient,
        DashboardSessionAccessor session,
        DashboardIdentityClient identity,
        NsfwModeState nsfw,
        DeviceContextService device,
        AuthenticationStateProvider authenticationState,
        bool alreadyOffered = false)
    {
        if (device.IsMobile)
        {
            return null;
        }

        // The setup circuit has not seen the new sign-in yet, so read it from the cookie and ask the Engine once, as Settings does.
        if (session.Authority is null)
        {
            var principal = (await authenticationState.GetAuthenticationStateAsync()).User;
            if (!session.InitializeFromPrincipal(principal))
            {
                return null;
            }

            await identity.EnsureInitialAuthorityAsync(session);
        }

        if (session.Authority?.EffectiveAdministrator != true || session.ActiveProfileId is not { } activeId)
        {
            return null;
        }

        var profile = (await apiClient.GetProfilesAsync()).FirstOrDefault(candidate => candidate.Id == activeId);
        if (profile is not { IsRestricted: false })
        {
            return null;
        }

        await nsfw.EnsureLoadedAsync(profile.Id, isKids: false);
        return nsfw.IsAvailable && !alreadyOffered ? null : profile;
    }
}
