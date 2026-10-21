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

    /// <summary>POST /api/v1/vault/libraries/{id}/remove - remove a Vault library.</summary>
    Task<VaultCall<bool>> RemoveVaultLibraryAsync(string libraryId, RemoveVaultLibraryRequest request, CancellationToken ct = default);
}
