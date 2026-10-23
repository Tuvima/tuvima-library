using System.Security.Cryptography;
using System.Text.Json;
using MediaEngine.Api.Security;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using Microsoft.AspNetCore.DataProtection;
using RegisteredApplication = MediaEngine.Domain.Entities.Application;

namespace MediaEngine.Api.Services.Playback;

public sealed class HlsAccessGrantService
{
    private readonly IDataProtector _protector;
    private readonly IConfigurationLoader _configuration;
    private readonly IAccountRepository _accounts;
    private readonly IApplicationRepository _applications;
    private readonly IIdentityRepository _identities;
    private readonly IClientAuthorizationRepository _clients;
    private readonly IPermissionRegistry _permissions;
    private readonly TimeProvider _timeProvider;

    public HlsAccessGrantService(
        IDataProtectionProvider provider,
        IConfigurationLoader configuration,
        IAccountRepository accounts,
        IApplicationRepository applications,
        IIdentityRepository identities,
        IClientAuthorizationRepository clients,
        IPermissionRegistry permissions,
        TimeProvider? timeProvider = null)
    {
        _protector = provider.CreateProtector("Tuvima.Library.Playback.HlsAccess.v2");
        _configuration = configuration;
        _accounts = accounts;
        _applications = applications;
        _identities = identities;
        _clients = clients;
        _permissions = permissions;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public HlsAccessGrant Create(
        Guid assetId,
        Guid packageId,
        RequestAuthority authority,
        Guid? tokenId,
        IReadOnlyCollection<string> scopes)
    {
        var binding = CreateBinding(authority, tokenId, scopes);
        var minutes = Math.Clamp(
            _configuration.LoadTranscoding().AdaptiveHls.AccessLifetimeMinutes,
            15,
            720);
        var expiresAt = _timeProvider.GetUtcNow().AddMinutes(minutes);
        var payload = JsonSerializer.Serialize(new HlsGrantPayload(
            assetId,
            packageId,
            expiresAt.ToUnixTimeSeconds(),
            binding));
        return new HlsAccessGrant(_protector.Protect(payload), expiresAt);
    }

    public async ValueTask<HlsGrantValidation?> ValidateAsync(
        string? token,
        Guid packageId,
        CancellationToken ct = default)
    {
        var payload = Unprotect(token, packageId);
        if (payload is null)
        {
            return null;
        }

        var authority = await ValidateBindingAsync(payload.Binding, ct).ConfigureAwait(false);
        return authority is null
            ? null
            : new HlsGrantValidation(payload.AssetId, authority, payload.Binding.Scopes);
    }

    private HlsGrantPayload? Unprotect(string? token, Guid packageId)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 2048)
        {
            return null;
        }

