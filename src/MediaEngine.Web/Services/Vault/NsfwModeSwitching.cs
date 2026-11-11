using MediaEngine.Web.Services.Ui;
using Microsoft.AspNetCore.Components;

namespace MediaEngine.Web.Services.Vault;

/// <summary>
/// Turning Vault mode on or off from the profile page or the top menu check (Videos spec D23, D51, D52). On asks for the profile PIN
/// first when "Ask for my PIN to open Vault mode" is on; otherwise it opens straight away and stays on until the person turns it off,
/// switches profile, signs out, closes the app or the idle time they chose runs out. Off is instant, hides everything and returns Home.
/// </summary>
public sealed class NsfwModeSwitching(NsfwModeState nsfw, IAppDialogService dialogs, NavigationManager navigation)
{
    public const string SetPinFirst = "Set a PIN for your profile first (Settings > Manage Profiles > your profile > Profile).";

    /// <summary>Switches Vault mode on or off. Returns true when the requested state is now in effect; a caller that can show a reason uses <see cref="TrySetAsync"/>.</summary>
    public async Task<bool> SetAsync(bool on) => (await TrySetAsync(on)).InEffect;

    /// <summary>
    /// Switches Vault mode on or off and says why it did not work, for the caller to show in place (never a pop-up).
    /// <see cref="VaultSwitchOutcome.InEffect"/> is true when the requested state is now in effect.
    /// </summary>
    public async Task<VaultSwitchOutcome> TrySetAsync(bool on)
    {
        if (!nsfw.IsAvailable)
        {
            return new VaultSwitchOutcome(false);
        }

        if (!on)
        {
            await nsfw.LockAsync();
            VaultExit.Leave(navigation, VaultExit.Home);
            return new VaultSwitchOutcome(true);
        }

        if (nsfw.Unlocked)
        {
            return new VaultSwitchOutcome(true);
        }

        if (!nsfw.NeedsPin)
        {
            var call = await nsfw.UnlockAsync(null);
            return call.Succeeded
                ? new VaultSwitchOutcome(true)
                : new VaultSwitchOutcome(false, VaultMessages.For(call) ?? VaultMessages.Unreachable, IsWarning: true);
        }

        if (!nsfw.HasPin)
        {
            return new VaultSwitchOutcome(false, SetPinFirst, IsWarning: false);
        }

        var unlocked = await VaultPinPrompt.AskAsync(
            dialogs,
            "Enter your PIN",
            "This is your profile PIN. It also protects admin pages when you ask it to.",
            "Turn on Vault mode",
            "PIN",
            async pin => VaultMessages.For(await nsfw.UnlockAsync(pin)));
        return new VaultSwitchOutcome(unlocked);
    }
}

/// <summary>What switching Vault mode did: <paramref name="InEffect"/> when the requested state now holds, and the reason to show in place when it did not (a warning, or a plain note when <paramref name="IsWarning"/> is false).</summary>
public sealed record VaultSwitchOutcome(bool InEffect, string? Message = null, bool IsWarning = true);
