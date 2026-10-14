using MediaEngine.Contracts.Authentication;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Identity;
using MediaEngine.Identity.Contracts;

namespace MediaEngine.Api.Security;

public enum DeviceProfileSwitchOutcome
{
    Switched,

    /// <summary>The caller is not the paired app of a live device of its own account.</summary>
    Forbidden,

    /// <summary>The account has no enabled grant to that profile. An unknown profile id gets the same answer, so ids cannot be probed.</summary>
    ProfileNotAllowed,
    PinRequired,
    Locked,

    /// <summary>The device, account or Application changed while switching, or another switch got there first.</summary>
    DeviceUnavailable,
}

public sealed record DeviceProfileSwitchResult(
    DeviceProfileSwitchOutcome Outcome,
    OAuthTokenResponse? Tokens = null,
    string? Description = null);

/// <summary>
/// Lets a paired phone or television switch person without re-pairing. It adds no rule of its own: the people offered
/// and allowed are the profiles the device's account is granted (the Dashboard's profile switch uses the same grant
/// check), and the PIN goes through <see cref="IProfilePinVerifier"/>, the same check and lockout as the Dashboard's
/// switch. One device has one active profile, so a switch revokes every earlier token of the device and issues a new pair.
/// </summary>
public sealed class DeviceProfileSwitchService(
    IClientAuthorizationRepository devices,
    ClientAuthorizationService authorization,
    IAccountRepository accounts,
    IProfileRepository profiles,
    IFirstPartyIdentityService identity,
    IProfilePinVerifier pins,
    TimeProvider clock)
{
    public const string AuditEvent = "device.profile_switched";

    /// <summary>The people this device may switch to, or null when the caller is not the paired app of a live device of its own account.</summary>
    public async Task<DeviceProfileListDto?> ListAsync(RequestAuthority authority, CancellationToken ct = default)
    {
        var device = await ResolveDeviceAsync(authority, ct).ConfigureAwait(false);
        if (device is null)
        {
            return null;
        }

        var grantedIds = await accounts.GetProfileIdsAsync(device.AccountId, ct).ConfigureAwait(false);
        var withPin = await identity.GetProfileIdsWithPinAsync(grantedIds, ct).ConfigureAwait(false);
        var list = new List<DeviceProfileDto>(grantedIds.Count);
        foreach (var profileId in grantedIds)
        {
            var profile = await profiles.GetByIdAsync(profileId, ct).ConfigureAwait(false);
            if (profile is null)
            {
                continue;
            }

            list.Add(new DeviceProfileDto
            {
                ProfileId = profile.Id,
                DisplayName = profile.DisplayName,
                IsKids = profile.Role == ProfileRole.RestrictedProfile,
                RequiresPin = withPin.Contains(profile.Id),
                IsActive = profile.Id == device.ProfileId,
            });
        }

        return new DeviceProfileListDto { Profiles = list };
    }

    /// <param name="callerTokenId">The caller's own access token (its <c>tuvima:token_id</c> claim); the switch needs it to still be live.</param>
    public async Task<DeviceProfileSwitchResult> SwitchAsync(
        RequestAuthority authority, Guid callerTokenId, Guid targetProfileId, string? pin, CancellationToken ct = default)
    {
        var device = await ResolveDeviceAsync(authority, ct).ConfigureAwait(false);
        if (device is null || callerTokenId == Guid.Empty || authority.ActiveProfileId is not { } expectedProfileId)
        {
            return new(DeviceProfileSwitchOutcome.Forbidden, Description: "Only the paired app of a live device can switch profile.");
        }

        // Same grant check, in the same order, as the Dashboard's switch: an enabled grant on the device's own account.
        // It comes before anything that looks the profile up, so a profile that does not exist and one this account
        // cannot use are indistinguishable.
        if (!await accounts.HasProfileAccessAsync(device.AccountId, targetProfileId, ct).ConfigureAwait(false))
        {
            return new(DeviceProfileSwitchOutcome.ProfileNotAllowed, Description: "This account cannot use that profile.");
        }

        // A device can be anywhere, so wrong PINs always count toward the lockout, and the PIN is asked at every switch.
        try
        {
            await pins.VerifyProfilePinAsync(targetProfileId, pin, true, ct).ConfigureAwait(false);
        }
        catch (ProfilePinLockedException)
        {
            return new(DeviceProfileSwitchOutcome.Locked, Description: "Too many attempts. Try again later.");
        }
        catch (ProfilePinRequiredException)
        {
            return new(DeviceProfileSwitchOutcome.PinRequired, Description: "Enter this profile's PIN.");
        }

        // The audit entry is stored in the switch's own transaction. from_profile_id is the profile the caller
        // authenticated as, not whatever the device is bound to by the time the switch runs.
        var audit = new AuthorizationAuditEvent(
            AuditEvent,
            clock.GetUtcNow(),
            authority.AccountId,
            targetProfileId,
            authority.ApplicationId,
            "device",
            device.Id.ToString("D"),
            new Dictionary<string, string?>
            {
                ["from_profile_id"] = expectedProfileId.ToString("D"),
                ["to_profile_id"] = targetProfileId.ToString("D"),
            });
        var result = await authorization.SwitchDeviceProfileAsync(
            device.Id, device.AccountId, expectedProfileId, callerTokenId, targetProfileId, audit, ct).ConfigureAwait(false);
        if (result.Success is null)
        {
            var error = result.Error;
            return new(
                error?.Error == ClientAuthorizationService.ProfileNotAllowedError
                    ? DeviceProfileSwitchOutcome.ProfileNotAllowed
                    : DeviceProfileSwitchOutcome.DeviceUnavailable,
                Description: error?.ErrorDescription);
        }

        return new(DeviceProfileSwitchOutcome.Switched, result.Success);
    }

    /// <summary>The caller's own live device: only a paired app's token counts (a Dashboard session may carry a device id the browser supplied).</summary>
    private async Task<ClientDevice?> ResolveDeviceAsync(RequestAuthority authority, CancellationToken ct)
    {
        if (!PhoneBackupProfileService.IsPairedApp(authority))
        {
            return null;
        }

        var device = await devices.GetDeviceAsync(authority.DeviceId!.Value, ct).ConfigureAwait(false);
        return device is { IsActive: true } && device.AccountId == authority.AccountId ? device : null;
    }
}
