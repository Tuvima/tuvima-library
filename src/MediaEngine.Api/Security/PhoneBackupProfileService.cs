using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;
using MediaEngine.Identity;
using MediaEngine.Identity.Contracts;

namespace MediaEngine.Api.Security;

public enum BackupProfileOutcome
{
    Changed,
    DeviceNotFound,
    ProfileNotFound,
    PinRequired,
    Locked,
}

/// <summary>The person an upload is stored for, or null when a phone has no backup person chosen yet.</summary>
public sealed record BackupTarget(Guid? ProfileId);

/// <summary>
/// Chooses which person a paired phone backs its photos up to. A phone can browse as anyone in the household, so
/// photos always go to this one person and never to whoever the phone happens to be browsing as.
/// </summary>
public sealed class PhoneBackupProfileService(
    IClientAuthorizationRepository devices,
    IAccountRepository accounts,
    IProfileRepository profiles,
    IProfilePinVerifier pins,
    IAccountAccessDecisionService decisions,
    IAuthorizationAuditWriter audit,
    TimeProvider clock)
{
    public const string AuditEvent = "device.backup_profile_changed";
    public const string ChooseBackupProfileCode = "choose_backup_profile";

    /// <summary>
    /// Whose personal space an upload lands in. A browser upload belongs to the person signed in. A paired phone's
    /// upload belongs to the person chosen for that phone, whoever it is browsing as; with no choice there is no target.
    /// </summary>
    public async Task<BackupTarget> ResolveUploadTargetAsync(RequestAuthority authority, Guid browsingProfileId, CancellationToken ct = default)
    {
        if (authority.DeviceId is not { } deviceId)
        {
            return new BackupTarget(browsingProfileId);
        }

        var device = await devices.GetDeviceAsync(deviceId, ct).ConfigureAwait(false);
        return device is { IsActive: true, BackupProfileId: { } backupProfileId }
            ? new BackupTarget(backupProfileId)
            : new BackupTarget(null);
    }

    /// <summary>The phone itself chooses. The person must be in the phone's household, and their PIN is needed when they have one.</summary>
    public async Task<BackupProfileOutcome> SetFromDeviceAsync(
        RequestAuthority authority, Guid deviceId, Guid profileId, string? pin, CancellationToken ct = default)
    {
        var device = await devices.GetDeviceAsync(deviceId, ct).ConfigureAwait(false);
        if (device is null || !device.IsActive)
        {
            return BackupProfileOutcome.DeviceNotFound;
        }

        if (!await InHouseholdAsync(device.AccountId, profileId, ct).ConfigureAwait(false))
        {
            return BackupProfileOutcome.ProfileNotFound;
        }

        // A phone can be anywhere, so wrong PINs always count toward the lockout.
        try
        {
            await pins.VerifyProfilePinAsync(profileId, pin, true, ct).ConfigureAwait(false);
        }
        catch (ProfilePinLockedException)
        {
            return BackupProfileOutcome.Locked;
        }
        catch (ProfilePinRequiredException)
        {
            return BackupProfileOutcome.PinRequired;
        }

        return await ApplyAsync(authority, device.Id, profileId, ct).ConfigureAwait(false);
    }

    /// <summary>A server administrator, or the household administrator of the phone's household, sets or clears it without a PIN.</summary>
    public async Task<BackupProfileOutcome> SetByAdministratorAsync(
        RequestAuthority actor, Guid deviceId, Guid? profileId, CancellationToken ct = default)
    {
        var device = await devices.GetDeviceAsync(deviceId, ct).ConfigureAwait(false);
        var account = device is null ? null : await accounts.GetByIdAsync(device.AccountId, ct).ConfigureAwait(false);
        await RequireAuthorityAsync(actor, account?.HouseholdId, ct).ConfigureAwait(false);
        if (device is null || !device.IsActive)
        {
            return BackupProfileOutcome.DeviceNotFound;
        }

        if (profileId is { } target && !await InHouseholdAsync(device.AccountId, target, ct).ConfigureAwait(false))
        {
            return BackupProfileOutcome.ProfileNotFound;
        }

        return await ApplyAsync(actor, device.Id, profileId, ct).ConfigureAwait(false);
    }

    private async Task<BackupProfileOutcome> ApplyAsync(RequestAuthority actor, Guid deviceId, Guid? profileId, CancellationToken ct)
    {
        if (!await devices.SetBackupProfileAsync(deviceId, profileId, ct).ConfigureAwait(false))
        {
            return BackupProfileOutcome.DeviceNotFound;
        }

        await audit.WriteAsync(new AuthorizationAuditEvent(
            AuditEvent,
            clock.GetUtcNow(),
            actor.AccountId,
            actor.ActiveProfileId,
            actor.ApplicationId,
            "device",
            deviceId.ToString("D"),
            new Dictionary<string, string?> { ["changed"] = "true" }), ct).ConfigureAwait(false);
        return BackupProfileOutcome.Changed;
    }

    private async Task<bool> InHouseholdAsync(Guid deviceAccountId, Guid profileId, CancellationToken ct)
    {
        var account = await accounts.GetByIdAsync(deviceAccountId, ct).ConfigureAwait(false);
        var profile = await profiles.GetByIdAsync(profileId, ct).ConfigureAwait(false);
        return account?.HouseholdId is { } household && profile?.HouseholdId == household;
    }

    /// <summary>Same rule as other household actions: a server administrator, or a household administrator inside their own household. Throws otherwise.</summary>
    private async Task RequireAuthorityAsync(RequestAuthority actor, Guid? householdId, CancellationToken ct)
    {
        if ((await decisions.EvaluateAdministratorAsync(actor, true, ct).ConfigureAwait(false)).IsAllowed)
        {
            return;
        }

        if (!actor.IsEffectiveAdministrator && householdId is { } household && actor.AccountHouseholdId == household &&
            (await decisions.EvaluateHouseholdAdministratorAsync(actor, true, ct).ConfigureAwait(false)).IsAllowed)
        {
            return;
        }

        throw new UnauthorizedAccessException("Unlocked administrator authority is required.");
    }
}
