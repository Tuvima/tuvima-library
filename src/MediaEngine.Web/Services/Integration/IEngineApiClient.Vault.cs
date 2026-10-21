using MediaEngine.Contracts.Vault;
using MediaEngine.Web.Services.Vault;

namespace MediaEngine.Web.Services.Integration;

public partial interface IEngineApiClient
{
    /// <summary>GET /api/v1/nsfw/status - the caller's own NSFW state. <see cref="VaultOutcome.NotAvailable"/> for any profile that cannot use it, so nothing is drawn.</summary>
    Task<VaultCall<NsfwStatusDto>> GetNsfwStatusAsync(CancellationToken ct = default);

    /// <summary>POST /api/v1/nsfw/unlock - switch NSFW mode on for this device session (the PIN is not needed for an Allowed profile).</summary>
    Task<VaultCall<NsfwStatusDto>> UnlockNsfwAsync(string? pin, CancellationToken ct = default);

    /// <summary>POST /api/v1/nsfw/lock - switch NSFW mode off for this device session at once.</summary>
    Task<VaultCall<bool>> LockNsfwAsync(CancellationToken ct = default);

    /// <summary>POST /api/v1/nsfw/keepalive - restart the idle timer. <see cref="VaultOutcome.NotAvailable"/> means the session has already locked.</summary>
    Task<VaultCall<bool>> KeepNsfwAliveAsync(CancellationToken ct = default);

    /// <summary>PUT /api/v1/nsfw/pin - save (or change) the person's own NSFW PIN.</summary>
    Task<VaultCall<bool>> SetNsfwPinAsync(NsfwSetPinRequest request, CancellationToken ct = default);

    /// <summary>PUT /api/v1/profiles/{id}/nsfw-access - an administrator opens, changes or closes one profile's NSFW access.</summary>
    Task<VaultCall<NsfwStatusDto>> SetProfileNsfwAccessAsync(Guid profileId, SetNsfwAccessRequest request, CancellationToken ct = default);

    /// <summary>DELETE /api/v1/profiles/{id}/nsfw-pin - an administrator removes one profile's NSFW PIN (never reads it).</summary>
    Task<VaultCall<bool>> ResetProfileNsfwPinAsync(Guid profileId, ResetNsfwPinRequest request, CancellationToken ct = default);

    /// <summary>GET /api/v1/vault/libraries - the Vault libraries, for an unlocked administrator.</summary>
    Task<VaultCall<IReadOnlyList<VaultLibraryDto>>> GetVaultLibrariesAsync(CancellationToken ct = default);

    /// <summary>POST /api/v1/vault/libraries - create the Vault library (asks for the administrator's own PIN).</summary>
    Task<VaultCall<VaultLibraryDto>> CreateVaultLibraryAsync(CreateVaultLibraryRequest request, CancellationToken ct = default);

    /// <summary>PUT /api/v1/vault/libraries/{id} - change a Vault library's lookup source.</summary>
    Task<VaultCall<VaultLibraryDto>> UpdateVaultLibraryAsync(string libraryId, UpdateVaultLibraryRequest request, CancellationToken ct = default);

    /// <summary>POST /api/v1/vault/libraries/{id}/folders - put a server folder into the Vault library (asks for the administrator's own PIN). The path travels in the body, never in the address.</summary>
    Task<VaultCall<VaultLibraryDto>> AttachVaultFolderAsync(string libraryId, AttachVaultFolderRequest request, CancellationToken ct = default);

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
}
