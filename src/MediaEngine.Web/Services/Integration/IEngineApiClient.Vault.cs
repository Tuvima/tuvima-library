using MediaEngine.Contracts.Vault;
using MediaEngine.Web.Services.Vault;

namespace MediaEngine.Web.Services.Integration;

public partial interface IEngineApiClient
{
    /// <summary>GET /api/v1/nsfw/status - the caller's own Vault mode state. <see cref="VaultOutcome.NotAvailable"/> for any profile that cannot use it, so nothing is drawn.</summary>
    Task<VaultCall<NsfwStatusDto>> GetNsfwStatusAsync(CancellationToken ct = default);

    /// <summary>POST /api/v1/nsfw/unlock - switch Vault mode on for this device session (the PIN is not needed for an Allowed profile).</summary>
    Task<VaultCall<NsfwStatusDto>> UnlockNsfwAsync(string? pin, CancellationToken ct = default);

    /// <summary>POST /api/v1/nsfw/lock - switch Vault mode off for this device session at once.</summary>
    Task<VaultCall<bool>> LockNsfwAsync(CancellationToken ct = default);

    /// <summary>POST /api/v1/nsfw/keepalive - restart the idle timer. <see cref="VaultOutcome.NotAvailable"/> means the session has already locked.</summary>
    Task<VaultCall<bool>> KeepNsfwAliveAsync(CancellationToken ct = default);

    /// <summary>PUT /api/v1/nsfw/idle - the person's own "Turn Vault mode off after being idle for" choice (5, 15, 30, 60 minutes, or 0 for Never).</summary>
    Task<VaultCall<NsfwStatusDto>> SetNsfwIdleAsync(int minutes, CancellationToken ct = default);

    /// <summary>Saves the signed-in person's own "Show Vault in the navigation bar" choice (Library pages D20).</summary>
    Task<VaultCall<NsfwStatusDto>> SetNsfwNavigationAsync(bool show, CancellationToken ct = default);

    /// <summary>PUT /api/v1/profiles/{id}/nsfw-access - an administrator opens, changes or closes one profile's Vault access.</summary>
    Task<VaultCall<NsfwStatusDto>> SetProfileNsfwAccessAsync(Guid profileId, SetNsfwAccessRequest request, CancellationToken ct = default);

    /// <summary>GET /api/v1/vault/libraries - the Vault libraries, for an unlocked administrator.</summary>
    Task<VaultCall<IReadOnlyList<VaultLibraryDto>>> GetVaultLibrariesAsync(CancellationToken ct = default);

    /// <summary>POST /api/v1/vault/libraries - create the Vault library (asks for the administrator's own PIN).</summary>
    Task<VaultCall<VaultLibraryDto>> CreateVaultLibraryAsync(CreateVaultLibraryRequest request, CancellationToken ct = default);

    /// <summary>PUT /api/v1/vault/libraries/{id} - change a Vault library's lookup source.</summary>
    Task<VaultCall<VaultLibraryDto>> UpdateVaultLibraryAsync(string libraryId, UpdateVaultLibraryRequest request, CancellationToken ct = default);

    /// <summary>POST /api/v1/vault/libraries/{id}/folders - put a server folder into the Vault library (asks for the administrator's own PIN). The path travels in the body, never in the address.</summary>
    Task<VaultCall<VaultLibraryDto>> AttachVaultFolderAsync(string libraryId, AttachVaultFolderRequest request, CancellationToken ct = default);

    /// <summary>GET /api/v1/profiles/{id}/vault-sections - the Vault sections and which of them this profile may open (an administrator whose Vault mode is on).</summary>
    Task<VaultCall<ProfileVaultSectionsDto>> GetProfileVaultSectionsAsync(Guid profileId, CancellationToken ct = default);

    /// <summary>PUT /api/v1/profiles/{id}/vault-sections - replace the Vault sections this profile may open. Nothing is ticked by default.</summary>
    Task<VaultCall<ProfileVaultSectionsDto>> SetProfileVaultSectionsAsync(Guid profileId, IReadOnlyList<string> libraryIds, CancellationToken ct = default);

