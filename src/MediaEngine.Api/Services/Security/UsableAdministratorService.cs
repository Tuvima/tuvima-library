using MediaEngine.Api.Endpoints;
using MediaEngine.Api.Security;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Identity.Contracts;
using Microsoft.AspNetCore.Identity;

namespace MediaEngine.Api.Services.Security;

/// <summary>What remote access needs to know about administrator sign-in.</summary>
public sealed record UsableAdministratorStatus(
    bool HasUsableAdministrator,
    bool HasRecoveryCodes,
    bool LocalhostBypassDisabled);

/// <summary>Answers "can an administrator actually sign in right now?".</summary>
public interface IUsableAdministratorService
{
    /// <summary>
    /// True when at least one enabled administrator has a working sign-in method under <paramref name="policy"/>.
    /// Used by Settings to stop changes that would lock every administrator out.
    /// </summary>
    Task<bool> HasUsableAdministratorSignInAsync(AuthSettings policy, CancellationToken ct);

    /// <summary>
    /// Remote-access view: local-only accounts do not count, and an administrator must also have saved recovery codes.
    /// </summary>
    Task<UsableAdministratorStatus> EvaluateForRemoteAsync(CancellationToken ct);
}

public sealed class UsableAdministratorService(
    IAccountRepository accounts,
    IIdentityRepository identities,
    IAccountExternalLoginService externalLogins,
    UserManager<Account> users,
    AuthenticationProviderConfigurationService providerConfiguration,
    TimeProvider time) : IUsableAdministratorService
{
    public async Task<bool> HasUsableAdministratorSignInAsync(AuthSettings policy, CancellationToken ct)
    {
        foreach (var account in await EnabledAdministratorsAsync(ct).ConfigureAwait(false))
        {
            if (await IsUsableAsync(policy, account, includeLocalOnly: true, ct).ConfigureAwait(false))
            {
                return true;
            }
        }
        return false;
    }

    public Task<UsableAdministratorStatus> EvaluateForRemoteAsync(CancellationToken ct) =>
        EvaluateForRemoteAsync(providerConfiguration.LoadWithSecrets(), ct);

    public async Task<UsableAdministratorStatus> EvaluateForRemoteAsync(AuthSettings policy, CancellationToken ct)
    {
        var usable = false;
        var recovery = false;
        foreach (var account in await EnabledAdministratorsAsync(ct).ConfigureAwait(false))
        {
            if (!await IsUsableAsync(policy, account, includeLocalOnly: false, ct).ConfigureAwait(false))
            {
                continue;
            }

            usable = true;
            if (await identities.CountActiveRecoveryCodesAsync(account.Id, time.GetUtcNow(), ct).ConfigureAwait(false) > 0)
            {
                recovery = true;
                break;
            }
        }
        return new UsableAdministratorStatus(usable, recovery, !policy.LocalhostBypass);
    }

    private async Task<IEnumerable<Account>> EnabledAdministratorsAsync(CancellationToken ct) =>
        (await accounts.GetAllAsync(ct).ConfigureAwait(false))
            .Where(account => account.IsEnabled && account.IsAdministrator);

    private async Task<bool> IsUsableAsync(AuthSettings policy, Account account, bool includeLocalOnly, CancellationToken ct)
    {
        var grants = (await accounts.GetGrantsAsync(account.Id, ct).ConfigureAwait(false))
            .Where(grant => grant.IsEnabled && grant.AdminEnabled)
            .ToArray();
        if (grants.Length == 0)
        {
            return false;
        }

        if (account.IsLocalOnly)
        {
            if (!includeLocalOnly || !policy.AllowLocalOnlyAccounts)
            {
                return false;
            }

            foreach (var grant in grants)
            {
                if (await accounts.GetLocalOnlyAccountIdForProfileAsync(grant.ProfileId, ct)
                        .ConfigureAwait(false) == account.Id)
                {
                    return true;
                }
            }
            return false;
        }

        var localOnlyMode = policy.Mode.Equals("DisabledLocalOnly", StringComparison.OrdinalIgnoreCase);
        if (!localOnlyMode && policy.PasswordSignInEnabled &&
            await identities.GetAccountCredentialAsync(account.Id, AccountCredentialKind.Password, ct)
                .ConfigureAwait(false) is not null)
        {
            return true;
        }

        if (!localOnlyMode && policy.PasskeySignInEnabled &&
            AuthenticationEndpoints.IsCanonicalOriginReady(policy) &&
            (await users.GetPasskeysAsync(account).ConfigureAwait(false)).Count > 0)
        {
            return true;
        }

        if (policy.Mode is "Optional" or "Required" && policy.ExternalSignInEnabled)
        {
            var linked = await externalLogins.GetByAccountAsync(account.Id, ct).ConfigureAwait(false);
            if (linked.Any(login => AuthenticationEndpoints.IsConfiguredProvider(policy, login.Provider, login.Issuer)))
            {
                return true;
            }
        }
        return false;
    }
}
