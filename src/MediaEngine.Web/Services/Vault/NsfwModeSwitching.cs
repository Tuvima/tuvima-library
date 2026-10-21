using MediaEngine.Web.Services.Ui;
using Microsoft.AspNetCore.Components;

namespace MediaEngine.Web.Services.Vault;

/// <summary>
/// Turning NSFW mode on or off from the profile page or the top menu check (Videos spec D23). On asks for the NSFW PIN first when
/// the profile's access is "PIN to unlock"; off is instant, hides everything and returns to Home.
/// </summary>
public sealed class NsfwModeSwitching(NsfwModeState nsfw, IAppDialogService dialogs, IAppToastService toasts, NavigationManager navigation)
{
    public const string SetPinFirst = "Set your NSFW PIN first (Settings > Profiles > your profile).";

    /// <summary>Switches NSFW mode on or off. Returns true when the requested state is now in effect.</summary>
    public async Task<bool> SetAsync(bool on)
    {
        if (!nsfw.IsAvailable)
        {
            return false;
        }

        if (!on)
        {
            await nsfw.LockAsync();
            VaultExit.Leave(navigation, VaultExit.Home);
            return true;
        }

        if (nsfw.Unlocked)
        {
            return true;
        }

        if (!nsfw.NeedsPin)
        {
            var call = await nsfw.UnlockAsync(null);
            if (call.Succeeded)
            {
                return true;
            }

            toasts.Add(VaultMessages.For(call) ?? VaultMessages.Unreachable, AppSeverity.Warning);
            return false;
        }

        if (!nsfw.HasPin)
        {
            toasts.Add(SetPinFirst, AppSeverity.Info);
            return false;
        }

        return await VaultPinPrompt.AskAsync(
            dialogs,
            "Enter your NSFW PIN",
            "This is the PIN you saved for NSFW mode, not your profile PIN.",
            "Switch on",
            "NSFW PIN",
            async pin => VaultMessages.For(await nsfw.UnlockAsync(pin)));
    }
}
