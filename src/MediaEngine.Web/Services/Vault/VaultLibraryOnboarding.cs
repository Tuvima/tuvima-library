using MediaEngine.Contracts.Vault;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Ui;
using Microsoft.AspNetCore.Components;

namespace MediaEngine.Web.Services.Vault;

/// <summary>What the third "Add folder" choice (the Vault) came to.</summary>
public enum VaultOnboardingOutcome
{
    /// <summary>The person stopped (cancelled a dialog or opened their profile to set a PIN).</summary>
    Cancelled,

    /// <summary>NSFW mode is off or the person cannot use the Vault, so nothing was offered.</summary>
    NotAvailable,

    /// <summary>The library exists but the Engine cannot attach the folder yet (see <see cref="IVaultFolderAttachment"/>).</summary>
    LibraryReadyFolderNotAttached,

    /// <summary>The library exists and the folder is part of it.</summary>
    FolderAttached,

    /// <summary>The Engine refused or could not be reached; <see cref="VaultOnboardingResult.Message"/> says why.</summary>
    Failed,
}

public sealed record VaultOnboardingResult(VaultOnboardingOutcome Outcome, string? Message = null)
{
    public static VaultOnboardingResult Of(VaultOnboardingOutcome outcome, string? message = null) => new(outcome, message);
}

/// <summary>
/// The third Add folder choice (Videos spec D3, D45), for administrators in NSFW mode only. In order: the administrator needs a
/// profile PIN of their own (D25), confirms that everyone who can unlock the Vault is an adult and the content is lawful and that
/// the files stay visible on disk (D34), enters that PIN, and the Vault library is created (once; later folders reuse it). Attaching
/// the folder itself goes through <see cref="IVaultFolderAttachment"/>.
/// </summary>
public sealed class VaultLibraryOnboarding(
    NsfwModeState nsfw,
    IEngineApiClient api,
    IAppDialogService dialogs,
    IVaultFolderAttachment attachment,
    NavigationManager navigation)
{
    public const string AdultsConfirmationText =
        "Everyone who can unlock the Vault must be an adult, and the content in it must be lawful where you live. "
        + "The files themselves stay visible on disk to anyone who can browse that folder, and backups include Vault data under the same rules.";

    public const string ReadyButNotAttached =
        "The Vault library is ready. Adding its folder arrives with the Vault import update, so this folder wasn't added yet.";

    /// <summary>Runs the whole choice for one chosen folder. <paramref name="administratorId"/> and <paramref name="administratorHasPin"/> describe the signed-in administrator.</summary>
    public async Task<VaultOnboardingResult> AddFolderAsync(string folderPath, Guid administratorId, bool administratorHasPin, CancellationToken ct = default)
    {
        if (!nsfw.Unlocked)
        {
            return VaultOnboardingResult.Of(VaultOnboardingOutcome.NotAvailable);
        }

        if (!administratorHasPin)
        {
            // D25: no Vault library before the administrator has a profile PIN of their own. Offer to set one; nothing is created.
            var setNow = await dialogs.ShowMessageBoxAsync(
                "Set your PIN first",
                "Adding to the Vault asks for your own profile PIN each time, so you need one first.",
                yesText: "Open my profile",
                noText: "Cancel");
            if (setNow == true)
            {
                navigation.NavigateTo($"/settings/profiles/{administratorId:D}");
            }

            return VaultOnboardingResult.Of(VaultOnboardingOutcome.Cancelled);
        }

        var confirmed = await dialogs.ShowMessageBoxAsync(
            "Adults only",
            AdultsConfirmationText,
            yesText: "I confirm",
            noText: "Cancel");
        if (confirmed != true)
        {
            return VaultOnboardingResult.Of(VaultOnboardingOutcome.Cancelled);
        }

        var existing = await api.GetVaultLibrariesAsync(ct);
        VaultLibraryDto? library = existing.Succeeded ? existing.Value?.FirstOrDefault() : null;
        string? failure = null;
        if (library is null)
        {
            var created = await VaultPinPrompt.AskAsync(
                dialogs,
                "Enter your PIN to continue",
                "This is your own profile PIN, asked for again before the Vault library is created.",
                "Create library",
                "Your profile PIN",
                async pin =>
                {
                    var call = await api.CreateVaultLibraryAsync(new CreateVaultLibraryRequest { LookupSource = "none", AdultsConfirmed = true, AdminPin = pin }, ct);
                    if (call.Succeeded)
                    {
                        library = call.Value;
                    }

                    failure = VaultMessages.For(call);
                    return failure;
                });
            if (!created || library is null)
            {
                return VaultOnboardingResult.Of(created ? VaultOnboardingOutcome.Failed : VaultOnboardingOutcome.Cancelled, failure);
            }
        }

        return await attachment.AttachAsync(library, folderPath, ct) == VaultFolderAttachmentResult.Attached
            ? VaultOnboardingResult.Of(VaultOnboardingOutcome.FolderAttached)
            : VaultOnboardingResult.Of(VaultOnboardingOutcome.LibraryReadyFolderNotAttached, ReadyButNotAttached);
    }
}
