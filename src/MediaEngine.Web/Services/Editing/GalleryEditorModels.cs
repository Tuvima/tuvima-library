using MediaEngine.Contracts.LocalAssets;
using MediaEngine.Contracts.Vault;

namespace MediaEngine.Web.Services.Editing;

public sealed class GalleryEditorLaunchRequest
{
    public ViewGalleryDto? EditingGallery { get; init; }
    public string InitialMembershipMode { get; init; } = "Manual";

    /// <summary>
    /// True to edit the Vault's own image galleries (Videos spec D44) instead of View's. The same editor opens, but it reads and saves
    /// Vault galleries only, offers only what Vault images have, and has no soundtrack.
    /// </summary>
    public bool Vault { get; init; }

    /// <summary>The Vault gallery being changed; null when a new Vault gallery is being made.</summary>
    public VaultImageGalleryDto? EditingVaultGallery { get; init; }
}
