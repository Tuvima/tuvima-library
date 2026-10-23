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
        await accounts.GetEnabledServerAdministratorsAsync(ct).ConfigureAwait(false);

    private async Task<bool> IsUsableAsync(
        AuthSettings policy, Account account, CancellationToken ct, AdministratorSignInLoss? loss = null)
    {
        // Signs in on this computer without a password, whatever the sign-in policy says, so policy changes can't lock it out.
        // When checking an account change, it counts only while it keeps its administrator profile. That is deliberately
        // stricter than the policy-change check above (HasUsableAdministratorSignInAsync, which counts it unconditionally):
        // a policy change cannot take the profile's role away, but an account change can, and a user whose profile is no
        // longer a server administrator is not an administrator.
        if (account.IsThisComputerOnly && loss is null)
        {
            return true;
        }

        // A server administrator is a user attached to a server administrator profile (the roles live on the profile); the user
        // is not an administrator while that profile is the one being lost.
        if (IsLost(loss, account))
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

    // The role is lost when the change names the profile the user opens.
    private static bool IsLost(AdministratorSignInLoss? loss, Account account) => loss is { } change
        && change.ProfileId is { } lostProfile
        && lostProfile == account.ProfileId;
}
