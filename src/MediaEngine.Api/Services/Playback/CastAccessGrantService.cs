using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MediaEngine.Api.Security;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;
using MediaEngine.Identity;
using Microsoft.AspNetCore.DataProtection;
using RegisteredApplication = MediaEngine.Domain.Entities.Application;

namespace MediaEngine.Api.Services.Playback;

/// <summary>
/// The secret a TV, speaker or browser receiver uses to play ONE title with no sign-in. It is a Data Protection payload
/// placed in the URL path (so relative HLS links keep working) and is not a bearer token: it reads one asset, over GET and HEAD only.
///
/// There are two kinds, told apart by the binding's <c>Kind</c> field, and one can never be validated as the other:
/// a DEVICE grant is made for a paired app and is bound to the asset, the HLS package if any, account, profile, device,
/// application, the token FAMILY of the paired phone that asked (not the ten-minute access token), the account, profile and
/// application authorisation versions, the scopes (they must include playback.read) and the expiry; a SESSION grant is made for
/// a signed-in Dashboard session and is bound to the asset, the package if any, account, profile, the managed session id, the
/// account and profile authorisation versions and the expiry. Neither is bound to an IP address.
///
/// Every request re-checks all of it (see <see cref="ValidateAsync"/>), so signing the phone or the Dashboard session out,
/// switching profile, revoking the device, disabling the account or removing the application's playback permission stops the
/// receiver within about a minute. Its Data Protection purpose differs from <see cref="HlsAccessGrantService"/>, so the two grant
/// families cannot be swapped.
/// </summary>
public sealed class CastAccessGrantService
{
    /// <summary>The Data Protection purpose string. Never share it with another grant kind.</summary>
    public const string ProtectorPurpose = "Tuvima.Library.Playback.CastAccess.v1";

    /// <summary>A film's length plus this long, so a pause of an hour or so does not end the session.</summary>
    public static readonly TimeSpan LifetimeExtra = TimeSpan.FromMinutes(90);

    public static readonly TimeSpan MinimumLifetime = TimeSpan.FromHours(2);
    public static readonly TimeSpan MaximumLifetime = TimeSpan.FromHours(6);

    private const int MaximumGrantLength = 2048;
    private const string DeviceKind = "device";
    private const string SessionKind = "session";

    private readonly IDataProtector _protector;
    private readonly IAccountRepository _accounts;
    private readonly IApplicationRepository _applications;
    private readonly IClientAuthorizationRepository _clients;
    private readonly IPermissionRegistry _permissions;
    private readonly IIdentityRepository? _identities;
    private readonly TimeProvider _timeProvider;

