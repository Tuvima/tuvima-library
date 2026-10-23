using MediaEngine.Contracts.Authentication;
using MediaEngine.Domain.Aggregates;
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
/// Lists and revokes paired phones and TVs for the Dashboard. A server administrator who has unlocked their
/// administrator PIN (when they use one) sees every device; a household administrator who has unlocked theirs sees their
/// own household's, except those of a server administrator or another household administrator; anyone else, including an
/// administrator who has not unlocked yet, sees only their own account's. The same rule as the sessions list.
/// </summary>
public sealed class ManagedClientDeviceService(
    IClientAuthorizationRepository devices,
    IAccountRepository accounts,
    IProfileRepository profiles,
    IDeviceRevocationNotifier notifier,
    IAccountAccessDecisionService decisions,
    TimeProvider timeProvider)
{
    public const string RevokedReason = "user_revoked";

    public async Task<IReadOnlyList<ManagedClientDeviceDto>> ListAsync(RequestAuthority authority, CancellationToken ct = default)
    {
        var everyone = await IsUnlockedServerAdministratorAsync(authority, ct).ConfigureAwait(false);
        var householdScope = everyone ? null : await HouseholdScopeAsync(authority, ct).ConfigureAwait(false);
        var accountScope = everyone || householdScope is not null
            ? (Guid?)null
            : RequiredAccountId(authority);
        var rows = await devices.GetActiveDevicesAsync(accountScope, ct).ConfigureAwait(false);
        var accountNames = new Dictionary<Guid, string>();
        var accountProfiles = new Dictionary<Guid, Profile?>();
        var profileNames = new Dictionary<Guid, string>();
        var result = new List<ManagedClientDeviceDto>(rows.Count);
        foreach (var device in rows)
        {
            if (!accountNames.TryGetValue(device.AccountId, out var accountName))
            {
                var account = await accounts.GetByIdAsync(device.AccountId, ct).ConfigureAwait(false);
                accountName = account is null ? "Unknown account" : string.IsNullOrWhiteSpace(account.Email) ? "No email" : account.Email;
                accountNames[device.AccountId] = accountName;
                accountProfiles[device.AccountId] = account is null
                    ? null
                    : await profiles.GetByIdAsync(account.ProfileId, ct).ConfigureAwait(false);
            }

            // A household administrator reaches only the devices of ordinary sign-ins in their own household.
            if (householdScope is { } household && !InScope(household, authority, device.AccountId, accountProfiles[device.AccountId]))
            {
                continue;
            }

            if (!profileNames.TryGetValue(device.ProfileId, out var profileName))
            {
                var profile = await profiles.GetByIdAsync(device.ProfileId, ct).ConfigureAwait(false);
                profileName = profile?.DisplayName ?? string.Empty;
                profileNames[device.ProfileId] = profileName;
            }

            string? backupName = null;
            if (device.BackupProfileId is { } backupId)
            {
                if (!profileNames.TryGetValue(backupId, out backupName))
                {
                    backupName = (await profiles.GetByIdAsync(backupId, ct).ConfigureAwait(false))?.DisplayName ?? string.Empty;
                    profileNames[backupId] = backupName;
                }
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
                BackupProfileId = device.BackupProfileId,
                HouseholdId = accountProfiles[device.AccountId]?.HouseholdId,
                BacksUpTo = backupName,
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

        if (device.AccountId != RequiredAccountId(authority) &&
            !await IsUnlockedServerAdministratorAsync(authority, ct).ConfigureAwait(false) &&
            !await IsManagedByHouseholdAdministratorAsync(authority, device.AccountId, ct).ConfigureAwait(false))
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

    /// <summary>
    /// A server administrator whose administrator PIN is unlocked (or who uses none). Until then they are like anyone
    /// else here: their own devices only, exactly as in the sessions list.
    /// </summary>
    private async Task<bool> IsUnlockedServerAdministratorAsync(RequestAuthority authority, CancellationToken ct) =>
        authority.PrincipalKind == PrincipalKind.Human && authority.IsEffectiveAdministrator &&
        (await decisions.EvaluateAdministratorAsync(authority, true, ct).ConfigureAwait(false)).IsAllowed;

    /// <summary>
    /// The household a household administrator is limited to, or <see langword="null"/> for everyone else. Like the
    /// sessions list, it needs the administrator PIN unlocked when the person uses one; until then they see only their own.
    /// </summary>
    private async Task<Guid?> HouseholdScopeAsync(RequestAuthority authority, CancellationToken ct) =>
        authority.PrincipalKind == PrincipalKind.Human && !authority.IsEffectiveAdministrator &&
        authority.ActiveProfileHouseholdId is { } household &&
        (await decisions.EvaluateHouseholdAdministratorAsync(authority, true, ct).ConfigureAwait(false)).IsAllowed
            ? household
            : null;

    private async Task<bool> IsManagedByHouseholdAdministratorAsync(RequestAuthority authority, Guid accountId, CancellationToken ct) =>
        await HouseholdScopeAsync(authority, ct).ConfigureAwait(false) is { } household &&
        await accounts.GetByIdAsync(accountId, ct).ConfigureAwait(false) is { } owner &&
        InScope(household, authority, accountId, await profiles.GetByIdAsync(owner.ProfileId, ct).ConfigureAwait(false));

    /// <summary>The same rule as the sessions list: another administrator's sign-in is not a household administrator's to manage.</summary>
    private static bool InScope(Guid household, RequestAuthority actor, Guid accountId, Profile? profile) =>
        profile is not null && profile.HouseholdId == household &&
        (accountId == actor.AccountId || !(profile.IsServerAdministrator || profile.IsHouseholdAdministrator));

    private static Guid RequiredAccountId(RequestAuthority authority) =>
        authority.AccountId ?? throw new UnauthorizedAccessException();
}
