using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MediaEngine.Api.Security;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;
using Microsoft.AspNetCore.DataProtection;
using RegisteredApplication = MediaEngine.Domain.Entities.Application;

namespace MediaEngine.Api.Services.Playback;

/// <summary>
/// The secret a home-network TV uses to play ONE title with no sign-in. It is a Data Protection payload placed in the URL
/// path (so relative HLS links keep working) and is not a bearer token: it reads one asset, over GET and HEAD only.
///
/// What it is bound to: the asset, the HLS package if any, account, profile, device, application, the token FAMILY of the
/// paired phone that asked (not the ten-minute access token), the account, grant and application authorisation versions, the
/// scopes (they must include playback.read) and the expiry. It is not bound to an IP address.
///
/// Every request re-checks all of it (see <see cref="ValidateAsync"/>), so signing the phone out, switching profile, revoking
/// the device, disabling the account or removing the application's playback permission stops the TV within about a minute.
/// Its Data Protection purpose differs from <see cref="HlsAccessGrantService"/>, so the two grant kinds cannot be swapped.
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

    private readonly IDataProtector _protector;
    private readonly IAccountRepository _accounts;
    private readonly IApplicationRepository _applications;
    private readonly IClientAuthorizationRepository _clients;
    private readonly IPermissionRegistry _permissions;
    private readonly TimeProvider _timeProvider;

    public CastAccessGrantService(
        IDataProtectionProvider provider,
        IAccountRepository accounts,
        IApplicationRepository applications,
        IClientAuthorizationRepository clients,
        IPermissionRegistry permissions,
        TimeProvider? timeProvider = null)
    {
        _protector = provider.CreateProtector(ProtectorPurpose);
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
    /// A grant for a paired device only (a signed-in Dashboard session or an Application is refused). The caller's own live
    /// access token is looked up so the grant can be tied to its token family.
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
                accountId,
                profileId,
                applicationId,
                deviceId,
                token.TokenFamilyId,
                authority.AccountAuthorizationVersion,
                authority.GrantAuthorizationVersion,
                authority.ApplicationAuthorizationVersion,
                granted)));
        return new CastAccessGrant(_protector.Protect(payload), expiresAt);
    }

    /// <summary>
    /// Everything that must hold on EVERY request: the grant is intact and unexpired; the device is unrevoked, still on that
    /// profile and the ids match; the token family is live; the account and grant are enabled with the same versions; the
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
        var accountTask = _accounts.GetByIdAsync(binding.AccountId, ct);
        var grantTask = _accounts.GetGrantAsync(binding.AccountId, binding.ProfileId, ct);
        var applicationTask = _applications.GetApplicationAsync(binding.ApplicationId, ct);
        var deviceTask = _clients.GetDeviceAsync(binding.DeviceId, ct);
        var familyTask = _clients.IsTokenFamilyLiveAsync(binding.TokenFamilyId, now, ct);
        await Task.WhenAll(accountTask, grantTask, applicationTask, deviceTask, familyTask).ConfigureAwait(false);
        var account = await accountTask.ConfigureAwait(false);
        var profileGrant = await grantTask.ConfigureAwait(false);
        var application = await applicationTask.ConfigureAwait(false);
        var device = await deviceTask.ConfigureAwait(false);
        if (account?.IsEnabled != true || profileGrant?.IsEnabled != true || application?.IsEnabled != true ||
            device?.IsActive != true || !await familyTask.ConfigureAwait(false) ||
            device.AccountId != binding.AccountId || device.ProfileId != binding.ProfileId ||
            device.ApplicationId != binding.ApplicationId ||
            account.AuthorizationVersion != binding.AccountAuthorizationVersion ||
            profileGrant.AuthorizationVersion != binding.GrantAuthorizationVersion ||
            application.AuthorizationVersion != binding.ApplicationAuthorizationVersion ||
            !await HasLivePlaybackPermissionAsync(application, binding.Scopes, ct).ConfigureAwait(false))
        {
            return null;
        }

        var currentBinding = await _applications.GetApplicationByClientIdAsync(device.ClientId, ct).ConfigureAwait(false);
        if (currentBinding?.Id != binding.ApplicationId || currentBinding.ApplicationType != ApplicationType.UserClient)
        {
            return null;
        }

        var authority = new RequestAuthority(
            PrincipalKind.DelegatedUserClient, true, account.Id, profileGrant.ProfileId, application.Id,
            DeviceId: binding.DeviceId, AccountEnabled: true, GrantEnabled: true, ApplicationEnabled: true,
            AccountAuthorizationVersion: account.AuthorizationVersion,
            GrantAuthorizationVersion: profileGrant.AuthorizationVersion,
            ApplicationAuthorizationVersion: application.AuthorizationVersion,
            AccountIsAdministrator: account.IsAdministrator,
            GrantAdminEnabled: profileGrant.AdminEnabled,
            ApplicationIsAdministrator: application.IsAdministrator);
        return new CastGrantValidation(
            payload.AssetId,
            payload.PackageId,
            DateTimeOffset.FromUnixTimeSeconds(payload.ExpiresAtUnixSeconds),
            authority,
            binding.Scopes);
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

    private sealed record CastBinding(
        Guid AccountId,
        Guid ProfileId,
        Guid ApplicationId,
        Guid DeviceId,
        Guid TokenFamilyId,
        long AccountAuthorizationVersion,
        long GrantAuthorizationVersion,
        long ApplicationAuthorizationVersion,
        IReadOnlyList<string> Scopes);
}

public sealed record CastAccessGrant(string Value, DateTimeOffset ExpiresAt);

public sealed record CastGrantValidation(
    Guid AssetId,
    Guid? PackageId,
    DateTimeOffset ExpiresAt,
    RequestAuthority Authority,
    IReadOnlyList<string> Scopes);
