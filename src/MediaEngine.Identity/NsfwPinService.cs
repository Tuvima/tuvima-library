using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Identity.Contracts;
using Microsoft.AspNetCore.Identity;

namespace MediaEngine.Identity;

/// <summary>Default <see cref="INsfwPinService"/>: the profile PIN's hashing and pause rules, applied to the NSFW PIN's own record.</summary>
public sealed class NsfwPinService(
    INsfwAccessRepository nsfw,
    IIdentityRepository identities,
    IPasswordHasher<ProfileCredential> hasher,
    TimeProvider clock) : INsfwPinService
{
    public async Task<bool> HasPinAsync(Guid profileId, CancellationToken ct = default) =>
        await nsfw.GetPinAsync(profileId, ct).ConfigureAwait(false) is not null;

    public async Task SetPinAsync(Guid profileId, string newPin, string? currentPin, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(newPin);
        ProfileSecretFormat.ValidatePin(newPin);
        var access = await nsfw.GetAsync(profileId, ct).ConfigureAwait(false);
        if (!access.CanUnlock)
        {
            throw new InvalidOperationException("NSFW mode is not available for this profile.");
        }

        var existing = await nsfw.GetPinAsync(profileId, ct).ConfigureAwait(false);
        if (existing is not null)
        {
            // Changing a PIN needs the current one, so a person at a signed-in device cannot swap it out.
            await VerifyPinAsync(profileId, currentPin, ct).ConfigureAwait(false);
        }

        await RejectSameDigitsAsProfilePinAsync(profileId, newPin, ct).ConfigureAwait(false);

        var now = clock.GetUtcNow();
        var credential = existing ?? new ProfileCredential
        {
            Id = Guid.NewGuid(),
            ProfileId = profileId,
            Kind = ProfileCredentialKind.NsfwPin,
            CreatedAt = now,
        };
        credential.Kind = ProfileCredentialKind.NsfwPin;
        credential.SecretHash = hasher.HashPassword(credential, ProfileSecretFormat.Format(credential.Kind, newPin));
        credential.SecurityStamp = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24));
        credential.UpdatedAt = now;
        credential.FailedAttemptCount = 0;
        credential.LockedUntil = null;
        await nsfw.UpsertPinAsync(credential, ct).ConfigureAwait(false);
    }

    public Task<bool> ResetPinAsync(Guid profileId, CancellationToken ct = default) => nsfw.DeletePinAsync(profileId, ct);

    public async Task VerifyPinAsync(Guid profileId, string? pin, CancellationToken ct = default)
    {
        var credential = await nsfw.GetPinAsync(profileId, ct).ConfigureAwait(false);
        if (credential is null)
        {
            // No NSFW PIN means no way in; this looks exactly like a wrong PIN.
            throw new ProfilePinRequiredException();
        }

        var now = clock.GetUtcNow();
        if (credential.LockedUntil is { } until && until > now)
        {
            throw new ProfilePinLockedException();
        }

        if (string.IsNullOrEmpty(pin))
        {
            throw new ProfilePinRequiredException();
        }

        // The try is reserved before the guess is checked, in one conditional statement, so a burst of parallel guesses cannot
        // all see "unlocked": once the limit's worth of tries is out, no row is reserved and the guess is not even checked.
        var attempt = await nsfw.ReservePinAttemptAsync(credential.Id, ProfileSecretFormat.MaxFailedAttempts, now, ct).ConfigureAwait(false);
        if (attempt is null)
        {
            await nsfw.LockExhaustedPinAsync(credential.Id, ProfileSecretFormat.MaxFailedAttempts, now.Add(ProfileSecretFormat.LockoutDuration), CancellationToken.None).ConfigureAwait(false);
            throw new ProfilePinLockedException();
        }

        if (!Matches(credential, pin))
        {
            if (attempt >= ProfileSecretFormat.MaxFailedAttempts)
            {
                await nsfw.LockExhaustedPinAsync(credential.Id, ProfileSecretFormat.MaxFailedAttempts, now.Add(ProfileSecretFormat.LockoutDuration), CancellationToken.None).ConfigureAwait(false);
                throw new ProfilePinLockedException();
            }

            throw new ProfilePinRequiredException();
        }

        // A right guess clears the count only while no pause is running; a pause a parallel burst just set is never undone by it.
        if (!await nsfw.ResetPinAttemptsIfUnlockedAsync(credential.Id, now, CancellationToken.None).ConfigureAwait(false))
        {
            throw new ProfilePinLockedException();
        }
    }

    private bool Matches(ProfileCredential credential, string pin)
    {
        var result = hasher.VerifyHashedPassword(credential, credential.SecretHash, ProfileSecretFormat.Format(credential.Kind, pin));
        return result is PasswordVerificationResult.Success or PasswordVerificationResult.SuccessRehashNeeded;
    }

    /// <summary>
    /// The NSFW PIN and the profile PIN are never the same digits. The comparison counts as a wrong try against the profile PIN and
    /// is refused outright while that PIN is paused, so saving an NSFW PIN can never be used to guess the profile PIN.
    /// </summary>
    private async Task RejectSameDigitsAsProfilePinAsync(Guid profileId, string pin, CancellationToken ct)
    {
        if (await identities.GetCredentialAsync(profileId, ProfileCredentialKind.ProfilePin, ct).ConfigureAwait(false) is not { } profilePin)
        {
            return;
        }

        var now = clock.GetUtcNow();
        if (profilePin.LockedUntil is { } until && until > now)
        {
            throw new ProfilePinLockedException();
        }

        if (!Matches(profilePin, pin))
        {
            return;
        }

        var attempt = await identities.ReserveCredentialAttemptAsync(profilePin.Id, ProfileSecretFormat.MaxFailedAttempts, now, CancellationToken.None).ConfigureAwait(false);
        if (attempt is null || attempt >= ProfileSecretFormat.MaxFailedAttempts)
        {
            await identities.LockExhaustedCredentialAsync(profilePin.Id, ProfileSecretFormat.MaxFailedAttempts, now.Add(ProfileSecretFormat.LockoutDuration), CancellationToken.None).ConfigureAwait(false);
        }

        throw new ArgumentException("Choose a PIN that is different from your other PIN.");
    }
}