    /// <summary>POST /api/v1/vault/libraries/{id}/remove - remove a Vault library.</summary>
    Task<VaultCall<bool>> RemoveVaultLibraryAsync(string libraryId, RemoveVaultLibraryRequest request, CancellationToken ct = default);

    /// <summary>POST /api/v1/vault/items/{assetId}/identify/search - records that might be this Vault video. The typed text travels in the body, never in the address.</summary>
    Task<VaultCall<VaultCandidateListDto>> SearchVaultIdentifyAsync(Guid assetId, string? text, CancellationToken ct = default);

    /// <summary>POST /api/v1/vault/items/{assetId}/identify/preview - the current values beside the values a chosen record would put there.</summary>
    Task<VaultCall<VaultIdentifyPreviewDto>> PreviewVaultIdentifyAsync(Guid assetId, VaultIdentifyPreviewRequest request, CancellationToken ct = default);

    /// <summary>POST /api/v1/vault/items/{assetId}/identify/apply - apply exactly the ticked fields of a chosen record.</summary>
    Task<VaultCall<VaultIdentifyApplyResultDto>> ApplyVaultIdentifyAsync(Guid assetId, VaultIdentifyApplyRequest request, CancellationToken ct = default);

    /// <summary>GET /api/v1/vault/items/{assetId}/match - how a Vault video was matched, if it was.</summary>
    Task<VaultCall<VaultMatchDto>> GetVaultMatchAsync(Guid assetId, CancellationToken ct = default);

    /// <summary>GET /api/v1/vault/activity - the quiet Vault activity list, for an unlocked administrator.</summary>
    Task<VaultCall<IReadOnlyList<VaultActivityItemDto>>> GetVaultActivityAsync(CancellationToken ct = default);

    /// <summary>GET /api/v1/vault/restored-bookmarks - how many saved Vault bookmarks from a restored backup still wait for their video. A count only, never a title.</summary>
    Task<VaultCall<VaultRestoredBookmarksDto>> GetVaultRestoredBookmarksAsync(CancellationToken ct = default);

    /// <summary>GET /api/v1/vault/scenes - Vault scenes (recently added, scenes, unmatched, or those of a performer, studio, tag or folder). <see cref="VaultOutcome.NotAvailable"/> for a locked session.</summary>
    Task<VaultCall<VaultSceneListDto>> GetVaultScenesAsync(
        string? match, string? sort, bool? descending, Guid? performerId, Guid? studioId, Guid? tagId, string? folderId,
        int skip = 0, int take = 60, CancellationToken ct = default);

    /// <summary>GET /api/v1/vault/scenes/{id} - one scene's own page.</summary>
    Task<VaultCall<VaultSceneDetailDto>> GetVaultSceneAsync(Guid assetId, CancellationToken ct = default);

    /// <summary>GET /api/v1/vault/folders - the series and sections of the Vault's folder layout.</summary>
    Task<VaultCall<IReadOnlyList<VaultFolderDto>>> GetVaultFoldersAsync(CancellationToken ct = default);

    /// <summary>GET /api/v1/vault/performers, /studios or /tags (<paramref name="kind"/>) - names with at least one owned scene.</summary>
    Task<VaultCall<IReadOnlyList<VaultNameTileDto>>> GetVaultNamesAsync(string kind, string? sort, bool? descending, CancellationToken ct = default);

    /// <summary>POST /api/v1/vault/search - the Vault search group. The typed text travels in the body, never in the address.</summary>
    Task<VaultCall<VaultSearchResultDto>> SearchVaultAsync(string text, CancellationToken ct = default);

    /// <summary>GET /api/v1/vault/images - Vault images, newest first: all, or one gallery's, one tag's or the favourites. <see cref="VaultOutcome.NotAvailable"/> for a locked session.</summary>
    Task<VaultCall<VaultImagePageDto>> GetVaultImagesAsync(Guid? galleryId, Guid? tagId, bool favoritesOnly, string? cursor, int take, CancellationToken ct = default);

    /// <summary>POST /api/v1/vault/images/search - searches only the Vault's images. The typed text travels in the body, never in the address.</summary>
    Task<VaultCall<VaultImagePageDto>> SearchVaultImagesAsync(VaultImageSearchRequest request, CancellationToken ct = default);

