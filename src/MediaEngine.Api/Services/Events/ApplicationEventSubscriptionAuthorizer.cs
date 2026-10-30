using MediaEngine.Api.Security;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Events;

namespace MediaEngine.Api.Services.Events;

public sealed class ApplicationEventSubscriptionAuthorizer(
    IAuthorizationEvaluator authorization,
    IAccountRepository accounts,
    IProfileAccessRepository profileAccess,
    IApplicationRepository applications,
    IClientAuthorizationRepository clients,
    IPermissionRegistry permissions,
    ApplicationEventRegistry registry,
    IApplicationEventDeliveryAuthorizer serviceAuthorization,
    TimeProvider timeProvider,
    LibraryVisibilityRule libraryVisibility,
    IConfigurationLoader configuration)
{
    internal async ValueTask<bool> CanDeliverServiceConnectionAsync(
        ApplicationEventSubscriber subscriber,
        StoredApplicationEvent value,
        CancellationToken ct = default)
    {
        if (subscriber.CredentialId is not { } credentialId)
        {
            return false;
        }

        var credentials = await applications.GetApplicationCredentialsAsync(subscriber.ApplicationId, ct).ConfigureAwait(false);
        var credential = credentials.SingleOrDefault(value => value.Id == credentialId);
        return credential?.IsActive(timeProvider.GetUtcNow()) == true &&
            await serviceAuthorization.CanDeliverAsync(subscriber.ApplicationId, value, ct).ConfigureAwait(false);
    }

    internal async ValueTask<bool> CanSubscribeAsync(
        RequestAuthority authority,
        IReadOnlySet<ApplicationPermissionId> consent,
        IReadOnlyList<string> eventTypes,
        IReadOnlySet<Guid> libraryIds,
        CancellationToken ct)
    {
        if (!authority.HasApplicationContext || eventTypes.Count is < 1 or > 32)
        {
            return false;
        }

        // A revoked device cannot subscribe, even on a connection it opened before the revoke.
        if (authority.DeviceId is { } deviceId &&
            (await clients.GetDeviceAsync(deviceId, ct).ConfigureAwait(false))?.IsActive != true)
        {
            return false;
        }

        if (!(await authorization.EvaluateAsync(authority,
                new(ApplicationPermissionIds.EventsSubscribe), null, ct).ConfigureAwait(false)).IsAllowed)
        {
            return false;
        }

        foreach (var type in eventTypes.Distinct(StringComparer.Ordinal))
        {
            if (!registry.TryGet(type, out var definition) ||
                !(await authorization.EvaluateAsync(authority, new(definition.ReadPermission), null, ct).ConfigureAwait(false)).IsAllowed)
            {
                return false;
            }

            if (definition.IsLibraryScoped &&
                !(await authorization.EvaluateAsync(authority, new(ApplicationPermissionIds.LibraryRead), null, ct).ConfigureAwait(false)).IsAllowed)
            {
                return false;
            }
        }
        if (authority.PrincipalKind == PrincipalKind.DelegatedUserClient)
        {
            if (libraryIds.Count == 0 || authority.AccountId is null)
            {
                return false;
            }

            // Access is the acting profile's, never the sign-in's.
            if (authority.ActiveProfileId is not { } actingProfile)
            {
                return false;
            }

            // A subscription names libraries the person may see: each must be a configured library, in an area the profile has
            // (administrators have them all), and not hidden. An unknown library or an area that can't be worked out fails closed.
            var features = await profileAccess.GetAreasAsync(actingProfile, ct).ConfigureAwait(false);
            var configured = configuration.LoadLibraries().Libraries;
            foreach (var id in libraryIds)
            {
                var library = configured.FirstOrDefault(item => string.Equals(item.Id, id.ToString("D"), StringComparison.OrdinalIgnoreCase));
                var areaName = library?.Area?.ToLowerInvariant();
                var areas = AccountFeatureId.All.Where(feature => feature.Value == areaName).ToList();
                if (library is null || areas.Count != 1)
                {
                    return false;
                }

                var area = areas[0];
                if (!await libraryVisibility.MaySeeAsync(
                        actingProfile, authority.IsEffectiveAdministrator, id,
                        authority.IsEffectiveAdministrator || features.Contains(area), ct).ConfigureAwait(false))
                {
                    return false;
                }
            }

            if (eventTypes.Any(type => registry.TryGet(type, out var definition) && definition.IsLibraryScoped))
            {
                if (!features.Contains(AccountFeatureId.Read) && !features.Contains(AccountFeatureId.Watch) &&
                    !features.Contains(AccountFeatureId.Listen))
                {
                    return false;
                }
            }
        }
        return true;
    }

    internal async ValueTask<bool> CanDeliverDelegatedAsync(
        ApplicationEventSubscriber subscriber,
        StoredApplicationEvent value,
        CancellationToken ct = default)
    {
        var authority = subscriber.Authority;
        if (authority.PrincipalKind != PrincipalKind.DelegatedUserClient || authority.AccountId is null ||
            authority.ActiveProfileId is null || authority.DeviceId is null || subscriber.TokenId is null ||
            !registry.TryGet(value.EventType, out var definition))
        {
            return false;
        }

        var accountId = authority.AccountId.Value;
        var profileId = authority.ActiveProfileId.Value;
        var deviceId = authority.DeviceId.Value;
        var tokenId = subscriber.TokenId.Value;
        var accountTask = accounts.GetByIdAsync(accountId, ct);
        var profileTask = accounts.GetActiveProfileAccessAsync(accountId, profileId, ct);
        var appTask = applications.GetApplicationAsync(subscriber.ApplicationId, ct);
        var deviceTask = clients.GetDeviceAsync(deviceId, ct);
        var tokenTask = clients.GetTokenByIdAsync(tokenId, ct);
        await Task.WhenAll(accountTask, profileTask, appTask, deviceTask, tokenTask).ConfigureAwait(false);
        var account = await accountTask.ConfigureAwait(false);
        var profile = await profileTask.ConfigureAwait(false);
        var app = await appTask.ConfigureAwait(false);
        var device = await deviceTask.ConfigureAwait(false);
        var token = await tokenTask.ConfigureAwait(false);
        var now = timeProvider.GetUtcNow();
        if (account?.IsEnabled != true || profile is null || app?.IsEnabled != true ||
            app.ApplicationType != ApplicationType.UserClient || device?.IsActive != true || token is null ||
            token.RevokedAt is not null || token.ExpiresAt <= now ||
            !string.Equals(token.Kind, "access", StringComparison.Ordinal) ||
            device.ApplicationId != app.Id || device.AccountId != accountId || device.ProfileId != profileId ||
            token.ApplicationId != app.Id || token.AccountId != accountId || token.ProfileId != profileId ||
            token.DeviceId != deviceId ||
            account.AuthorizationVersion != authority.AccountAuthorizationVersion ||
            profile.AuthorizationVersion != authority.ProfileAuthorizationVersion ||
            app.AuthorizationVersion != authority.ApplicationAuthorizationVersion ||
            token.AccountAuthorizationVersion != authority.AccountAuthorizationVersion ||
            token.GrantAuthorizationVersion != authority.ProfileAuthorizationVersion ||
            token.ApplicationAuthorizationVersion != authority.ApplicationAuthorizationVersion ||
            !ScopesEqual(subscriber.Consent, ClientAuthorizationService.SplitScopes(token.Scopes)))
        {
            return false;
        }

        var currentBinding = await applications.GetApplicationByClientIdAsync(device.ClientId, ct).ConfigureAwait(false);
        if (currentBinding?.Id != app.Id || currentBinding.ApplicationType != ApplicationType.UserClient)
        {
            return false;
        }

        var required = new HashSet<ApplicationPermissionId> { ApplicationPermissionIds.EventsSubscribe, definition.ReadPermission };
        if (definition.IsLibraryScoped)
        {
            if (value.Subject.LibraryId is not { } libraryId ||
                !await libraryVisibility.MaySeeAsync(
                    profile.ProfileId, authority.IsEffectiveAdministrator, libraryId, true, ct).ConfigureAwait(false))
            {
                return false;
            }

            if (value.Subject.FeatureId is not { } featureId ||
                !(await profileAccess.GetAreasAsync(profile.ProfileId, ct).ConfigureAwait(false)).Contains(featureId))
            {
                return false;
            }

            required.Add(ApplicationPermissionIds.LibraryRead);
        }
        if (definition.IsProfileScoped && value.Subject.ProfileId != authority.ActiveProfileId)
        {
            return false;
        }

        var granted = app.IsAdministrator ? null : await applications.GetApplicationPermissionsAsync(app.Id, ct).ConfigureAwait(false);
        return required.All(permission =>
            subscriber.Consent.Contains(permission) &&
            permissions.TryGet(permission, out var registered) && registered.IsAvailable &&
            registered.ApplicationTypes.Contains(app.ApplicationType) &&
            (app.IsAdministrator || granted!.Contains(permission)));
    }

    private static bool ScopesEqual(
        IReadOnlySet<ApplicationPermissionId> consent,
        IReadOnlyList<string> tokenScopes) =>
        consent.Count == tokenScopes.Count &&
        consent.Select(permission => permission.Value).ToHashSet(StringComparer.Ordinal)
            .SetEquals(tokenScopes);
}
