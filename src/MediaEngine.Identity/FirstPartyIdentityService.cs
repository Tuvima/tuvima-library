using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Identity.Contracts;
using Microsoft.AspNetCore.Identity;

namespace MediaEngine.Identity;

public sealed class FirstPartyIdentityService(
    IIdentityRepository identities,
    IAccountRepository accounts,
    IProfileRepository profiles,
    IPasswordHasher<AccountCredential> accountPasswordHasher,
    IPasswordHasher<ProfileCredential> profileSecretHasher,
    TimeProvider timeProvider,
    IAuthenticationPolicyProvider authenticationPolicy) : IFirstPartyIdentityService, IHostAdministratorRecoveryService
{
    private const int MaxFailedAttempts = 5;
    private const int MinimumPasswordLength = 12;
    private const int MaximumPasswordLength = 128;
    private const string PasswordRuleMessage = "Use at least 12 characters, and avoid common passwords or your email address.";
    private const string CommonPasswordsResourceName = "MediaEngine.Identity.common-passwords.txt";
    private static readonly FrozenSet<string> CommonPasswords = LoadCommonPasswords();
    private static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan RecoveryLifetime = TimeSpan.FromDays(365);
    private static readonly TimeSpan PasswordResetLifetime = TimeSpan.FromMinutes(30);
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> sessionIssueLocks = new();
    private readonly SemaphoreSlim bootstrapGate = new(1, 1);

    public Task<bool> IsAdministratorConfiguredAsync(CancellationToken ct = default) =>
        identities.IsAdministratorBootstrapCompletedAsync(ct);

    public async Task<SessionIssueResult> BootstrapAdministratorAsync(string email, string password, string displayName, string deviceId, string deviceName, string client, CancellationToken ct = default, string? pin = null, string ingress = ClientIngress.Remote)
    {
        await bootstrapGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (await identities.IsAdministratorBootstrapCompletedAsync(ct).ConfigureAwait(false))
            {
                throw new InvalidOperationException("The administrator has already been configured.");
            }

            ValidatePassword(password);
            if (!string.IsNullOrEmpty(pin))
            {
                ValidatePin(pin);
            }

            var normalizedEmail = NormalizeEmail(email);
            RejectPasswordMatchingIdentity(password, email, displayName);
            var profile = await profiles.GetByIdAsync(Profile.SeedProfileId, ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The seeded administrator profile is unavailable.");
            profile.DisplayName = string.IsNullOrWhiteSpace(displayName) ? "Administrator" : displayName.Trim();
            if (!await profiles.UpdateAsync(profile, ct).ConfigureAwait(false))
            {
                throw new InvalidOperationException("The administrator profile could not be updated.");
            }

            var now = UtcNow;
            var account = new Account { Id = Account.SeedAccountId, Email = email.Trim(), NormalizedEmail = normalizedEmail, IsEnabled = true, IsAdministrator = true, AuthorizationVersion = 1, CreatedAt = now, UpdatedAt = now };
            await accounts.CreateAccountAsync(account,
                new AccountProfileGrant { AccountId = account.Id, ProfileId = profile.Id, IsDefault = true, IsEnabled = true, AdminEnabled = true, AuthorizationVersion = 1, GrantedAt = now },
                AccountFeatureId.All.ToHashSet(), new HashSet<Guid>(), ct).ConfigureAwait(false);
            var credential = NewAccountCredential(account.Id, password);
            if (!string.IsNullOrEmpty(pin))
            {
                await SetProfilePinAsync(profile.Id, pin, ct).ConfigureAwait(false);
            }

            await identities.UpsertAccountCredentialAsync(credential, ct).ConfigureAwait(false);
            var codes = await ReplaceRecoveryCodesAsync(account.Id, ct).ConfigureAwait(false);
            var issued = await IssueSessionAsync(account, profile, credential.SecurityStamp, "Password", deviceId, deviceName, client, ingress, ct).ConfigureAwait(false);
            await AuditAsync(account.Id, profile.Id, issued.Session.Id, "administrator_bootstrap", true, null, ct).ConfigureAwait(false);
            return issued with { RecoveryCodes = codes };
        }
        finally
        {
            bootstrapGate.Release();
        }
    }

    public async Task<AuthenticationAttemptResult> AuthenticatePasswordAsync(string email, string password, string deviceId, string deviceName, string client, CancellationToken ct = default, string ingress = ClientIngress.Remote)
    {
        Account? account;
        try { account = await accounts.GetByNormalizedEmailAsync(NormalizeEmail(email), ct).ConfigureAwait(false); }
        catch (ArgumentException) { account = null; }
        var credential = account is null ? null : await identities.GetAccountCredentialAsync(account.Id, AccountCredentialKind.Password, ct).ConfigureAwait(false);
        return await AuthenticateAccountAsync(account, credential, password, deviceId, deviceName, client, ingress, ct).ConfigureAwait(false);
    }

    public async Task<SessionIssueResult> CreateExternalSessionAsync(Guid accountId, string provider, string deviceId, string deviceName, string client, CancellationToken ct = default, string ingress = ClientIngress.Remote)
    {
        var account = await accounts.GetByIdAsync(accountId, ct).ConfigureAwait(false) ?? throw new KeyNotFoundException($"Account '{accountId}' was not found.");
        var profile = await GetDefaultProfileAsync(account.Id, ct).ConfigureAwait(false);
        var issued = await IssueSessionAsync(account, profile, $"external:{provider.Trim().ToLowerInvariant()}", "Oidc", deviceId, deviceName, client, ingress, ct).ConfigureAwait(false);
        await AuditAsync(account.Id, profile.Id, issued.Session.Id, "login_oidc", true, provider, ct).ConfigureAwait(false);
        return issued;
    }

    public async Task<SessionIssueResult> CreatePasskeySessionAsync(Guid accountId, string deviceId, string deviceName, string client, CancellationToken ct = default, string ingress = ClientIngress.Remote)
    {
        var account = await accounts.GetByIdAsync(accountId, ct).ConfigureAwait(false) ?? throw new KeyNotFoundException("Account was not found.");
        var profile = await GetDefaultProfileAsync(accountId, ct).ConfigureAwait(false);
        var issued = await IssueSessionAsync(account, profile, "passkey", "Passkey", deviceId, deviceName, client, ingress, ct).ConfigureAwait(false);
        await AuditAsync(account.Id, profile.Id, issued.Session.Id, "login_passkey", true, null, ct).ConfigureAwait(false); return issued;
    }

    public async Task<SessionIssueResult> AcceptInvitationAsync(string token, string password, string deviceId, string deviceName, string client, CancellationToken ct = default, string ingress = ClientIngress.Remote)
    {
        ValidatePassword(password); if (string.IsNullOrWhiteSpace(token))
        {
            throw new UnauthorizedAccessException("The invitation is invalid or expired.");
        }

        var invitation = await accounts.GetActiveInvitationAsync(HashToken(token), UtcNow, ct).ConfigureAwait(false) ?? throw new UnauthorizedAccessException("The invitation is invalid or expired.");
        var account = await accounts.GetByIdAsync(invitation.AccountId, ct).ConfigureAwait(false) ?? throw new UnauthorizedAccessException("The invitation is invalid or expired.");
        if (!account.IsEnabled || await identities.GetAccountCredentialAsync(account.Id, AccountCredentialKind.Password, ct).ConfigureAwait(false) is not null)
        {
            throw new UnauthorizedAccessException("The invitation is invalid or expired.");
        }

        await RejectPasswordMatchingAccountAsync(account.Id, password, ct).ConfigureAwait(false);
        if (!await accounts.ConsumeInvitationAsync(invitation.Id, UtcNow, ct).ConfigureAwait(false))
        {
            throw new UnauthorizedAccessException("The invitation is invalid or expired.");
        }

        var credential = NewAccountCredential(account.Id, password); await identities.UpsertAccountCredentialAsync(credential, ct).ConfigureAwait(false);
        var profile = await GetDefaultProfileAsync(account.Id, ct).ConfigureAwait(false); var issued = await IssueSessionAsync(account, profile, credential.SecurityStamp, "Password", deviceId, deviceName, client, ingress, ct).ConfigureAwait(false);
        await AuditAsync(account.Id, profile.Id, issued.Session.Id, "invitation_accepted", true, null, ct).ConfigureAwait(false); return issued;
    }

    public async Task<SessionValidationResult?> ValidateSessionAsync(string plaintextToken, bool touch = true, CancellationToken ct = default, string? currentIngress = null)
    {
        if (string.IsNullOrWhiteSpace(plaintextToken))
        {
            return null;
        }

        var now = UtcNow;
        var session = await identities.GetSessionByTokenHashAsync(HashToken(plaintextToken), ct).ConfigureAwait(false);
        if (session is null || !session.IsActive(now))
        {
            return null;
        }

        // A session made at home only works from home. The session stays valid (it works again back at home).
        // A null currentIngress means an internal, already-admitted caller that does not carry a request origin.
        if (currentIngress is not null && !ClientIngress.SessionMayContinue(session.IssuedIngress, currentIngress))
        {
            return null;
        }

        var account = await accounts.GetByIdAsync(session.AccountId, ct).ConfigureAwait(false);
        var active = await profiles.GetByIdAsync(session.ActiveProfileId, ct).ConfigureAwait(false);
        if (account is null || !account.IsEnabled || active is null || !await accounts.HasProfileAccessAsync(account.Id, active.Id, ct).ConfigureAwait(false))
        {
            return null;
        }

        if (session.AuthenticationMethod.Equals("Password", StringComparison.OrdinalIgnoreCase))
        {
            var credential = await identities.GetAccountCredentialAsync(account.Id, AccountCredentialKind.Password, ct).ConfigureAwait(false);
            if (!StampMatches(credential?.SecurityStamp, session.SecurityStamp))
            {
                return null;
            }
        }
        else if (session.AuthenticationMethod is "ProfilePin" or "ProfileEntry")
        {
            // Retired sign-in methods: a profile PIN only switches profiles inside a session that an account
            // sign-in already made, so a session that claims to have started from one is never accepted.
            return null;
        }

        if (touch && now - session.LastSeenAt >= TimeSpan.FromMinutes(1))
        {
            await identities.TouchSessionAsync(session.Id, now, ct).ConfigureAwait(false);
            session.LastSeenAt = now;
        }
        return new SessionValidationResult(session, account, active, active);
    }

    public Task<IReadOnlyList<AuthSession>> GetSessionsAsync(Guid accountId, CancellationToken ct = default) => identities.GetSessionsAsync(accountId, ct);

    public async Task<bool> RevokeSessionAsync(Guid sessionId, string reason, CancellationToken ct = default)
    {
        var session = await identities.GetSessionByIdAsync(sessionId, ct).ConfigureAwait(false);
        var revoked = await identities.RevokeSessionAsync(sessionId, UtcNow, SanitizeReason(reason), ct).ConfigureAwait(false);
        if (revoked)
        {
            await accounts.ClearAdminUnlockAsync(sessionId, ct).ConfigureAwait(false);
            await AuditAsync(session?.AccountId, session?.ActiveProfileId, sessionId,
                "session_revoked", true, reason, ct).ConfigureAwait(false);
        }
        return revoked;
    }

    public async Task<int> RevokeOtherSessionsAsync(Guid accountId, Guid currentSessionId, string reason, CancellationToken ct = default)
    {
        var now = UtcNow;
        var otherSessions = (await identities.GetSessionsAsync(accountId, ct).ConfigureAwait(false))
            .Where(session => session.Id != currentSessionId && session.RevokedAt is null)
            .ToArray();
        var activeRevoked = otherSessions.Count(session => session.IsActive(now));
        await identities.RevokeAccountSessionsAsync(
            accountId, now, SanitizeReason(reason), currentSessionId, ct).ConfigureAwait(false);
        foreach (var sessionId in otherSessions.Select(session => session.Id))
        {
            await accounts.ClearAdminUnlockAsync(sessionId, ct).ConfigureAwait(false);
        }
        await AuditAsync(accountId, null, currentSessionId, "other_sessions_revoked", true,
            activeRevoked.ToString(System.Globalization.CultureInfo.InvariantCulture), ct).ConfigureAwait(false);
        return activeRevoked;
    }

    public async Task ChangePasswordAsync(Guid accountId, string currentPassword, string newPassword, Guid? currentSessionId = null, CancellationToken ct = default)
    {
        ValidatePassword(newPassword);
        var credential = await identities.GetAccountCredentialAsync(accountId, AccountCredentialKind.Password, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("This account does not have a local password.");
        if (!Verify(credential, currentPassword, out _))
        {
            throw new UnauthorizedAccessException("The current password is incorrect.");
        }

        await RejectPasswordMatchingAccountAsync(accountId, newPassword, ct).ConfigureAwait(false);

        // Other sessions are explicitly revoked below. Retaining this stamp keeps the deliberately
        // preserved current password session valid on its next validation.
        credential.SecretHash = Hash(credential, newPassword); credential.UpdatedAt = UtcNow; credential.FailedAttemptCount = 0; credential.LockedUntil = null;
        await identities.UpsertAccountCredentialAsync(credential, ct).ConfigureAwait(false);
        await identities.RevokeAccountSessionsAsync(accountId, UtcNow, "password_changed", currentSessionId, ct).ConfigureAwait(false);
        await AuditAsync(accountId, null, currentSessionId, "password_changed", true, null, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<string>> ResetPasswordWithRecoveryCodeAsync(string email, string recoveryCode, string newPassword, CancellationToken ct = default)
    {
        ValidatePassword(newPassword);
        var account = await accounts.GetByNormalizedEmailAsync(NormalizeEmail(email), ct).ConfigureAwait(false) ?? throw InvalidRecovery();
        var credential = await identities.GetAccountCredentialAsync(account.Id, AccountCredentialKind.Password, ct).ConfigureAwait(false) ?? throw InvalidRecovery();
        await RejectPasswordMatchingAccountAsync(account.Id, newPassword, ct).ConfigureAwait(false);
        var code = await identities.GetActiveRecoveryCodeAsync(account.Id, HashToken(NormalizeRecoveryCode(recoveryCode)), UtcNow, ct).ConfigureAwait(false) ?? throw InvalidRecovery();
        if (!await identities.ConsumeRecoveryCodeAsync(code.Id, UtcNow, ct).ConfigureAwait(false))
        {
            throw InvalidRecovery();
        }

        await ReplacePasswordAsync(account.Id, credential, newPassword, "password_recovered", ct).ConfigureAwait(false);
        return await ReplaceRecoveryCodesAsync(account.Id, ct).ConfigureAwait(false);
    }

    public async Task<string?> BeginPasswordResetAsync(string email, CancellationToken ct = default)
    {
        Account? account;
        try { account = await accounts.GetByNormalizedEmailAsync(NormalizeEmail(email), ct).ConfigureAwait(false); }
        catch (ArgumentException) { return null; }
        if (account is null || !account.IsEnabled || await identities.GetAccountCredentialAsync(account.Id, AccountCredentialKind.Password, ct).ConfigureAwait(false) is null)
        {
            return null;
        }

        var token = RandomToken(32); var now = UtcNow;
        await identities.InvalidatePasswordResetChallengesAsync(account.Id, ct).ConfigureAwait(false);
        await identities.InsertPasswordResetChallengeAsync(new PasswordResetChallenge { Id = Guid.NewGuid(), AccountId = account.Id, TokenHash = HashToken(token), CreatedAt = now, ExpiresAt = now.Add(PasswordResetLifetime) }, ct).ConfigureAwait(false);
        await AuditAsync(account.Id, null, null, "password_reset_requested", true, null, ct).ConfigureAwait(false);
        return token;
    }

    public async Task ResetPasswordWithTokenAsync(string token, string newPassword, CancellationToken ct = default)
    {
        ValidatePassword(newPassword);
        if (string.IsNullOrWhiteSpace(token))
        {
            throw InvalidRecovery();
        }

        var challenge = await identities.GetActivePasswordResetChallengeAsync(HashToken(token), UtcNow, ct).ConfigureAwait(false) ?? throw InvalidRecovery();
        await RejectPasswordMatchingAccountAsync(challenge.AccountId, newPassword, ct).ConfigureAwait(false);
        if (!await identities.ConsumePasswordResetChallengeAsync(challenge.Id, UtcNow, ct).ConfigureAwait(false))
        {
            throw InvalidRecovery();
        }

        var credential = await identities.GetAccountCredentialAsync(challenge.AccountId, AccountCredentialKind.Password, ct).ConfigureAwait(false) ?? throw InvalidRecovery();
        await ReplacePasswordAsync(challenge.AccountId, credential, newPassword, "password_reset", ct).ConfigureAwait(false);
        await identities.InvalidatePasswordResetChallengesAsync(challenge.AccountId, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<string>> ResetAdministratorPasswordFromHostAsync(string email, string newPassword, CancellationToken ct = default)
    {
        ValidatePassword(newPassword);
        Account? account;
        try { account = await accounts.GetByNormalizedEmailAsync(NormalizeEmail(email), ct).ConfigureAwait(false); }
        catch (ArgumentException) { account = null; }
        var credential = account is null ? null : await identities.GetAccountCredentialAsync(account.Id, AccountCredentialKind.Password, ct).ConfigureAwait(false);
        if (account is null || credential is null || !account.IsAdministrator)
        {
            throw new UnauthorizedAccessException("The local administrator information is invalid.");
        }

        await RejectPasswordMatchingAccountAsync(account.Id, newPassword, ct).ConfigureAwait(false);
        await ReplacePasswordAsync(account.Id, credential, newPassword, "host_administrator_password_reset", ct).ConfigureAwait(false);
        return await ReplaceRecoveryCodesAsync(account.Id, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<string>> RegenerateRecoveryCodesAsync(Guid accountId, string currentPassword, CancellationToken ct = default)
    {
        var credential = await identities.GetAccountCredentialAsync(accountId, AccountCredentialKind.Password, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("This account does not have a local password.");
        if (!Verify(credential, currentPassword, out _))
        {
            throw new UnauthorizedAccessException("The current password is incorrect.");
        }

        var codes = await ReplaceRecoveryCodesAsync(accountId, ct).ConfigureAwait(false);
        await AuditAsync(accountId, null, null, "recovery_codes_regenerated", true, null, ct).ConfigureAwait(false);
        return codes;
    }

    public Task SetProfilePinAsync(Guid profileId, string? pin, CancellationToken ct = default) => SetProfileSecretAsync(profileId, ProfileCredentialKind.ProfilePin, pin, ct);

    public async Task<SessionValidationResult> SwitchActiveProfileAsync(string sessionToken, Guid targetProfileId, string? pin, CancellationToken ct = default)
    {
        var current = await ValidateSessionAsync(sessionToken, false, ct).ConfigureAwait(false) ?? throw new UnauthorizedAccessException("The session is no longer valid.");
        if (!await accounts.HasProfileAccessAsync(current.Account.Id, targetProfileId, ct).ConfigureAwait(false))
        {
            throw new UnauthorizedAccessException("This account cannot use that profile.");
        }

        var target = await profiles.GetByIdAsync(targetProfileId, ct).ConfigureAwait(false) ?? throw new KeyNotFoundException($"Profile '{targetProfileId}' was not found.");
        var credential = await identities.GetCredentialAsync(target.Id, ProfileCredentialKind.ProfilePin, ct).ConfigureAwait(false);
        if (credential is not null)
        {
            // Same rule as profile sign-in: only sessions made from outside the home count toward, or are
            // blocked by, the PIN lockout. A session's issuing place is checked on every request by the Dashboard.
            var countsTowardLockout = current.Session.IssuedIngress == ClientIngress.Remote;
            var now = UtcNow;
            if (countsTowardLockout && credential.LockedUntil is { } until && until > now)
            {
                throw new ProfilePinLockedException();
            }

            if (string.IsNullOrEmpty(pin))
            {
                throw new ProfilePinRequiredException();
            }

            if (!Verify(credential, pin, out _))
            {
                if (countsTowardLockout)
                {
                    var failures = credential.FailedAttemptCount + 1;
                    DateTimeOffset? locked = failures >= MaxFailedAttempts ? now.Add(LockoutDuration) : null;
                    await identities.UpdateCredentialAttemptAsync(credential.Id, failures, locked, null, ct).ConfigureAwait(false);
                    if (locked is not null)
                    {
                        throw new ProfilePinLockedException();
                    }
                }

                throw new ProfilePinRequiredException();
            }

            if (countsTowardLockout && credential.FailedAttemptCount > 0)
            {
                await identities.UpdateCredentialAttemptAsync(credential.Id, 0, null, now, ct).ConfigureAwait(false);
            }
        }

        if (!await identities.UpdateActiveProfileAsync(current.Session.Id, target.Id, ct).ConfigureAwait(false))
        {
            throw new UnauthorizedAccessException("The session is no longer valid.");
        }

        await accounts.ClearAdminUnlockAsync(current.Session.Id, ct).ConfigureAwait(false);
        current.Session.ActiveProfileId = target.Id;
        await AuditAsync(current.Account.Id, target.Id, current.Session.Id, "active_profile_changed", true, target.Id.ToString("D"), ct).ConfigureAwait(false);
        return new SessionValidationResult(current.Session, current.Account, target, target);
    }

    public async Task<bool> ValidateServiceCredentialAsync(string plaintextToken, CancellationToken ct = default) =>
        !string.IsNullOrWhiteSpace(plaintextToken) && await identities.GetServiceCredentialByHashAsync(HashToken(plaintextToken), ct).ConfigureAwait(false) is not null;

    private async Task<AuthenticationAttemptResult> AuthenticateAccountAsync(Account? account, AccountCredential? credential, string secret, string deviceId, string deviceName, string client, string ingress, CancellationToken ct)
    {
        var now = UtcNow;
        if (account is null || !account.IsEnabled || credential is null) { await AuditAsync(account?.Id, null, null, "login_failed", false, "unknown_credential", ct).ConfigureAwait(false); return new(false, false, "Invalid credentials.", null); }
        // Only internet attempts count toward, or are blocked by, the lockout; home and
        // this-computer attempts are throttled per client address by the Dashboard instead.
        var countsTowardLockout = ClientIngress.Parse(ingress) == ClientIngress.Remote;
        if (countsTowardLockout && credential.LockedUntil is { } until && until > now)
        {
            return new(false, true, "Too many attempts. Try again later.", null);
        }

        if (!Verify(credential, secret, out var rehash)) { if (!countsTowardLockout) { return new(false, false, "Invalid credentials.", null); } var failures = credential.FailedAttemptCount + 1; DateTimeOffset? locked = failures >= MaxFailedAttempts ? now.Add(LockoutDuration) : null; await identities.UpdateAccountCredentialAttemptAsync(credential.Id, failures, locked, null, ct).ConfigureAwait(false); return new(false, locked is not null, "Invalid credentials.", null); }
        if (!countsTowardLockout)
        {
            // A home success must not write back an out-of-date failure count or lock: re-read so an internet lock
            // recorded since this attempt began is kept.
            var fresh = await identities.GetAccountCredentialAsync(account.Id, AccountCredentialKind.Password, ct).ConfigureAwait(false);
            if (fresh is not null) { credential.FailedAttemptCount = fresh.FailedAttemptCount; credential.LockedUntil = fresh.LockedUntil; }
        }

        if (rehash) { credential.SecretHash = Hash(credential, secret); credential.UpdatedAt = now; await identities.UpsertAccountCredentialAsync(credential, ct).ConfigureAwait(false); }
        await identities.UpdateAccountCredentialAttemptAsync(credential.Id, countsTowardLockout ? 0 : credential.FailedAttemptCount, countsTowardLockout ? null : credential.LockedUntil, now, ct).ConfigureAwait(false);
        var profile = await GetDefaultProfileAsync(account.Id, ct).ConfigureAwait(false);
        var issued = await IssueSessionAsync(account, profile, credential.SecurityStamp, "Password", deviceId, deviceName, client, ingress, ct).ConfigureAwait(false);
        await AuditAsync(account.Id, profile.Id, issued.Session.Id, "login_local", true, "Password", ct).ConfigureAwait(false); return new(true, false, null, issued);
    }

    private async Task<Profile> GetDefaultProfileAsync(Guid accountId, CancellationToken ct)
    {
        var id = await accounts.GetDefaultProfileIdAsync(accountId, ct).ConfigureAwait(false) ?? (await accounts.GetProfileIdsAsync(accountId, ct).ConfigureAwait(false)).FirstOrDefault();
        return id != Guid.Empty && await profiles.GetByIdAsync(id, ct).ConfigureAwait(false) is { } profile ? profile : throw new InvalidOperationException("The account has no available profile.");
    }

    private async Task<SessionIssueResult> IssueSessionAsync(Account account, Profile profile, string stamp, string method, string deviceId, string deviceName, string client, string ingress, CancellationToken ct)
    {
        var gate = sessionIssueLocks.GetOrAdd(account.Id, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var now = UtcNow;
            var policy = authenticationPolicy.GetCurrent();
            var lifetime = TimeSpan.FromHours(Math.Clamp(policy.SessionLifetimeHours, 1, 8760));
            var maximumActiveSessions = Math.Clamp(policy.MaximumActiveSessions, 1, 100);
            var active = (await identities.GetSessionsAsync(account.Id, ct).ConfigureAwait(false))
                .Where(session => session.IsActive(now))
                .OrderBy(session => session.LastSeenAt)
                .ThenBy(session => session.CreatedAt)
                .ToArray();
            foreach (var oldest in active.Take(Math.Max(0, active.Length - maximumActiveSessions + 1)))
            {
                await RevokeSessionAsync(oldest.Id, "session_limit", ct).ConfigureAwait(false);
            }

            var token = RandomToken(32);
            var session = new AuthSession
            {
                Id = Guid.NewGuid(),
                AccountId = account.Id,
                ActiveProfileId = profile.Id,
                TokenHash = HashToken(token),
                DeviceId = Sanitize(deviceId, 100, "unknown"),
                DeviceName = Sanitize(deviceName, 100, "Unknown device"),
                Client = Sanitize(client, 200, "Dashboard"),
                AuthenticationMethod = method,
                IssuedIngress = ClientIngress.Parse(ingress),
                SecurityStamp = stamp,
                CreatedAt = now,
                LastSeenAt = now,
                ExpiresAt = now.Add(lifetime),
            };
            await identities.InsertSessionAsync(session, ct).ConfigureAwait(false);
            return new(session, account, profile, profile, token, []);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task SetProfileSecretAsync(Guid profileId, ProfileCredentialKind kind, string? secret, CancellationToken ct)
    {
        _ = await profiles.GetByIdAsync(profileId, ct).ConfigureAwait(false) ?? throw new KeyNotFoundException($"Profile '{profileId}' was not found.");
        if (string.IsNullOrWhiteSpace(secret)) { await identities.DeleteCredentialAsync(profileId, kind, ct).ConfigureAwait(false); return; }
        ValidatePin(secret); var credential = await identities.GetCredentialAsync(profileId, kind, ct).ConfigureAwait(false) ?? NewProfileCredential(profileId, kind, secret);
        credential.SecretHash = Hash(credential, secret); credential.SecurityStamp = NewSecurityStamp(); credential.UpdatedAt = UtcNow; credential.FailedAttemptCount = 0; credential.LockedUntil = null;
        await identities.UpsertCredentialAsync(credential, ct).ConfigureAwait(false);
    }

    private async Task ReplacePasswordAsync(Guid accountId, AccountCredential credential, string password, string reason, CancellationToken ct)
    { credential.SecretHash = Hash(credential, password); credential.SecurityStamp = NewSecurityStamp(); credential.UpdatedAt = UtcNow; credential.FailedAttemptCount = 0; credential.LockedUntil = null; await identities.UpsertAccountCredentialAsync(credential, ct).ConfigureAwait(false); await identities.RevokeAccountSessionsAsync(accountId, UtcNow, reason, null, ct).ConfigureAwait(false); await AuditAsync(accountId, null, null, reason, true, null, ct).ConfigureAwait(false); }

    private async Task<IReadOnlyList<string>> ReplaceRecoveryCodesAsync(Guid accountId, CancellationToken ct)
    { var now = UtcNow; var plaintext = Enumerable.Range(0, 10).Select(_ => RecoveryCode()).ToArray(); var rows = plaintext.Select(code => new PasswordRecoveryCode { Id = Guid.NewGuid(), AccountId = accountId, CodeHash = HashToken(NormalizeRecoveryCode(code)), CreatedAt = now, ExpiresAt = now.Add(RecoveryLifetime) }).ToArray(); await identities.DeleteRecoveryCodesAsync(accountId, ct).ConfigureAwait(false); await identities.InsertRecoveryCodesAsync(rows, ct).ConfigureAwait(false); return plaintext; }

    private AccountCredential NewAccountCredential(Guid accountId, string secret) { var now = UtcNow; var c = new AccountCredential { Id = Guid.NewGuid(), AccountId = accountId, Kind = AccountCredentialKind.Password, SecurityStamp = NewSecurityStamp(), CreatedAt = now, UpdatedAt = now }; c.SecretHash = Hash(c, secret); return c; }
    private ProfileCredential NewProfileCredential(Guid profileId, ProfileCredentialKind kind, string secret) { var now = UtcNow; var c = new ProfileCredential { Id = Guid.NewGuid(), ProfileId = profileId, Kind = kind, SecurityStamp = NewSecurityStamp(), CreatedAt = now, UpdatedAt = now }; c.SecretHash = Hash(c, secret); return c; }
    private string Hash(AccountCredential c, string secret) => accountPasswordHasher.HashPassword(c, $"tuvima:{c.Kind}:v1\n{secret}");
    private bool Verify(AccountCredential c, string secret, out bool rehash) { var r = accountPasswordHasher.VerifyHashedPassword(c, c.SecretHash, $"tuvima:{c.Kind}:v1\n{secret}"); rehash = r == PasswordVerificationResult.SuccessRehashNeeded; return r is PasswordVerificationResult.Success or PasswordVerificationResult.SuccessRehashNeeded; }
    private string Hash(ProfileCredential c, string secret) => profileSecretHasher.HashPassword(c, $"tuvima:{c.Kind}:v1\n{secret}");
    private bool Verify(ProfileCredential c, string secret, out bool rehash) { var r = profileSecretHasher.VerifyHashedPassword(c, c.SecretHash, $"tuvima:{c.Kind}:v1\n{secret}"); rehash = r == PasswordVerificationResult.SuccessRehashNeeded; return r is PasswordVerificationResult.Success or PasswordVerificationResult.SuccessRehashNeeded; }
    private Task AuditAsync(Guid? accountId, Guid? profileId, Guid? sessionId, string type, bool ok, string? detail, CancellationToken ct) => identities.WriteAuditEventAsync(accountId, profileId, sessionId, type, ok, detail, UtcNow, ct);
    private DateTimeOffset UtcNow => timeProvider.GetUtcNow();
    private static string NormalizeEmail(string value) { if (string.IsNullOrWhiteSpace(value)) { throw new ArgumentException("Email is required."); } try { return new MailAddress(value.Trim()).Address.ToUpperInvariant(); } catch (FormatException) { throw new ArgumentException("Enter a valid email address.", nameof(value)); } }
    private static bool StampMatches(string? actual, string expected) => actual is not null && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(actual), Encoding.UTF8.GetBytes(expected));
    private static string NewSecurityStamp() => RandomToken(24);
    private static string RandomToken(int bytes) => Convert.ToBase64String(RandomNumberGenerator.GetBytes(bytes)).Replace('+', '-').Replace('/', '_').TrimEnd('=');
    private static string HashToken(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static string RecoveryCode() { var raw = Convert.ToHexString(RandomNumberGenerator.GetBytes(10)); return string.Join('-', Enumerable.Range(0, 4).Select(i => raw.Substring(i * 5, 5))); }
    private static string NormalizeRecoveryCode(string code) => code.Replace("-", string.Empty, StringComparison.Ordinal).Trim().ToUpperInvariant();
    private static UnauthorizedAccessException InvalidRecovery() => new("The recovery information is invalid.");
    private static void ValidatePassword(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length > MaximumPasswordLength)
        {
            throw new ArgumentException($"Use {MaximumPasswordLength} characters or fewer.");
        }

        if (value.Length < MinimumPasswordLength || CommonPasswords.Contains(value.ToLowerInvariant()))
        {
            throw new ArgumentException(PasswordRuleMessage);
        }
    }
    private static void RejectPasswordMatchingIdentity(string password, string? email, string? displayName)
    {
        if ((!string.IsNullOrWhiteSpace(email) && string.Equals(password, email.Trim(), StringComparison.OrdinalIgnoreCase))
            || (!string.IsNullOrWhiteSpace(displayName) && string.Equals(password, displayName.Trim(), StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException(PasswordRuleMessage);
        }
    }
    private async Task RejectPasswordMatchingAccountAsync(Guid accountId, string password, CancellationToken ct)
    {
        var account = await accounts.GetByIdAsync(accountId, ct).ConfigureAwait(false) ?? throw new KeyNotFoundException("Account was not found.");
        var profile = await GetDefaultProfileAsync(accountId, ct).ConfigureAwait(false);
        RejectPasswordMatchingIdentity(password, account.Email, profile.DisplayName);
    }
    private static FrozenSet<string> LoadCommonPasswords()
    {
        using var stream = typeof(FirstPartyIdentityService).Assembly.GetManifestResourceStream(CommonPasswordsResourceName)
            ?? throw new InvalidOperationException("The common password list is missing from this build.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var entries = new List<string>();
        while (reader.ReadLine() is { } line)
        {
            var entry = line.Trim().ToLowerInvariant();
            if (entry.Length > 0)
            {
                entries.Add(entry);
            }
        }

        return entries.ToFrozenSet(StringComparer.Ordinal);
    }
    private static void ValidatePin(string value)
    {
        if (value.Length is < 4 or > 12 || value.Any(c => !char.IsAsciiDigit(c)))
        {
            throw new ArgumentException("PIN must contain 4 to 12 digits.");
        }
    }
    private static string Sanitize(string? value, int max, string fallback) { var result = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim(); return result.Length <= max ? result : result[..max]; }
    private static string SanitizeReason(string value) => Sanitize(value, 100, "revoked");
}