    /// <summary>GET /api/v1/vault/images/galleries - folder, hand-made and rule galleries.</summary>
    Task<VaultCall<IReadOnlyList<VaultImageGalleryDto>>> GetVaultImageGalleriesAsync(CancellationToken ct = default);

    /// <summary>GET /api/v1/vault/images/{id} - one image with its tags, for the viewer.</summary>
    Task<VaultCall<VaultImageDetailDto>> GetVaultImageAsync(Guid imageId, CancellationToken ct = default);

    /// <summary>PUT /api/v1/vault/images/{id}/tags - replaces the image's tags by name.</summary>
    Task<VaultCall<VaultImageDetailDto>> SetVaultImageTagsAsync(Guid imageId, IReadOnlyList<string> tags, CancellationToken ct = default);

    /// <summary>PUT /api/v1/vault/images/{id}/favorite.</summary>
    Task<VaultCall<bool>> SetVaultImageFavoriteAsync(Guid imageId, bool favorite, CancellationToken ct = default);

    /// <summary>POST /api/v1/vault/images/galleries - makes a hand-made or rule gallery.</summary>
    Task<VaultCall<VaultImageGalleryDto>> CreateVaultImageGalleryAsync(VaultImageGalleryRequest request, CancellationToken ct = default);

    /// <summary>PUT /api/v1/vault/images/galleries/{id} - changes a hand-made or rule gallery.</summary>
    Task<VaultCall<VaultImageGalleryDto>> UpdateVaultImageGalleryAsync(Guid galleryId, VaultImageGalleryRequest request, CancellationToken ct = default);

    /// <summary>DELETE /api/v1/vault/images/galleries/{id} - the images are kept.</summary>
    Task<VaultCall<bool>> DeleteVaultImageGalleryAsync(Guid galleryId, CancellationToken ct = default);

    /// <summary>POST /api/v1/vault/images/galleries/{id}/items - adds images to a hand-made gallery.</summary>
    Task<VaultCall<int>> AddVaultImagesToGalleryAsync(Guid galleryId, IReadOnlyList<Guid> imageIds, CancellationToken ct = default);

    /// <summary>POST /api/v1/vault/images/scan - an administrator looks for new Vault images; counts only come back.</summary>
    Task<VaultCall<VaultImageScanDto>> ScanVaultImagesAsync(CancellationToken ct = default);

    /// <summary>POST /api/v1/vault/images/{id}/delete - an administrator moves an image to the recycle bin.</summary>
    Task<VaultCall<bool>> DeleteVaultImageAsync(Guid imageId, CancellationToken ct = default);

    /// <summary>POST /api/v1/vault/images/{id}/restore - an administrator puts a deleted image back.</summary>
    Task<VaultCall<bool>> RestoreVaultImageAsync(Guid imageId, CancellationToken ct = default);

    /// <summary>GET /api/v1/vault/images/trash - an administrator's list of deleted Vault images.</summary>
    Task<VaultCall<VaultImagePageDto>> GetVaultImageTrashAsync(string? cursor, int take, CancellationToken ct = default);

    /// <summary>POST /api/v1/vault/images/trash/empty - an administrator removes every deleted Vault image for good.</summary>
    Task<VaultCall<VaultImageEmptiedDto>> EmptyVaultImageTrashAsync(CancellationToken ct = default);

    /// <summary>POST /settings/vault-moves/preview - counts only; nothing is moved and no title comes back.</summary>
    Task<VaultCall<VaultMovePreviewDto>> PreviewVaultMoveAsync(VaultMovePreviewRequest request, CancellationToken ct = default);

    /// <summary>POST /settings/vault-moves - an unlocked administrator starts a move with their own PIN.</summary>
    Task<VaultCall<StartVaultMoveResultDto>> StartVaultMoveAsync(StartVaultMoveRequest request, CancellationToken ct = default);

    /// <summary>GET /settings/vault-moves - the moves still running or needing attention (counts only).</summary>
    Task<VaultCall<IReadOnlyList<VaultMoveJobDto>>> GetVaultMovesAsync(CancellationToken ct = default);
}
