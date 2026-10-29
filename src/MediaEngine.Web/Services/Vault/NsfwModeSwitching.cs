using MediaEngine.Web.Services.Ui;
using Microsoft.AspNetCore.Components;

namespace MediaEngine.Web.Services.Vault;

/// <summary>
/// Turning Vault mode on or off from the profile page or the top menu check (Videos spec D23, D51, D52). On asks for the profile PIN
/// first when "Ask for my PIN to open Vault mode" is on; otherwise it opens straight away and stays on until the person turns it off,
/// switches profile, signs out, closes the app or the idle time they chose runs out. Off is instant, hides everything and returns Home.
/// </summary>
public sealed class NsfwModeSwitching(NsfwModeState nsfw, IAppDialogService dialogs, IAppToastService toasts, NavigationManager navigation)
{
    public const string SetPinFirst = "Set a PIN for your profile first (Settings > Manage Profiles > your profile > Profile).";

    /// <summary>Switches Vault mode on or off. Returns true when the requested state is now in effect.</summary>
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
            "Enter your PIN",
            "This is your profile PIN. It also protects admin pages when you ask it to.",
            "Turn on Vault mode",
            "PIN",
            async pin => VaultMessages.For(await nsfw.UnlockAsync(pin)));
    }
}
