using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Vault;

namespace MediaEngine.Web.Components.Settings;

/// <summary>
/// The one rule for whether the Libraries screens may draw the Vault (Library setup spec D13, Videos spec D45): only for an
/// administrator, on a profile the Engine says may use Vault mode, while Vault mode is on in this device session, and never on a
/// Kids profile. Anything else gets no card, no word and no hint, and the Vault's address reads as not found.
/// </summary>
public static class LibraryVaultAccess
{
    public static bool CanShow(NsfwModeState nsfw, DashboardSessionAccessor session, bool isKids) =>
        !isKids
        && session.Authority?.EffectiveAdministrator == true
        && nsfw.IsAvailable
        && nsfw.Unlocked;
}
