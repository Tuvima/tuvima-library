using MediaEngine.Contracts.Authentication;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;

namespace MediaEngine.Api.Security;

/// <summary>Tells a paired app that its device was revoked so it can sign out at once.</summary>
public interface IDeviceRevocationNotifier
{
    Task NotifyDeviceRevokedAsync(Guid deviceId, CancellationToken ct = default);
}

public enum ManagedDeviceRevokeOutcome
{
    Revoked,
    NotFound,
    Forbidden,
}

/// <summary>
/// Lists and revokes paired phones and TVs for the Dashboard. An administrator sees every device; anyone
/// else sees only their own account's.
/// </summary>
public sealed class ManagedClientDeviceService(
    IClientAuthorizationRepository devices,
    IAccountRepository accounts,
    IProfileRepository profiles,
    IDeviceRevocationNotifier notifier,
    TimeProvider timeProvider)
{
    public const string RevokedReason = "user_revoked";

    public async Task<IReadOnlyList<ManagedClientDeviceDto>> ListAsync(RequestAuthority authority, CancellationToken ct = default)
    {
        var accountScope = authority.IsEffectiveAdministrator ? (Guid?)null : RequiredAccountId(authority);
        var rows = await devices.GetActiveDevicesAsync(accountScope, ct).ConfigureAwait(false);
        var accountNames = new Dictionary<Guid, string>();
        var profileNames = new Dictionary<Guid, string>();
        var result = new List<ManagedClientDeviceDto>(rows.Count);
        foreach (var device in rows)
        {
            if (!accountNames.TryGetValue(device.AccountId, out var accountName))
            {
                var account = await accounts.GetByIdAsync(device.AccountId, ct).ConfigureAwait(false);
                accountName = string.IsNullOrWhiteSpace(account?.Email) ? "Unknown account" : account.Email;
                accountNames[device.AccountId] = accountName;
            }

            if (!profileNames.TryGetValue(device.ProfileId, out var profileName))
            {
                var profile = await profiles.GetByIdAsync(device.ProfileId, ct).ConfigureAwait(false);
                profileName = profile?.DisplayName ?? string.Empty;
                profileNames[device.ProfileId] = profileName;
            }

            result.Add(new ManagedClientDeviceDto
            {
                Id = device.Id,
                DeviceName = device.DeviceName,
                Platform = device.DeviceClass,
                ClientName = device.ClientName,
                AccountId = device.AccountId,
                AccountDisplayName = accountName,
                ProfileId = device.ProfileId,
                ProfileName = profileName,
                PairedAt = device.CreatedAt,
                LastSeenAt = device.LastSeenAt,
            });
        }

        return result;
    }

    public async Task<ManagedDeviceRevokeOutcome> RevokeAsync(RequestAuthority authority, Guid deviceId, CancellationToken ct = default)
    {
        var device = await devices.GetDeviceAsync(deviceId, ct).ConfigureAwait(false);
        if (device is null || !device.IsActive)
        {
            return ManagedDeviceRevokeOutcome.NotFound;
        }

        if (!authority.IsEffectiveAdministrator && device.AccountId != RequiredAccountId(authority))
        {
            return ManagedDeviceRevokeOutcome.Forbidden;
        }

        if (!await devices.RevokeDeviceByIdAsync(deviceId, timeProvider.GetUtcNow(), RevokedReason, ct).ConfigureAwait(false))
        {
            return ManagedDeviceRevokeOutcome.NotFound;
        }

        await notifier.NotifyDeviceRevokedAsync(deviceId, ct).ConfigureAwait(false);
        return ManagedDeviceRevokeOutcome.Revoked;
    }

    private static Guid RequiredAccountId(RequestAuthority authority) =>
        authority.AccountId ?? throw new UnauthorizedAccessException();
}
