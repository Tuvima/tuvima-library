using MediaEngine.Api.Endpoints;
using MediaEngine.Api.Security;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Identity.Contracts;
using Microsoft.AspNetCore.Identity;

namespace MediaEngine.Api.Services.Security;

public sealed class UsableAdministratorService(
    IAccountRepository accounts,
    IIdentityRepository identities,
    IAccountExternalLoginService externalLogins,
    UserManager<Account> users,
    AuthenticationProviderConfigurationService providerConfiguration,
    IConfigurationLoader configuration,
    TimeProvider time) : IUsableAdministratorService
{
    public async Task<bool> HasUsableAdministratorSignInAsync(AuthSettings policy, CancellationToken ct)
    {
        foreach (var account in await EnabledAdministratorsAsync(ct).ConfigureAwait(false))
        {
            if (await IsUsableAsync(policy, account, ct).ConfigureAwait(false))
            {
                return true;
            }
        }
        return false;
    }

    public async Task<bool> HasUsableAdministratorSignInAfterAsync(AdministratorSignInLoss loss, CancellationToken ct)
    {
        var policy = providerConfiguration.LoadWithSecrets();
        foreach (var account in await EnabledAdministratorsAsync(ct).ConfigureAwait(false))
        {
            if (loss is { AccountId: { } lostAccount, ProfileId: null } && account.Id == lostAccount)
            {
                continue;
            }

            if (await IsUsableAsync(policy, account, ct, loss).ConfigureAwait(false))
            {
                return true;
            }
        }
        return false;
    }

    public async Task<bool> OnlyThisComputerAdministratorsAsync(CancellationToken ct)
    {
        var administrators = (await EnabledAdministratorsAsync(ct).ConfigureAwait(false)).ToArray();
        return administrators.Any(account => account.IsThisComputerOnly)
            && administrators.All(account => account.IsThisComputerOnly);
    }

    public Task<UsableAdministratorStatus> EvaluateForRemoteAsync(CancellationToken ct) =>
        EvaluateForRemoteAsync(providerConfiguration.LoadWithSecrets(), ct);

    public async Task<UsableAdministratorStatus> EvaluateForRemoteAsync(AuthSettings policy, CancellationToken ct)
    {
        var usable = false;
        var recovery = false;
        foreach (var account in await EnabledAdministratorsAsync(ct).ConfigureAwait(false))
        {
            // A this-computer-only account has no password or passkey, so it never makes remote access ready.
            if (account.IsThisComputerOnly || !await IsUsableAsync(policy, account, ct).ConfigureAwait(false))
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

    private async Task<bool> IsUsableAsync(
        AuthSettings policy, Account account, CancellationToken ct, AdministratorSignInLoss? loss = null)
    {
        // Signs in on this computer without a password, whatever the sign-in policy says, so policy changes can't lock it out.
        // When checking a change, it counts only while it keeps an administrator grant.
        if (account.IsThisComputerOnly && loss is null)
        {
            return true;
        }

        var grants = (await accounts.GetGrantsAsync(account.Id, ct).ConfigureAwait(false))
            .Where(grant => grant.IsEnabled && grant.AdminEnabled && !IsLost(loss, grant))
            .ToArray();
        if (grants.Length == 0)
        {
            return false;
        }

        if (account.IsThisComputerOnly)
        {
            return true;
        }

        if (policy.PasswordSignInEnabled &&
            await identities.GetAccountCredentialAsync(account.Id, AccountCredentialKind.Password, ct)
                .ConfigureAwait(false) is not null)
        {
            return true;
        }

        if (policy.PasskeySignInEnabled &&
            AuthenticationEndpoints.IsCanonicalOriginReady(configuration.LoadNetwork()) &&
            (await users.GetPasskeysAsync(account).ConfigureAwait(false)).Count > 0)
        {
            return true;
        }

        if (policy.Mode is "Optional" or "Required" && policy.ExternalSignInEnabled)
        {
            var linked = await externalLogins.GetByAccountAsync(account.Id, ct).ConfigureAwait(false);
            if (linked.Any(login => AuthenticationEndpoints.IsConfiguredProvider(policy, configuration.LoadNetwork(), login.Provider, login.Issuer)))
            {
                return true;
            }
        }
        return false;
    }

    // A grant is lost when the change names exactly that grant, or every grant to its profile.
    private static bool IsLost(AdministratorSignInLoss? loss, AccountProfileGrant grant) => loss is { } change
        && change.ProfileId == grant.ProfileId
        && (change.AccountId is null || change.AccountId == grant.AccountId);
}
