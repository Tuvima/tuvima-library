using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Entities;

namespace MediaEngine.Domain.Contracts;

public interface IClientAuthorizationRepository
{
    Task InsertPairingAsync(DevicePairingRequest request, CancellationToken ct = default);
    Task<DevicePairingRequest?> GetPairingByDeviceCodeHashAsync(string hash, CancellationToken ct = default);
    Task<DevicePairingRequest?> GetPairingByUserCodeHashAsync(string hash, CancellationToken ct = default);
    Task RecordPairingPollAsync(Guid requestId, DateTimeOffset polledAt, int intervalSeconds, CancellationToken ct = default);
    Task<bool> DecidePairingAsync(Guid requestId, bool approved, Guid accountId, Guid profileId, Guid approvedByProfileId, string scopes, DateTimeOffset now, CancellationToken ct = default);
    Task<bool> ConsumePairingAsync(DevicePairingRequest pairing, ClientDevice device, ClientToken accessToken, ClientToken refreshToken, DateTimeOffset now, CancellationToken ct = default);
    Task<(ClientToken Token, ClientDevice Device)?> FindActiveAccessTokenAsync(string hash, DateTimeOffset now, CancellationToken ct = default);
    Task<(ClientToken Token, ClientDevice Device)?> FindRefreshTokenAsync(string hash, CancellationToken ct = default);
    Task<ClientToken?> GetTokenByIdAsync(Guid tokenId, CancellationToken ct = default);
    Task<bool> RotateRefreshTokenAsync(ClientToken current, ClientToken nextAccess, ClientToken nextRefresh, DateTimeOffset now, CancellationToken ct = default);
    Task RevokeTokenFamilyAsync(Guid tokenFamilyId, DateTimeOffset now, string reason, CancellationToken ct = default);
    Task<IReadOnlyList<ClientDevice>> GetDevicesAsync(Guid profileId, CancellationToken ct = default);
    Task<ClientDevice?> GetDeviceAsync(Guid deviceId, CancellationToken ct = default);
    Task<bool> UpdateCapabilitiesAsync(Guid deviceId, string capabilitiesJson, DateTimeOffset now, CancellationToken ct = default);
    Task<bool> RevokeDeviceAsync(Guid deviceId, Guid profileId, DateTimeOffset now, string reason, CancellationToken ct = default);

    /// <summary>Active (not revoked) devices; every account's when <paramref name="accountId"/> is null.</summary>
    Task<IReadOnlyList<ClientDevice>> GetActiveDevicesAsync(Guid? accountId, CancellationToken ct = default);

    /// <summary>Revokes a device and all of its tokens whichever profile it was paired under.</summary>
    Task<bool> RevokeDeviceByIdAsync(Guid deviceId, DateTimeOffset now, string reason, CancellationToken ct = default);

    /// <summary>
    /// Moves an active device to <paramref name="newProfileId"/> in one transaction: rebinds the device (only while it is still
    /// bound to <paramref name="accountId"/> and to <paramref name="expectedProfileId"/>, the profile the caller authenticated as,
    /// and only while the caller's own access token <paramref name="callerTokenId"/> is not revoked), revokes every token the
    /// device holds, stores the new access and refresh tokens, and writes <paramref name="audit"/>. Returns false, changing
    /// nothing, when the device is revoked, has moved, or the caller's token is no longer live. If the audit row cannot be written, nothing changes.
    /// </summary>
    Task<bool> SwitchDeviceProfileAsync(Guid deviceId, Guid accountId, Guid expectedProfileId, Guid callerTokenId, Guid newProfileId, ClientToken accessToken, ClientToken refreshToken, DateTimeOffset now, string reason, AuthorizationAuditEvent audit, CancellationToken ct = default);

    /// <summary>Sets (or clears, with null) the person an active device backs its photos up to.</summary>
    Task<bool> SetBackupProfileAsync(Guid deviceId, Guid? profileId, CancellationToken ct = default);
}
