using MediaEngine.Contracts.Vault;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Ui;

namespace MediaEngine.Web.Services.Vault;

/// <summary>What the third "Add folder" choice (the Vault) came to.</summary>
public enum VaultOnboardingOutcome
{
    /// <summary>The person stopped (cancelled a dialog).</summary>
    Cancelled,

    /// <summary>Vault mode is off or the person cannot use the Vault, so nothing was offered.</summary>
    NotAvailable,

    /// <summary>The PIN was right but the Engine did not add the folder (missing, not allowed, not available or unreachable); <see cref="VaultOnboardingResult.Message"/> says why.</summary>
    FolderNotAdded,

    /// <summary>The library exists and the folder is part of it.</summary>
    FolderAttached,

}

public sealed record VaultOnboardingResult(VaultOnboardingOutcome Outcome, string? Message = null)
{
    public static VaultOnboardingResult Of(VaultOnboardingOutcome outcome, string? message = null) => new(outcome, message);
}

/// <summary>What a new Vault library is created with: where its details come from, and the person's adults-only confirmation (D34).</summary>
public sealed record NewVaultLibrary(string LookupSource, string? LookupServerId, bool AdultsConfirmed)
{
    /// <summary>No lookups (file names only), which is what the dialog flow creates.</summary>
    public static NewVaultLibrary WithoutLookup { get; } = new("none", null, true);
}

/// <summary>
/// The third Add folder choice (Videos spec D3, D45), for administrators in Vault mode only. In order: the administrator confirms that everyone who can unlock the Vault is an adult and the content is lawful and that
/// the files stay visible on disk (D34), and enters that PIN once; the Vault library is created if it does not exist yet (once; later
/// folders reuse it) and the folder is added through <see cref="IVaultFolderAttachment"/>.
/// </summary>
public sealed class VaultLibraryOnboarding(
    NsfwModeState nsfw,
    IEngineApiClient api,
    IAppDialogService dialogs,
    IVaultFolderAttachment attachment)
{
    public const string AdultsConfirmationText =
        "Everyone who can unlock the Vault must be an adult, and the content in it must be lawful where you live. "
        + "The files themselves stay visible on disk to anyone who can browse that folder, and backups include Vault data under the same rules.";

    public const string NotAdded = "That folder couldn't be added to the Vault.";

    /// <summary>Runs the whole choice for one chosen folder. A PIN is strongly suggested but never required (Vault sections D44), so an administrator without one is not stopped.</summary>
    public async Task<VaultOnboardingResult> AddFolderAsync(string folderPath, CancellationToken ct = default)
    {
        if (!nsfw.Unlocked)
        {
            return VaultOnboardingResult.Of(VaultOnboardingOutcome.NotAvailable);
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
        VaultLibraryDto? existingLibrary = existing.Succeeded ? existing.Value?.FirstOrDefault() : null;
        return await AddFolderAsync(folderPath, existingLibrary, NewVaultLibrary.WithoutLookup, ct);
    }

    /// <summary>
    /// The PIN step on its own, for a screen that has already asked its questions (the Add folder wizard's Vault step): the
    /// Vault mode must be on, which the caller checks. If there is no Vault library
    /// yet (<paramref name="library"/> is null) it is created from <paramref name="newLibrary"/> with the person's confirmation;
    /// otherwise the folder is added to <paramref name="library"/>. The Engine's refusals come back as plain sentences, unchanged.
    /// </summary>
    public async Task<VaultOnboardingResult> AddFolderAsync(string folderPath, VaultLibraryDto? library, NewVaultLibrary? newLibrary, CancellationToken ct = default)
    {
        if (!nsfw.Unlocked)
        {
            return VaultOnboardingResult.Of(VaultOnboardingOutcome.NotAvailable);
        }

        if (library is null && newLibrary is not { AdultsConfirmed: true })
        {
            // A new Vault library is never created without the adults-only confirmation.
            return VaultOnboardingResult.Of(VaultOnboardingOutcome.Cancelled);
        }

        VaultOnboardingResult? outcome = null;

        // One PIN entry covers both steps: creating the library (once; later folders reuse it) and adding the folder to it.
        var accepted = await VaultPinPrompt.AskIfSetAsync(
            nsfw.HasPin,
            dialogs,
            "Enter your PIN to continue",
            "This is your own profile PIN, asked for again before the folder is added to the Vault.",
            "Add folder",
            "Your profile PIN",
            async pin =>
            {
                if (library is null)
                {
                    var created = await api.CreateVaultLibraryAsync(new CreateVaultLibraryRequest { LookupSource = newLibrary!.LookupSource, LookupServerId = newLibrary.LookupServerId, AdultsConfirmed = true, AdminPin = pin }, ct);
                    if (!created.Succeeded)
                    {
                        return VaultMessages.For(created);
                    }

                    library = created.Value;
                    if (library is null)
                    {
                        return VaultMessages.Unreachable;
                    }
                }

                var attached = await attachment.AttachAsync(library, folderPath, pin, ct);
                switch (attached.Result)
                {
                    case VaultFolderAttachmentResult.Attached:
                        outcome = VaultOnboardingResult.Of(VaultOnboardingOutcome.FolderAttached);
                        return null;
                    case VaultFolderAttachmentResult.PinNotAccepted:
                        return attached.Message ?? VaultMessages.WrongPin;
                    default:
                        // The PIN was fine; the folder is the problem, so the pad closes and the person is told why.
                        outcome = VaultOnboardingResult.Of(VaultOnboardingOutcome.FolderNotAdded, attached.Message ?? NotAdded);
                        return null;
                }
            });

        return accepted && outcome is not null ? outcome : VaultOnboardingResult.Of(VaultOnboardingOutcome.Cancelled);
    }
}