        try
        {
            var payload = JsonSerializer.Deserialize<HlsGrantPayload>(_protector.Unprotect(token));
            return payload is not null &&
                   payload.PackageId == packageId &&
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

    private async ValueTask<RequestAuthority?> ValidateBindingAsync(
        HlsAuthorityBinding binding,
        CancellationToken ct)
    {
        var now = _timeProvider.GetUtcNow();
        if (binding.PrincipalKind == PrincipalKind.Human)
        {
            if (binding.AccountId is not { } accountId || binding.ProfileId is not { } profileId ||
                binding.SessionId is not { } sessionId)
            {
                return null;
            }

            var account = await _accounts.GetByIdAsync(accountId, ct).ConfigureAwait(false);
            var profile = await _accounts.GetActiveProfileAccessAsync(accountId, profileId, ct).ConfigureAwait(false);
            var session = await _identities.GetSessionByIdAsync(sessionId, ct).ConfigureAwait(false);
            if (account?.IsEnabled != true || profile is null || session is null ||
                !session.IsActive(now) || session.AccountId != accountId || session.ActiveProfileId != profileId ||
                account.AuthorizationVersion != binding.AccountAuthorizationVersion ||
                profile.AuthorizationVersion != binding.ProfileAuthorizationVersion)
            {
                return null;
            }

            return HumanAuthority(account, profile, sessionId);
        }

        if (binding.PrincipalKind == PrincipalKind.DelegatedUserClient)
        {
            return await ValidateDelegatedBindingAsync(binding, now, ct).ConfigureAwait(false);
        }

        if (binding.PrincipalKind != PrincipalKind.ServiceApplication || binding.ApplicationId is not { } applicationId)
        {
            return null;
        }

        var application = await _applications.GetApplicationAsync(applicationId, ct).ConfigureAwait(false);
        if (application?.IsEnabled != true ||
            application.AuthorizationVersion != binding.ApplicationAuthorizationVersion ||
            !await HasLivePlaybackPermissionAsync(application, binding.Scopes, ct).ConfigureAwait(false))
        {
            return null;
        }

        return ApplicationAuthority(application);
    }

    private async ValueTask<RequestAuthority?> ValidateDelegatedBindingAsync(
        HlsAuthorityBinding binding,
        DateTimeOffset now,
        CancellationToken ct)
    {
        if (binding.AccountId is not { } accountId || binding.ProfileId is not { } profileId ||
            binding.ApplicationId is not { } applicationId || binding.DeviceId is not { } deviceId ||
            binding.TokenId is not { } tokenId)
        {
            return null;
        }

        var accountTask = _accounts.GetByIdAsync(accountId, ct);
        var profileTask = _accounts.GetActiveProfileAccessAsync(accountId, profileId, ct);
        var applicationTask = _applications.GetApplicationAsync(applicationId, ct);
        var deviceTask = _clients.GetDeviceAsync(deviceId, ct);
        var tokenTask = _clients.GetTokenByIdAsync(tokenId, ct);
        await Task.WhenAll(accountTask, profileTask, applicationTask, deviceTask, tokenTask).ConfigureAwait(false);
        var account = await accountTask.ConfigureAwait(false);
        var profile = await profileTask.ConfigureAwait(false);
        var application = await applicationTask.ConfigureAwait(false);
        var device = await deviceTask.ConfigureAwait(false);
        var clientToken = await tokenTask.ConfigureAwait(false);
        if (account?.IsEnabled != true || profile is null || application?.IsEnabled != true ||
            device?.IsActive != true || clientToken is null || clientToken.RevokedAt is not null ||
            clientToken.ExpiresAt <= now || !string.Equals(clientToken.Kind, "access", StringComparison.Ordinal) ||
            device.AccountId != accountId || device.ProfileId != profileId || device.ApplicationId != applicationId ||
            clientToken.AccountId != accountId || clientToken.ProfileId != profileId ||
            clientToken.ApplicationId != applicationId || clientToken.DeviceId != deviceId ||
            account.AuthorizationVersion != binding.AccountAuthorizationVersion ||
            profile.AuthorizationVersion != binding.ProfileAuthorizationVersion ||
            application.AuthorizationVersion != binding.ApplicationAuthorizationVersion ||
            !ScopesEqual(binding.Scopes, ClientAuthorizationService.SplitScopes(clientToken.Scopes)) ||
            !await HasLivePlaybackPermissionAsync(application, binding.Scopes, ct).ConfigureAwait(false))
        {
            return null;
        }

        var currentBinding = await _applications.GetApplicationByClientIdAsync(device.ClientId, ct).ConfigureAwait(false);
        return currentBinding?.Id == applicationId && currentBinding.ApplicationType == ApplicationType.UserClient
            ? DelegatedAuthority(account, profile, application, deviceId)
            : null;
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

    private static HlsAuthorityBinding CreateBinding(
        RequestAuthority authority,
        Guid? tokenId,
        IReadOnlyCollection<string> scopes)
    {
        if (AuthorityValidity.Validate(authority) is not null ||
            authority.PrincipalKind is not (PrincipalKind.Human or PrincipalKind.DelegatedUserClient or PrincipalKind.ServiceApplication))
        {
            throw new UnauthorizedAccessException("A live human or Application authority is required for HLS access.");
        }

        if (authority.PrincipalKind == PrincipalKind.Human && authority.SessionId is null)
        {
            throw new UnauthorizedAccessException("A revocable session is required for HLS access.");
        }

        if (authority.PrincipalKind == PrincipalKind.DelegatedUserClient && tokenId is null)
        {
            throw new UnauthorizedAccessException("A revocable client token is required for HLS access.");
        }

        return new HlsAuthorityBinding(
            authority.PrincipalKind,
            authority.AccountId,
            authority.ActiveProfileId,
            authority.ApplicationId,
            authority.SessionId,
            authority.DeviceId,
            tokenId,
            authority.AccountAuthorizationVersion,
            authority.ProfileAuthorizationVersion,
            authority.ApplicationAuthorizationVersion,
            scopes.Where(scope => !string.IsNullOrWhiteSpace(scope))
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray());
    }

    private static RequestAuthority HumanAuthority(Account account, ActiveProfileAccess profile, Guid sessionId) =>
        new RequestAuthority(PrincipalKind.Human, true, account.Id, profile.ProfileId, SessionId: sessionId,
            AccountEnabled: true,
            AccountAuthorizationVersion: account.AuthorizationVersion).WithActiveProfile(profile);

    private static RequestAuthority DelegatedAuthority(
        Account account,
        ActiveProfileAccess profile,
        RegisteredApplication application,
        Guid deviceId) =>
        new RequestAuthority(PrincipalKind.DelegatedUserClient, true, account.Id, profile.ProfileId, application.Id,
            DeviceId: deviceId, AccountEnabled: true, ApplicationEnabled: true,
            AccountAuthorizationVersion: account.AuthorizationVersion,
            ApplicationAuthorizationVersion: application.AuthorizationVersion,
            ApplicationIsAdministrator: application.IsAdministrator).WithActiveProfile(profile);

    private static RequestAuthority ApplicationAuthority(RegisteredApplication application) =>
        new(PrincipalKind.ServiceApplication, true, ApplicationId: application.Id,
            ApplicationEnabled: true,
            ApplicationAuthorizationVersion: application.AuthorizationVersion,
            ApplicationIsAdministrator: application.IsAdministrator);

    private static bool ScopesEqual(IReadOnlyList<string> left, IReadOnlyList<string> right) =>
        left.Count == right.Count && left.ToHashSet(StringComparer.Ordinal).SetEquals(right);

    private sealed record HlsGrantPayload(
        Guid AssetId,
        Guid PackageId,
        long ExpiresAtUnixSeconds,
        HlsAuthorityBinding Binding);

    private sealed record HlsAuthorityBinding(
        PrincipalKind PrincipalKind,
        Guid? AccountId,
        Guid? ProfileId,
        Guid? ApplicationId,
        Guid? SessionId,
        Guid? DeviceId,
        Guid? TokenId,
        long AccountAuthorizationVersion,
        long ProfileAuthorizationVersion,
        long ApplicationAuthorizationVersion,
        IReadOnlyList<string> Scopes);
}

public sealed record HlsAccessGrant(string Value, DateTimeOffset ExpiresAt);
public sealed record HlsGrantValidation(
    Guid AssetId,
    RequestAuthority Authority,
    IReadOnlyList<string> Scopes);