    public CastAccessGrantService(
        IDataProtectionProvider provider,
        IAccountRepository accounts,
        IApplicationRepository applications,
        IClientAuthorizationRepository clients,
        IPermissionRegistry permissions,
        TimeProvider? timeProvider = null,
        IIdentityRepository? identities = null)
    {
        _protector = provider.CreateProtector(ProtectorPurpose);
        _identities = identities;
        _accounts = accounts;
        _applications = applications;
        _clients = clients;
        _permissions = permissions;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Film length plus <see cref="LifetimeExtra"/>, never under two hours or over six.</summary>
    public static TimeSpan LifetimeFor(double? durationSeconds)
    {
        var length = durationSeconds is > 0 && double.IsFinite(durationSeconds.Value)
            ? TimeSpan.FromSeconds(Math.Min(durationSeconds.Value, MaximumLifetime.TotalSeconds))
            : TimeSpan.Zero;
        var lifetime = length + LifetimeExtra;
        return lifetime < MinimumLifetime ? MinimumLifetime : lifetime > MaximumLifetime ? MaximumLifetime : lifetime;
    }

    /// <summary>
    /// A grant for a paired device (an Application or any other principal is refused; a signed-in Dashboard session uses
    /// <see cref="CreateForSessionAsync"/>). The caller's own live access token is looked up so the grant can be tied to its token family.
    /// </summary>
    /// <exception cref="UnauthorizedAccessException">The caller is not a live paired device with playback.read.</exception>
    public async Task<CastAccessGrant> CreateAsync(
        Guid assetId,
        Guid? packageId,
        RequestAuthority authority,
        Guid? tokenId,
        IReadOnlyCollection<string> scopes,
        double? durationSeconds,
        CancellationToken ct = default)
    {
        if (assetId == Guid.Empty ||
            AuthorityValidity.Validate(authority) is not null ||
            authority.PrincipalKind != PrincipalKind.DelegatedUserClient ||
            authority.AccountId is not { } accountId ||
            authority.ActiveProfileId is not { } profileId ||
            authority.ApplicationId is not { } applicationId ||
            authority.DeviceId is not { } deviceId ||
            tokenId is not { } callerTokenId)
        {
            throw new UnauthorizedAccessException("Only a live paired device can start a cast session.");
        }

        var granted = scopes.Where(scope => !string.IsNullOrWhiteSpace(scope))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (!granted.Contains(ApplicationPermissionIds.PlaybackRead.Value, StringComparer.Ordinal))
        {
            throw new UnauthorizedAccessException("Casting needs the playback.read scope.");
        }

        var now = _timeProvider.GetUtcNow();
        var token = await _clients.GetTokenByIdAsync(callerTokenId, ct).ConfigureAwait(false);
        if (token is null || token.RevokedAt is not null || token.ExpiresAt <= now ||
            token.DeviceId != deviceId || token.AccountId != accountId || token.ProfileId != profileId ||
            token.ApplicationId != applicationId)
        {
            throw new UnauthorizedAccessException("The paired device's token is not live.");
        }

        var expiresAt = now.Add(LifetimeFor(durationSeconds));
        var payload = JsonSerializer.Serialize(new CastGrantPayload(
            assetId,
            packageId,
            expiresAt.ToUnixTimeSeconds(),
            new CastBinding(
                DeviceKind,
                accountId,
                profileId,
                applicationId,
                deviceId,
                token.TokenFamilyId,
                authority.AccountAuthorizationVersion,
                authority.ProfileAuthorizationVersion,
                authority.ApplicationAuthorizationVersion,
                granted,
                null)));
        return new CastAccessGrant(_protector.Protect(payload), expiresAt);
    }

    /// <summary>
    /// A grant for a signed-in Dashboard session (a <see cref="PrincipalKind.Human"/> with a managed session id), so the browser's own
    /// device picker can cast. It is bound to the session, so signing out or revoking the session ends the cast at its next request.
    /// The caller has already been cleared for <c>playback.read</c> on the asset, and has checked the asset is not a Vault item.
    /// </summary>
    /// <exception cref="UnauthorizedAccessException">The caller is not a live signed-in Dashboard session.</exception>
    public async Task<CastAccessGrant> CreateForSessionAsync(
        Guid assetId,
        Guid? packageId,
        RequestAuthority authority,
        double? durationSeconds,
        CancellationToken ct = default)
    {
        if (assetId == Guid.Empty || _identities is null ||
            AuthorityValidity.Validate(authority) is not null ||
            authority.PrincipalKind != PrincipalKind.Human ||
            authority.AccountId is not { } accountId ||
            authority.ActiveProfileId is not { } profileId ||
            authority.SessionId is not { } sessionId)
        {
            throw new UnauthorizedAccessException("Only a signed-in Dashboard session can start a browser cast session.");
        }

        // The session must be live right now; a cast made from a session that has just ended must not be created.
        var now = _timeProvider.GetUtcNow();
        var session = await _identities.GetSessionByIdAsync(sessionId, ct).ConfigureAwait(false);
        var sessionAccount = session is null ? null : await _accounts.GetByIdAsync(accountId, ct).ConfigureAwait(false);
        if (session is null || sessionAccount is null || !session.IsActive(now) || session.ProfilePending ||
            session.AccountId != accountId || session.ActiveProfileId != profileId ||
            !await SessionIntegrity.HoldsAsync(_identities, session, sessionAccount, now, ct).ConfigureAwait(false))
        {
            throw new UnauthorizedAccessException("The Dashboard session is not live.");
        }

        var expiresAt = now.Add(LifetimeFor(durationSeconds));
        var payload = JsonSerializer.Serialize(new CastGrantPayload(
            assetId,
            packageId,
            expiresAt.ToUnixTimeSeconds(),
            new CastBinding(
                SessionKind,
                accountId,
                profileId,
                null,
                null,
                null,
                authority.AccountAuthorizationVersion,
                authority.ProfileAuthorizationVersion,
                0,
                [],
                sessionId)));
        return new CastAccessGrant(_protector.Protect(payload), expiresAt);
    }

    /// <summary>
    /// Everything that must hold on EVERY request: the grant is intact and unexpired; the device is unrevoked, still on that
    /// profile and the ids match; the token family is live; the account is enabled and the profile is still in its household, both with the same versions; the
    /// application is enabled and still holds playback.read. Returns null on any failure (the caller answers 403). The asset
    /// and the catalogue's own limits (Kids) are checked by the caller against the returned authority.
    /// </summary>
    public async ValueTask<CastGrantValidation?> ValidateAsync(string? grant, CancellationToken ct = default)
    {
        var payload = Unprotect(grant);
        if (payload is null)
        {
            return null;
        }

        var binding = payload.Binding;
        var now = _timeProvider.GetUtcNow();
        if (string.Equals(binding.Kind, SessionKind, StringComparison.Ordinal))
        {
            return await ValidateSessionAsync(payload, now, ct).ConfigureAwait(false);
        }

        // Anything that is not exactly a device grant (an unknown or missing kind) is refused, so a session grant can
        // never be read as a device grant.
        if (!string.Equals(binding.Kind, DeviceKind, StringComparison.Ordinal) ||
            binding.ApplicationId is not { } applicationId ||
            binding.DeviceId is not { } deviceId ||
            binding.TokenFamilyId is not { } tokenFamilyId)
        {
            return null;
        }

        var accountTask = _accounts.GetByIdAsync(binding.AccountId, ct);
        var profileTask = _accounts.GetActiveProfileAccessAsync(binding.AccountId, binding.ProfileId, ct);
        var applicationTask = _applications.GetApplicationAsync(applicationId, ct);
        var deviceTask = _clients.GetDeviceAsync(deviceId, ct);
        var familyTask = _clients.IsTokenFamilyLiveAsync(tokenFamilyId, now, ct);
        await Task.WhenAll(accountTask, profileTask, applicationTask, deviceTask, familyTask).ConfigureAwait(false);
        var account = await accountTask.ConfigureAwait(false);
        var activeProfile = await profileTask.ConfigureAwait(false);
        var application = await applicationTask.ConfigureAwait(false);
        var device = await deviceTask.ConfigureAwait(false);
        if (account?.IsEnabled != true || activeProfile is null || application?.IsEnabled != true ||
            device?.IsActive != true || !await familyTask.ConfigureAwait(false) ||
            device.AccountId != binding.AccountId || device.ProfileId != binding.ProfileId ||
            device.ApplicationId != applicationId ||
            account.AuthorizationVersion != binding.AccountAuthorizationVersion ||
            activeProfile.AuthorizationVersion != binding.ProfileAuthorizationVersion ||
            application.AuthorizationVersion != binding.ApplicationAuthorizationVersion ||
            !await HasLivePlaybackPermissionAsync(application, binding.Scopes, ct).ConfigureAwait(false))
        {
            return null;
        }

        var currentBinding = await _applications.GetApplicationByClientIdAsync(device.ClientId, ct).ConfigureAwait(false);
        if (currentBinding?.Id != applicationId || currentBinding.ApplicationType != ApplicationType.UserClient)
        {
            return null;
        }

        var authority = new RequestAuthority(
            PrincipalKind.DelegatedUserClient, true, account.Id, activeProfile.ProfileId, application.Id,
            DeviceId: deviceId, AccountEnabled: true, ApplicationEnabled: true,
            AccountAuthorizationVersion: account.AuthorizationVersion,
            ApplicationAuthorizationVersion: application.AuthorizationVersion,
            ApplicationIsAdministrator: application.IsAdministrator).WithActiveProfile(activeProfile);
        return new CastGrantValidation(
            payload.AssetId,
            payload.PackageId,
            DateTimeOffset.FromUnixTimeSeconds(payload.ExpiresAtUnixSeconds),
            authority,
            binding.Scopes);
    }

    /// <summary>
    /// The checks for a SESSION grant, on every request: the managed session still exists and is live (not signed out, revoked or
    /// expired; <c>AuthSession.IsActive</c>), has not been left waiting for a profile pick, and is still the
    /// same account and profile as the grant; the account is enabled and the session still passes <see cref="SessionIntegrity"/>
    /// (password security stamp, temporary password, retired sign-in methods; the same check sign-in validation uses); the profile is still active in its household; the account and
    /// profile authorisation versions are unchanged. The answer is a <see cref="PrincipalKind.Human"/> authority for that profile,
    /// so the catalogue's own limits (asset access, Kids) apply to it unchanged.
    /// </summary>
    private async ValueTask<CastGrantValidation?> ValidateSessionAsync(CastGrantPayload payload, DateTimeOffset now, CancellationToken ct)
    {
        var binding = payload.Binding;
        if (_identities is null || binding.SessionId is not { } sessionId || sessionId == Guid.Empty)
        {
            return null;
        }

        var account = await _accounts.GetByIdAsync(binding.AccountId, ct).ConfigureAwait(false);
        var profile = await _accounts.GetActiveProfileAccessAsync(binding.AccountId, binding.ProfileId, ct).ConfigureAwait(false);
        var session = await _identities.GetSessionByIdAsync(sessionId, ct).ConfigureAwait(false);
        if (account?.IsEnabled != true || profile is null || session is null ||
            !session.IsActive(now) || session.ProfilePending ||
            session.AccountId != binding.AccountId || session.ActiveProfileId != binding.ProfileId ||
            account.AuthorizationVersion != binding.AccountAuthorizationVersion ||
            profile.AuthorizationVersion != binding.ProfileAuthorizationVersion ||
            // The same list sign-in itself uses: a rotated password stamp, an expired temporary password or a retired sign-in method.
            !await SessionIntegrity.HoldsAsync(_identities, session, account, now, ct).ConfigureAwait(false))
        {
            return null;
        }

        var authority = new RequestAuthority(PrincipalKind.Human, true, account.Id, profile.ProfileId, SessionId: sessionId,
            AccountEnabled: true,
            AccountAuthorizationVersion: account.AuthorizationVersion).WithActiveProfile(profile);
        return AuthorityValidity.Validate(authority) is null
            ? new CastGrantValidation(
                payload.AssetId,
                payload.PackageId,
                DateTimeOffset.FromUnixTimeSeconds(payload.ExpiresAtUnixSeconds),
                authority,
                [])
            : null;
    }

    /// <summary>
    /// Eight hex characters of the grant's SHA-256, for logs. The grant itself is a secret and is never written anywhere.
    /// </summary>
    public static string LogId(string? grant) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(grant ?? string.Empty)))[..8].ToLowerInvariant();

    private CastGrantPayload? Unprotect(string? grant)
    {
        if (string.IsNullOrWhiteSpace(grant) || grant.Length > MaximumGrantLength)
        {
            return null;
        }

        try
        {
            var payload = JsonSerializer.Deserialize<CastGrantPayload>(_protector.Unprotect(grant));
            return payload is not null &&
                   payload.AssetId != Guid.Empty &&
                   payload.Binding is not null &&
                   DateTimeOffset.FromUnixTimeSeconds(payload.ExpiresAtUnixSeconds) > _timeProvider.GetUtcNow()
                ? payload
                : null;
        }
        catch (Exception exception) when (exception is CryptographicException or JsonException or ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private async ValueTask<bool> HasLivePlaybackPermissionAsync(
        RegisteredApplication application,
        IReadOnlyList<string> scopes,
        CancellationToken ct)
    {
        var permission = ApplicationPermissionIds.PlaybackRead;
        if (!scopes.Contains(permission.Value, StringComparer.Ordinal) ||
            !_permissions.TryGet(permission, out var definition) || !definition.IsAvailable ||
            !definition.ApplicationTypes.Contains(application.ApplicationType))
        {
            return false;
        }

        if (application.IsAdministrator)
        {
            return true;
        }

        return (await _applications.GetApplicationPermissionsAsync(application.Id, ct).ConfigureAwait(false))
            .Contains(permission);
    }

    private sealed record CastGrantPayload(
        Guid AssetId,
        Guid? PackageId,
        long ExpiresAtUnixSeconds,
        CastBinding Binding);

    /// <summary>
    /// What a grant is bound to. <c>Kind</c> says which kind it is; the device-only ids are null on a session grant, and the
    /// session id is null on a device grant.
    /// </summary>
    private sealed record CastBinding(
        string Kind,
        Guid AccountId,
        Guid ProfileId,
        Guid? ApplicationId,
        Guid? DeviceId,
        Guid? TokenFamilyId,
        long AccountAuthorizationVersion,
        long ProfileAuthorizationVersion,
        long ApplicationAuthorizationVersion,
        IReadOnlyList<string> Scopes,
        Guid? SessionId);
}

public sealed record CastAccessGrant(string Value, DateTimeOffset ExpiresAt);

public sealed record CastGrantValidation(
    Guid AssetId,
    Guid? PackageId,
    DateTimeOffset ExpiresAt,
    RequestAuthority Authority,
    IReadOnlyList<string> Scopes);
